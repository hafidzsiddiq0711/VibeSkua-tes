using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Skua.Core.Interfaces;
using Skua.Core.Scripts;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Skua.Host;

/// <summary>A game player without a screen in this bot's process (headless Ruffle).</summary>
public interface IHeadlessGame
{
    /// <summary>Start playing skua-mobile.swf pointed at this bot's game port.</summary>
    bool Start(int gamePort);
    void Stop();
    bool Running { get; }
}

/// <summary>What the manager asks one bot to do: who to log in and what to run.</summary>
public sealed class BotProfile
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string Server { get; set; } = "Twilly";
    /// <summary>Script path relative to the Skua scripts folder.</summary>
    public string Script { get; set; } = "";
    public bool AutoLogin { get; set; } = true;
    public bool AutoStart { get; set; }
    /// <summary>Skua options (IScriptOption property name → value) applied after login.</summary>
    public Dictionary<string, string> SkuaOptions { get; set; } = new();
}

/// <summary>
/// Drives one <see cref="SkuaHost"/> (one account) and lets the Skua Mobile manager control it over
/// a local TCP socket, JSON one object per line:
///   manager → bot : {"id":1,"cmd":"status"}
///   bot → manager : {"id":1,"ok":true,"data":{...}}   or {"id":1,"ok":false,"error":"..."}
/// Same idea as the desktop manager talking to each VibeSkua.exe.
/// </summary>
public sealed class BotController : IDisposable
{
    private readonly SkuaHost _host;
    private readonly int _slot;
    private readonly List<(long Seq, string Text)> _log = new();
    private long _seq;
    private BotProfile _profile = new();
    private JObject _status = new();
    private TcpListener? _listener;
    private readonly CancellationTokenSource _cts = new();
    private int _autopilotRunning;
    // Auto start runs once per game load, so a script stopped by hand stays stopped.
    private volatile bool _autoStartDone;
    private volatile bool _worldChecked;

    /// <summary>Headless player for this platform (null: not available).</summary>
    public IHeadlessGame? Headless { get; set; }

    /// <summary>The manager asked this bot to quit (the app ends the process).</summary>
    public event Action? ShutdownRequested;

    public BotController(SkuaHost host, int slot)
    {
        _host = host;
        _slot = slot;
        host.Log += AddLog;
        host.GameReady += () =>
        {
            _autoStartDone = false;
            _worldChecked = false;
            _ = AutopilotAsync("game ready");
        };
    }

    public static int ControlPort(int slot) => 6150 + slot;
    public static int GamePort(int slot) => 5150 + slot;

    public BotProfile Profile => _profile;

    /// <summary>Last command other than ping/status/logs (the helper bot quits when idle).</summary>
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    public void AddLog(string text)
    {
        lock (_log)
        {
            _log.Add((++_seq, $"{DateTime.Now:HH:mm:ss} {text}"));
            if (_log.Count > 600)
                _log.RemoveRange(0, _log.Count - 600);
        }
    }

    // ------------------------------------------------------------------ autopilot

