using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Host;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

/// <summary>server.json next to the program.</summary>
sealed class ServerConfig
{
    public string Name { get; set; } = Environment.MachineName;
    /// <summary>Address to listen on: "0.0.0.0" (all, e.g. Tailscale) or one IP.</summary>
    public string Bind { get; set; } = "0.0.0.0";
    public int Port { get; set; } = 6100;
    public string Token { get; set; } = "";
    public int MaxSlots { get; set; } = 12;

    public static ServerConfig LoadOrCreate(string path, out bool created)
    {
        created = false;
        ServerConfig config = File.Exists(path) ? JsonConvert.DeserializeObject<ServerConfig>(File.ReadAllText(path)) ?? new() : new();
        if (string.IsNullOrWhiteSpace(config.Token))
        {
            // 20 random characters, easy to type into the phone.
            const string chars = "abcdefghjkmnpqrstuvwxyz23456789";
            config.Token = new string(Enumerable.Range(0, 20).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray());
            created = true;
        }
        File.WriteAllText(path, JsonConvert.SerializeObject(config, Formatting.Indented));
        return config;
    }
}

/// <summary>
/// Skua Server: runs the bots of one machine (a VPS) for Skua Mobile. One port; every request
/// carries the token and a slot, and is passed to that slot's bot process (started on demand,
/// headless). Bots themselves only listen on 127.0.0.1.
///   phone → server : {"id":1,"token":"…","slot":2,"cmd":"configure","profile":{…}}
///   server → phone : {"id":1,"ok":true,"data":…}
/// </summary>
sealed class SkuaServer : IDisposable
{
    private static readonly HashSet<string> PollOnly = new() { "ping", "status", "logs" };

    private readonly ServerConfig _config;
    private readonly Func<int, ProcessStartInfo> _botStart;
    private readonly string _logDir;
    private readonly ConcurrentDictionary<int, Process> _bots = new();
    private readonly ConcurrentDictionary<int, BotClient> _clients = new();
    private readonly ConcurrentDictionary<int, SemaphoreSlim> _spawnLocks = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    public SkuaServer(ServerConfig config, Func<int, ProcessStartInfo> botStart, string logDir)
    {
        _config = config;
        _botStart = botStart;
        _logDir = logDir;
        Directory.CreateDirectory(logDir);
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Parse(_config.Bind), _config.Port);
        _listener.Start();
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
                catch { break; }
                _ = Task.Run(() => ServeAsync(client));
            }
        });
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            string peer = client.Client.RemoteEndPoint?.ToString() ?? "?";
            NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, new UTF8Encoding(false));
            using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            SemaphoreSlim writeLock = new(1, 1);
            bool authed = false;
            while (!_cts.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); }
                catch { break; }
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                _ = Task.Run(async () =>
                {
                    JObject reply = await HandleAsync(line, peer, () => authed, () => authed = true);
                    await writeLock.WaitAsync();
                    try { await writer.WriteLineAsync(reply.ToString(Formatting.None)); }
                    catch { }
                    finally { writeLock.Release(); }
                });
            }
        }
    }

    private async Task<JObject> HandleAsync(string line, string peer, Func<bool> authed, Action markAuthed)
    {
        JObject reply = new();
        try
        {
            JObject req = JObject.Parse(line);
            reply["id"] = req["id"];
            string token = req.Value<string>("token") ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(_config.Token)))
            {
                await Task.Delay(1000);
                Log($"rejected a request from {peer} (wrong token)");
                throw new UnauthorizedAccessException("Wrong server token.");
            }
            if (!authed())
            {
                markAuthed();
                Log($"manager connected from {peer}");
            }

            string cmd = req.Value<string>("cmd") ?? "";
            JToken? data = cmd.StartsWith("server.", StringComparison.Ordinal)
                ? ServerCommand(cmd, req)
                : await ForwardAsync(cmd, req);
            reply["ok"] = true;
            if (data is not null)
                reply["data"] = data;
        }
        catch (Exception e)
        {
            reply["ok"] = false;
            reply["error"] = e.GetBaseException().Message;
        }
        return reply;
    }

    private JToken? ServerCommand(string cmd, JObject req)
    {
        switch (cmd)
        {
            case "server.info":
                return new JObject
                {
                    ["name"] = _config.Name,
                    ["version"] = typeof(SkuaHost).Assembly.GetName().Version?.ToString() ?? "?",
                    ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                    ["cpus"] = Environment.ProcessorCount,
                    ["memoryMb"] = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024),
                    ["maxSlots"] = _config.MaxSlots,
                    ["running"] = new JArray(_bots.Where(kv => !kv.Value.HasExited).Select(kv => (object)kv.Key).OrderBy(s => s).ToArray()),
                };
            case "server.stopBot":
            {
                int slot = req.Value<int?>("slot") ?? -1;
                if (_bots.TryRemove(slot, out Process? process))
                {
                    try { process.Kill(true); } catch { }
                    Log($"bot {slot} stopped");
                }
                return null;
            }
            default:
                throw new InvalidOperationException($"Unknown command: {cmd}");
        }
    }

    private async Task<JToken?> ForwardAsync(string cmd, JObject req)
    {
        int slot = req.Value<int?>("slot") ?? -1;
        if (slot < 0 || slot > _config.MaxSlots)
            throw new InvalidOperationException($"This server runs slots 0-{_config.MaxSlots}.");

        BotClient client = _clients.GetOrAdd(slot, s => new BotClient(s));
        if (!Running(slot) || !await client.PingAsync(1500))
        {
            if (PollOnly.Contains(cmd))
                throw new InvalidOperationException("Bot isn't running.");
            await SpawnAsync(slot, client);
        }

        JObject args = (JObject)req.DeepClone();
        foreach (string key in new[] { "id", "token", "slot", "cmd" })
            args.Remove(key);
        JToken? data = await client.SendAsync(cmd, args, 30 * 60 * 1000);
        if (cmd == "shutdown")
            _bots.TryRemove(slot, out _);
        return data;
    }

    private bool Running(int slot) => _bots.TryGetValue(slot, out Process? p) && !p.HasExited;

    private async Task SpawnAsync(int slot, BotClient client)
    {
        SemaphoreSlim gate = _spawnLocks.GetOrAdd(slot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (Running(slot) && await client.PingAsync(1500))
                return;
            if (_bots.TryRemove(slot, out Process? old))
                try { old.Kill(true); } catch { }

            ProcessStartInfo info = _botStart(slot);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the bot.");
            StreamWriter log = new(Path.Combine(_logDir, $"bot{slot}.log"), append: true) { AutoFlush = true };
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _bots[slot] = process;
            Log($"bot {slot} starting (pid {process.Id})");

            Stopwatch sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(120))
            {
                if (process.HasExited)
                    throw new InvalidOperationException($"Bot {slot} exited at start (see logs/bot{slot}.log).");
                if (await client.PingAsync(1500))
                {
                    Log($"bot {slot} ready in {sw.Elapsed.TotalSeconds:F0} s");
                    return;
                }
                await Task.Delay(500);
            }
            throw new TimeoutException($"Bot {slot} didn't start (see logs/bot{slot}.log).");
        }
        finally
        {
            gate.Release();
        }
    }

    private static void Log(string text) => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {text}");

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
        foreach (var (_, process) in _bots)
            try { process.Kill(true); } catch { }
    }
}
