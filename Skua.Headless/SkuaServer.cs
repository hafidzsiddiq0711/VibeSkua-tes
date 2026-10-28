using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Host;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
    /// <summary>
    /// The bots' own Skua folder (scripts, options, settings), next to the program by default.
    /// "" shares VibeSkua desktop's %APPDATA%\VibeSkua, whose open files and locks get in the way
    /// when both run on one PC.
    /// </summary>
    public string DataDir { get; set; } = "data";

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
    private readonly ConcurrentDictionary<int, StreamWriter> _logs = new();
    /// <summary>Account each bot was configured with.</summary>
    private readonly ConcurrentDictionary<int, string> _users = new();
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;

    /// <summary>Bots start once this is done (first download of the scripts).</summary>
    public Task Ready { get; set; } = Task.CompletedTask;

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
            case "server.botLog":
            {
                // The bot's whole output (game included), for when its own log is gone or empty.
                int slot = req.Value<int?>("slot") ?? -1;
                string? file = slot >= 0 ? Path.Combine(_logDir, $"bot{slot}.log") : null;
                if (file is null || !File.Exists(file))
                    file = new DirectoryInfo(_logDir).GetFiles("bot*.log").OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault()?.FullName;
                if (file is null)
                    return "(no bot has run yet)";
                using FileStream fs = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (fs.Length > 256 * 1024)
                    fs.Seek(-256 * 1024, SeekOrigin.End);
                using StreamReader reader = new(fs, new UTF8Encoding(false));
                // Stack traces of the game's own script errors are long and say little; keep their first line.
                List<string> lines = reader.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r'))
                    .Where(l => !l.TrimStart().StartsWith("at ", StringComparison.Ordinal)).ToList();
                return $"{Path.GetFileName(file)}\n" + string.Join("\n", lines.TakeLast(400));
            }
            case "server.stopBot":
            {
                int slot = req.Value<int?>("slot") ?? -1;
                StopBot(slot, $"bot {slot} stopped");
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
        if (cmd == "configure" && req["profile"]?.Value<string>("Username") is { Length: > 0 } user)
            OneBotPerAccount(slot, user);
        if (PollOnly.Contains(cmd))
        {
            // A busy bot (compiling a script on a loaded PC) answers slowly: it's only "not running"
            // when its process is gone.
            if (!Running(slot))
                throw new InvalidOperationException("Bot isn't running.");
        }
        else if (!Running(slot))
        {
            // Never replace a living bot because it answers slowly; "Stop bot" ends a stuck one.
            await SpawnAsync(slot, client);
        }

        JObject args = (JObject)req.DeepClone();
        foreach (string key in new[] { "id", "token", "slot", "cmd" })
            args.Remove(key);
        JToken? data = await client.SendAsync(cmd, args, 30 * 60 * 1000);
        if (cmd == "shutdown")
        {
            _bots.TryRemove(slot, out _);
            _users.TryRemove(slot, out _);
        }
        return data;
    }

    /// <summary>
    /// An account plays in one bot only: two logged in as the same account kick each other out
    /// forever. A bot that gets another account is started fresh (no session left from the old one).
    /// </summary>
    private void OneBotPerAccount(int slot, string user)
    {
        foreach (var (other, otherUser) in _users)
        {
            if (other != slot && string.Equals(otherUser, user, StringComparison.OrdinalIgnoreCase))
            {
                StopBot(other, $"{user} moved from bot {other} to bot {slot}");
            }
        }
        if (_users.TryGetValue(slot, out string? current) && !string.Equals(current, user, StringComparison.OrdinalIgnoreCase))
            StopBot(slot, $"bot {slot} switches from {current} to {user}: restarting it");
        _users[slot] = user;
    }

    private void StopBot(int slot, string why)
    {
        _users.TryRemove(slot, out _);
        if (_bots.TryRemove(slot, out Process? process))
        {
            try { process.Kill(true); process.WaitForExit(5000); } catch { }
            Log(why);
        }
    }

    private bool Running(int slot) => _bots.TryGetValue(slot, out Process? p) && !p.HasExited;

    private async Task SpawnAsync(int slot, BotClient client)
    {
        await Ready;
        SemaphoreSlim gate = _spawnLocks.GetOrAdd(slot, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (Running(slot))
                return;
            if (_bots.TryRemove(slot, out Process? old))
                try { old.Kill(true); } catch { }

            ProcessStartInfo info = _botStart(slot);
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.RedirectStandardOutput = true;
            info.RedirectStandardError = true;
            Process process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the bot.");
            // Bots (and their games) end with the server, even when its window is just closed.
            BotJob.Add(process);
            // One writer per slot for the server's lifetime: Windows won't open the same file twice,
            // and a slot is started again after its bot stops or fails.
            StreamWriter log = _logs.GetOrAdd(slot, s => new StreamWriter(
                new FileStream(Path.Combine(_logDir, $"bot{s}.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete),
                new UTF8Encoding(false)) { AutoFlush = true });
            lock (log)
                log.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} bot {slot} starting =====");
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (log) log.WriteLine(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _bots[slot] = process;
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                string text = $"bot {slot} exited (code {SafeExitCode(process)})";
                Log(text);
                lock (log)
                    log.WriteLine($"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {text} =====");
            };
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

    /// <summary>
    /// Ends bots and games left running by an earlier server (e.g. its window was closed): they hold
    /// the slots' ports and stay logged in. Call after this server's port is taken.
    /// </summary>
    public static int KillLeftovers()
    {
        int killed = 0;
        string dir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        foreach (string name in new[] { "skua-headless", "skua-ruffle" })
        {
            foreach (Process p in Process.GetProcessesByName(name))
            {
                try
                {
                    if (p.Id == Environment.ProcessId)
                        continue;
                    string? file = p.MainModule?.FileName;
                    if (file is null || !file.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
                        continue;
                    p.Kill(true);
                    killed++;
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        return killed;
    }

    private static string SafeExitCode(Process process)
    {
        try { return process.ExitCode.ToString(); } catch { return "?"; }
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

/// <summary>Windows job object: processes in it are killed when the server process ends.</summary>
static class BotJob
{
    private static readonly IntPtr _job = Create();

    public static void Add(Process process)
    {
        if (_job != IntPtr.Zero)
            AssignProcessToJobObject(_job, process.Handle);
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows())
            return IntPtr.Zero;
        try
        {
            IntPtr job = CreateJobObject(IntPtr.Zero, null);
            JOBOBJECT_EXTENDED_LIMIT_INFORMATION info = new();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, 9, ptr, (uint)size)) // JobObjectExtendedLimitInformation
                    return IntPtr.Zero;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            return job;
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