    /// <summary>Log in (if enabled) and start the script (if enabled) once the game is ready.</summary>
    public async Task AutopilotAsync(string reason)
    {
        if (Interlocked.Exchange(ref _autopilotRunning, 1) == 1)
            return;
        try
        {
            BotProfile p = _profile;
            if (!_host.GameLoaded)
                return;
            if (p.AutoLogin && p.Username.Length > 0 && !_host.Bot.Player.Playing)
            {
                AddLog($"Autopilot ({reason}): logging in {p.Username} …");
                if (!await _host.LoginAsync(p.Username, p.Password, p.Server))
                    return;
            }
            if (_host.Bot.Player.Playing)
            {
                ApplySkuaOptions(p.SkuaOptions);
                if (!_worldChecked)
                {
                    _worldChecked = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(15000);
                        _host.Report(_host.WorldCheck());
                    });
                }
            }
            if (p.AutoStart && !_autoStartDone && p.Script.Length > 0 && _host.Bot.Player.Playing && !_host.Manager.ScriptRunning)
            {
                _autoStartDone = true;
                await Task.Delay(1000);
                await _host.StartScriptAsync(ScriptPath(p.Script));
            }
        }
        catch (Exception e)
        {
            AddLog($"Autopilot error: {e.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _autopilotRunning, 0);
        }
    }

    private static string ScriptPath(string relative)
    {
        if (Path.IsPathRooted(relative))
            return relative;
        string path = Path.Combine(SkuaHost.ScriptsDirectory, relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));
        if (File.Exists(path) || !Directory.Exists(SkuaHost.ScriptsDirectory))
            return path;
        // Scripts get moved between folders in the script repo: find it by its file name.
        string? found = Directory.EnumerateFiles(SkuaHost.ScriptsDirectory, Path.GetFileName(path), SearchOption.AllDirectories).FirstOrDefault();
        return found ?? path;
    }

    // ------------------------------------------------------------------ Skua options

    private static readonly Dictionary<string, PropertyInfo> _optionProps = typeof(IScriptOption)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.CanRead && p.CanWrite && (p.PropertyType == typeof(bool) || p.PropertyType == typeof(int) || p.PropertyType == typeof(string)))
        .ToDictionary(p => p.Name);

    public void ApplySkuaOptions(IReadOnlyDictionary<string, string> values)
    {
        foreach (var (key, value) in values)
        {
            if (!_optionProps.TryGetValue(key, out PropertyInfo? prop))
                continue;
            try
            {
                object converted = prop.PropertyType == typeof(bool) ? bool.Parse(value)
                    : prop.PropertyType == typeof(int) ? int.Parse(value)
                    : value;
                prop.SetValue(_host.Bot.Options, converted);
            }
            catch (Exception e)
            {
                AddLog($"Option {key}={value} failed: {e.Message}");
            }
        }
    }

    public Dictionary<string, string> ReadSkuaOptions()
    {
        Dictionary<string, string> result = new();
        foreach (OptionDef def in OptionCatalog.SkuaOptions)
        {
            if (!_optionProps.TryGetValue(def.Key, out PropertyInfo? prop))
                continue;
            try { result[def.Key] = Convert.ToString(prop.GetValue(_host.Bot.Options), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
            catch { }
        }
        return result;
    }

    // ------------------------------------------------------------------ script options

    /// <summary>Compile the script and list its options (the desktop "Script Options" window).</summary>
    public JArray DescribeScriptOptions(string script)
    {
        IScriptOptionContainer config = LoadConfig(script);
        JArray items = new();
        void Add(string category, IOption o)
        {
            JObject item = new()
            {
                ["category"] = category,
                ["name"] = o.Name,
                ["label"] = string.IsNullOrWhiteSpace(o.DisplayName) ? o.Name : o.DisplayName,
                ["description"] = o.Description ?? "",
                ["value"] = config.GetDirect(o),
            };
            if (o.Type == typeof(bool))
                item["type"] = "bool";
            else if (o.Type == typeof(int) || o.Type == typeof(long) || o.Type == typeof(short))
                item["type"] = "int";
            else if (o.Type.IsEnum)
            {
                item["type"] = "choice";
                item["choices"] = new JArray(Enum.GetNames(o.Type).Select(n => (object)n).ToArray());
            }
            else
                item["type"] = "string";
            items.Add(item);
        }
        foreach (IOption o in config.Options)
            Add("", o);
        foreach (var (category, options) in config.MultipleOptions)
            foreach (IOption o in options)
                Add(category, o);
        return items;
    }

    public void SaveScriptOptions(string script, JObject values)
    {
        IScriptOptionContainer config = LoadConfig(script);
        foreach (JProperty prop in values.Properties())
        {
            // key: "name" or "category/name"
            string key = prop.Name;
            int slash = key.IndexOf('/');
            IOption? option = slash < 0
                ? config.Options.Find(o => o.Name == key)
                : config.MultipleOptions.TryGetValue(key[..slash], out List<IOption>? list) ? list.Find(o => o.Name == key[(slash + 1)..]) : null;
            if (option is not null)
                config.OptionValues[option] = prop.Value.ToString();
        }
        config.Save();
        AddLog($"Script options saved: {Path.GetFileName(script)}");
    }

    private IScriptOptionContainer LoadConfig(string script)
    {
        if (_host.Manager.ScriptRunning)
            throw new InvalidOperationException("Stop the script first to change its options.");
        string path = ScriptPath(script);
        _host.Manager.OverrideStorage = null;
        _host.Manager.SetLoadedScript(path);
        object compiled = _host.Manager.Compile(ScriptPlatformCompat.ReadScript(path))
            ?? throw new InvalidOperationException("The script failed to compile.");
        _host.Manager.LoadScriptConfig(compiled);
        return _host.Manager.Config ?? throw new InvalidOperationException("The script has no options.");
    }

    // ------------------------------------------------------------------ status

    private void RefreshStatusLoop()
    {
        Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                JObject s = new()
                {
                    ["slot"] = _slot,
                    ["profileUser"] = _profile.Username,
                    ["connected"] = _host.GameConnected,
                    ["loaded"] = _host.GameLoaded,
                    ["running"] = _host.Manager.ScriptRunning,
                    ["script"] = _host.Manager.ScriptRunning && _host.Manager.LoadedScript is { } ls ? Path.GetFileNameWithoutExtension(ls) : _profile.Script.Length > 0 ? Path.GetFileNameWithoutExtension(_profile.Script) : "",
                    ["memMb"] = Environment.WorkingSet / (1024 * 1024),
                    ["headless"] = Headless?.Running == true,
                };
                try
                {
                    if (_host.GameLoaded)
                    {
                        bool playing = _host.Bot.Player.Playing;
                        s["playing"] = playing;
                        if (playing)
                        {
                            s["user"] = _host.Bot.Player.Username;
                            s["level"] = _host.Bot.Player.Level;
                            s["gold"] = _host.Bot.Player.Gold;
                            s["map"] = _host.Bot.Map.Name;
                            s["cell"] = _host.Bot.Player.Cell;
                        }
                    }
                }
                catch { }
                lock (_log)
                    s["logSeq"] = _seq;
                _status = s;
                try { await Task.Delay(2000, _cts.Token); } catch { }
            }
        });
    }

    // ------------------------------------------------------------------ control socket

    public void StartControlServer()
    {
        RefreshStatusLoop();
        _listener = new TcpListener(IPAddress.Loopback, ControlPort(_slot));
        _listener.Start();
        AddLog($"Bot #{_slot}: control on 127.0.0.1:{ControlPort(_slot)}, game on 127.0.0.1:{GamePort(_slot)}");
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
            NetworkStream stream = client.GetStream();
            using StreamReader reader = new(stream, new UTF8Encoding(false));
            using StreamWriter writer = new(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            SemaphoreSlim writeLock = new(1, 1);
            while (!_cts.IsCancellationRequested)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); }
                catch { break; }
                if (line is null)
                    break;
                if (line.Length == 0)
                    continue;
                // Long commands (updateScripts, compile) must not block status polling on the same connection.
                _ = Task.Run(async () =>
                {
                    JObject reply = await HandleAsync(line);
                    await writeLock.WaitAsync();
                    try { await writer.WriteLineAsync(reply.ToString(Formatting.None)); }
                    catch { }
                    finally { writeLock.Release(); }
                });
            }
        }
    }

    public async Task<JObject> HandleAsync(string line)
    {
        JObject reply = new();
        try
        {
            JObject req = JObject.Parse(line);
            reply["id"] = req["id"];
            string cmd = req["cmd"]?.ToString() ?? "";
            bool busy = cmd is not ("ping" or "status" or "logs");
            if (busy)
                LastActivityUtc = DateTime.UtcNow;
            JToken? data = await RunAsync(cmd, req);
            if (busy)
                LastActivityUtc = DateTime.UtcNow;
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

    private async Task<JToken?> RunAsync(string cmd, JObject req)
    {
        switch (cmd)
        {
            case "ping":
                return "pong";
            case "status":
                return _status;
            case "logs":
            {
                long since = req.Value<long?>("since") ?? 0;
                lock (_log)
                    return new JObject
                    {
                        ["seq"] = _seq,
                        ["lines"] = new JArray(_log.Where(l => l.Seq > since).Select(l => (object)l.Text).ToArray()),
                    };
            }
            case "configure":
                _profile = req["profile"]?.ToObject<BotProfile>() ?? new BotProfile();
                AddLog($"Profile: {_profile.Username} @ {_profile.Server}, script: {(_profile.Script.Length > 0 ? _profile.Script : "-")}");
                _ = AutopilotAsync("new profile");
                return null;
            case "login":
                return await _host.LoginAsync(_profile.Username, _profile.Password, _profile.Server);
            case "start":
            {
                string script = req.Value<string>("script") ?? _profile.Script;
                if (script.Length == 0)
                    throw new InvalidOperationException("No script chosen.");
                _profile.Script = script;
                Exception? error = await _host.StartScriptAsync(ScriptPath(script));
                if (error is not null)
                    throw error;
                return null;
            }
            case "stop":
                await _host.StopScriptAsync();
                return null;
            case "servers":
                return JArray.FromObject((await _host.GetServerListAsync()).Select(s => new { s.Name, s.PlayerCount, s.MaxPlayers, s.Online, s.Upgrade }));
            case "updateScripts":
                return await _host.UpdateScriptsAsync(new Progress<string>(_ => { }));
            case "skuaOptions.get":
                return JObject.FromObject(ReadSkuaOptions());
            case "skuaOptions.set":
            {
                Dictionary<string, string> values = req["values"]?.ToObject<Dictionary<string, string>>() ?? new();
                foreach (var kv in values)
                    _profile.SkuaOptions[kv.Key] = kv.Value;
                ApplySkuaOptions(values);
                return null;
            }
            case "scriptOptions.get":
                return DescribeScriptOptions(req.Value<string>("script") ?? _profile.Script);
            case "scriptOptions.set":
                SaveScriptOptions(req.Value<string>("script") ?? _profile.Script, (JObject?)req["values"] ?? new JObject());
                return null;
            case "cbo.get":
                return JObject.FromObject(OptionCatalog.ReadCbo(req.Value<string>("user") ?? _profile.Username));
            case "cbo.set":
                OptionCatalog.WriteCbo(req.Value<string>("user") ?? _profile.Username,
                    req["values"]?.ToObject<Dictionary<string, string>>() ?? new());
                return null;
            case "scripts.list":
            {
                // Scripts on this machine (a phone managing a server picks from the server's).
                string dir = SkuaHost.ScriptsDirectory;
                if (!Directory.Exists(dir))
                    return new JArray();
                return new JArray(Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                    .Select(f => Path.GetRelativePath(dir, f).Replace('\\', '/'))
                    .Where(f => !f.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("Cached-Scripts", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                    .Select(f => (object)f).ToArray());
            }
            case "headless.start":
                if (Headless is null)
                    throw new InvalidOperationException("Headless mode isn't available here.");
                if (!Headless.Running && !Headless.Start(GamePort(_slot)))
                    throw new InvalidOperationException("Headless game failed to start (see bot log).");
                AddLog("Headless game started.");
                return null;
            case "headless.stop":
                if (Headless?.Running == true)
                {
                    Headless.Stop();
                    AddLog("Headless game stopped.");
                }
                return null;
            case "shutdown":
                AddLog("Bot stopped from the manager.");
                if (_host.Manager.ScriptRunning)
                    await _host.StopScriptAsync();
                try { Headless?.Stop(); } catch { }
                _ = Task.Run(async () => { await Task.Delay(300); ShutdownRequested?.Invoke(); });
                return null;
            default:
                throw new InvalidOperationException($"Unknown command: {cmd}");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener?.Stop(); } catch { }
    }
}
