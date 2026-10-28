using Skua.Host;
using System.Diagnostics;

// skua-headless selftest [--refs DIR] [--with-scripts]
//   Compiles a tiny script (and with --with-scripts, downloads the Skua script repo and compiles
//   real community scripts that include CoreBots). Exit code 0 = OK.
// skua-headless run --user U --pass P [--server Twilly] [--script PATH] [--endpoint listen:5150]
//                  [--update-scripts]
//   Waits for skua-mobile.swf (or the Ruffle Skua PoC app), logs in and runs the script.

Trace.Listeners.Add(new ConsoleTraceListener());
string Arg(string name, string? def = null)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : def ?? string.Empty;
}
bool Flag(string name) => args.Contains(name);

string command = args.Length > 0 ? args[0] : "help";
string refs = Arg("--refs");
if (refs.Length > 0)
    Skua.Core.AppStartup.Services.ReferenceDirectory = refs;

switch (command)
{
    case "selftest":
        return await SelfTest();
    case "run":
        return await Run();
    case "bot":
        return await Bot();
    case "e2e":
        return await EndToEnd();
    case "lab":
        return await Lab();
    case "server":
        return await Server();
    case "servertest":
        return await ServerTest();
    default:
        Console.WriteLine("usage: skua-headless selftest [--refs DIR] [--with-scripts]");
        Console.WriteLine("       skua-headless run --user U --pass P [--server S] [--script PATH] [--endpoint listen:5150] [--update-scripts]");
        Console.WriteLine("       skua-headless server        (Skua Server for Skua Mobile; settings in server.json)");
        return 2;
}

async Task<int> SelfTest()
{
    using SkuaHost host = SkuaHost.Create("listen:0");
    host.Log += Console.WriteLine;
    int failures = 0;

    const string tiny = """
        using Skua.Core.Interfaces;
        public class Script
        {
            public void ScriptMain(IScriptInterface bot)
            {
                bot.Log("hello from a compiled script");
            }
        }
        """;
    failures += TryCompile("tiny script", tiny, null);

    if (Flag("--with-scripts"))
    {
        await host.UpdateScriptsAsync(new Progress<string>(_ => { }));
        string dir = SkuaHost.ScriptsDirectory;
        string coreBots = Path.Combine(dir, "CoreBots.cs");
        Console.WriteLine($"CoreBots.cs present: {File.Exists(coreBots)}");
        if (!File.Exists(coreBots))
            failures++;
        else if (Skua.Core.Scripts.ScriptPlatformCompat.ReadScript(coreBots).Contains("IsWindowsVersionAtLeast"))
        {
            Console.WriteLine("[FAIL] CoreBots still has the Windows version check after compat");
            failures++;
        }
        else
            Console.WriteLine("[OK]   CoreBots Windows check neutralised");

        // A few real scripts that pull in CoreBots/CoreFarms/CoreAdvanced via //cs_include.
        var candidates = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("Core", StringComparison.OrdinalIgnoreCase))
            .Where(f =>
            {
                string head = string.Join('\n', File.ReadLines(f).Take(40));
                return head.Contains("//cs_include Scripts/CoreBots.cs") && head.Contains("CoreFarms");
            })
            .OrderBy(f => new FileInfo(f).Length)
            .Take(3)
            .ToList();
        // Scripts whose //cs_include paths differ in letter case from the repo (Linux/Android are case-sensitive).
        foreach (string rel in new[] { "Other/MergeShops/YulgarsUndineMerge.cs", "Other/FarmJoeKits/0FarmerJoeDoAll.cs" })
        {
            string full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
                candidates.Add(full);
        }
        foreach (string path in candidates)
            failures += TryCompile(Path.GetRelativePath(dir, path), File.ReadAllText(path), path);
        if (candidates.Count == 0)
        {
            Console.WriteLine("no CoreBots-based scripts found");
            failures++;
        }

        failures += await ControlTest();
    }

    async Task<int> ControlTest()
    {
        // Manager <-> bot protocol, the way Skua Mobile drives each account.
        int fails = 0;
        using BotController bot = new(host, 9);
        bot.StartControlServer();
        using BotClient client = new(9);
        async Task Check(string label, Func<Task<bool>> test)
        {
            try
            {
                bool ok = await test();
                Console.WriteLine($"{(ok ? "[OK]  " : "[FAIL]")} control {label}");
                if (!ok) fails++;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[FAIL] control {label}: {e.Message}");
                fails++;
            }
        }
        await Check("ping", () => client.PingAsync(5000));
        await Check("configure + status", async () =>
        {
            BotProfile profile = new() { Username = "selftest", Server = "Twilly", Script = "Farm/BuyScrolls.cs", AutoLogin = false };
            await client.SendAsync("configure", new Newtonsoft.Json.Linq.JObject { ["profile"] = Newtonsoft.Json.Linq.JObject.FromObject(profile) });
            await Task.Delay(2500);
            var status = await client.SendAsync("status");
            Console.WriteLine($"       status: {status?.ToString(Newtonsoft.Json.Formatting.None)}");
            return status?.Value<string>("profileUser") == "selftest";
        });
        await Check("skuaOptions.get", async () =>
        {
            var opts = await client.SendAsync("skuaOptions.get");
            Console.WriteLine($"       {opts?.ToString(Newtonsoft.Json.Formatting.None)}");
            return opts?["LagKiller"] is not null;
        });
        await Check("scriptOptions.get/set (Farm/BuyScrolls.cs)", async () =>
        {
            var items = await client.SendAsync("scriptOptions.get", new Newtonsoft.Json.Linq.JObject { ["script"] = "Farm/BuyScrolls.cs" }, 120000);
            Console.WriteLine($"       {items?.Count()} option(s): {string.Join(", ", items!.Select(i => $"{i["name"]}:{i["type"]}={i["value"]}"))}");
            var first = items!.First();
            string key = (first.Value<string>("category") is { Length: > 0 } c ? c + "/" : "") + first.Value<string>("name");
            string newValue = first.Value<string>("type") == "bool" ? (!bool.Parse(first.Value<string>("value")!)).ToString() : first.Value<string>("value")!;
            await client.SendAsync("scriptOptions.set", new Newtonsoft.Json.Linq.JObject { ["script"] = "Farm/BuyScrolls.cs", ["values"] = new Newtonsoft.Json.Linq.JObject { [key] = newValue } }, 120000);
            var again = await client.SendAsync("scriptOptions.get", new Newtonsoft.Json.Linq.JObject { ["script"] = "Farm/BuyScrolls.cs" }, 120000);
            return again!.First().Value<string>("value") == newValue;
        });
        await Check("CBO file round trip", () =>
        {
            OptionCatalog.WriteCbo("selftest", new Dictionary<string, string> { ["AntiLag"] = "False", ["FarmClassSelect"] = "Legion Revenant" });
            var read = OptionCatalog.ReadCbo("selftest");
            return Task.FromResult(read["AntiLag"] == "False" && read["FarmClassSelect"] == "Legion Revenant" && read["PrivateRooms"] == "True");
        });
        await Check("logs", async () => (await client.SendAsync("logs", new Newtonsoft.Json.Linq.JObject { ["since"] = 0 }))?["lines"]?.Any() == true);
        return fails;
    }

    Console.WriteLine(failures == 0 ? "SELFTEST OK" : $"SELFTEST FAILED ({failures})");
    return failures == 0 ? 0 : 1;

    int TryCompile(string label, string source, string? path)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            if (path is not null)
                host.Manager.SetLoadedScript(path);
            object? script = host.CompileOnly(source);
            Console.WriteLine($"[OK]   compile {label} ({sw.ElapsedMilliseconds} ms) -> {script?.GetType().FullName}");
            return script is null ? 1 : 0;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[FAIL] compile {label}: {e.Message}");
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                string msg = $"{label}: {e.GetType().Name}: {e.Message}\n{e}";
                Console.WriteLine($"::error title=selftest::{msg.Replace("%", "%25").Replace("\r", "").Replace("\n", "%0A")}");
            }
            return 1;
        }
    }
}

string DefaultRuffle() => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "skua-ruffle.exe" : "skua-ruffle");
string DefaultSwf() => Path.Combine(AppContext.BaseDirectory, "skua-mobile.swf");

// skua-headless bot --slot N [--ruffle PATH] [--swf PATH]
//   One account controlled by a manager (control 6150+N, game 5150+N, both on 127.0.0.1).
async Task<int> Bot()
{
    int slot = int.Parse(Arg("--slot", "1"));
    string ruffle = Arg("--ruffle", DefaultRuffle()), swf = Arg("--swf", DefaultSwf());
    using SkuaHost host = SkuaHost.Create($"listen-local:{BotController.GamePort(slot)}");
    using BotController bot = new(host, slot)
    {
        Headless = File.Exists(ruffle) && File.Exists(swf) ? new ProcessHeadlessGame(ruffle, swf) : null,
    };
    host.Log += line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    TaskCompletionSource quit = new();
    bot.ShutdownRequested += () => quit.TrySetResult();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
    try
    {
        host.Start();
        bot.StartControlServer();
    }
    catch (System.Net.Sockets.SocketException e)
    {
        Console.WriteLine($"Bot {slot}: port {BotController.GamePort(slot)}/{BotController.ControlPort(slot)} is in use ({e.Message}). " +
            "Another bot for this slot is still running: restart Skua Server.");
        return 3;
    }
    await quit.Task;
    bot.Headless?.Stop();
    return 0;
}

ProcessStartInfo BotProcess(int slot, string ruffle, string swf)
{
    string exe = Environment.ProcessPath!;
    ProcessStartInfo info = new(exe);
    if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
        info.ArgumentList.Add(typeof(ServerConfig).Assembly.Location);
    foreach (string a in new[] { "bot", "--slot", slot.ToString(), "--ruffle", ruffle, "--swf", swf })
        info.ArgumentList.Add(a);
    return info;
}

// skua-headless server : Skua Server (bots of this machine for Skua Mobile), see server.json.
async Task<int> Server()
{
    string configPath = Path.Combine(AppContext.BaseDirectory, "server.json");
    ServerConfig config = ServerConfig.LoadOrCreate(configPath, out bool created);
    // Before anything reads Skua's folders: the bots (child processes) inherit this.
    string desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSkua");
    if (config.DataDir.Length > 0)
        Environment.SetEnvironmentVariable("SKUA_DATA_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, config.DataDir)));
    string ruffle = Arg("--ruffle", DefaultRuffle()), swf = Arg("--swf", DefaultSwf());
    using SkuaServer server = new(config, slot => BotProcess(slot, ruffle, swf), Path.Combine(AppContext.BaseDirectory, "logs"));
    try
    {
        server.Start();
    }
    catch (System.Net.Sockets.SocketException)
    {
        Console.WriteLine($"Port {config.Port} is already in use: is Skua Server already running (another window, or autostart)?");
        Console.WriteLine("Close the other one first. Press Enter to quit.");
        Console.ReadLine();
        return 1;
    }
    int leftovers = SkuaServer.KillLeftovers();
    if (leftovers > 0)
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ended {leftovers} bot/game process(es) left over from the last run.");
    server.Ready = PrepareServerData(desktopDir);
    Console.WriteLine("==============================================================");
    Console.WriteLine($"  Skua Server \"{config.Name}\" is running on port {config.Port}.");
    Console.WriteLine($"  Token: {config.Token}");
    Console.WriteLine("  In Skua Mobile: menu -> Servers -> Add, then enter this PC's");
    Console.WriteLine("  Tailscale IP, the port and the token above.");
    if (!File.Exists(ruffle))
        Console.WriteLine($"  WARNING: {ruffle} is missing: headless games can't start.");
    Console.WriteLine("  Keep this window open (or use install-autostart.bat).");
    Console.WriteLine("==============================================================");
    if (created)
        Console.WriteLine($"(A new token was saved in {configPath}.)");
    TaskCompletionSource quit = new();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
    await quit.Task;
    return 0;
}

// First start with its own data folder: CoreBots options (classes etc.) from VibeSkua desktop,
// and the scripts downloaded.
async Task PrepareServerData(string desktopDir)
{
    string dataDir = Skua.Core.Models.ClientFileSources.SkuaDIR;
    if (string.Equals(Path.GetFullPath(dataDir).TrimEnd('\\', '/'), Path.GetFullPath(desktopDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        return;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Skua data folder: {dataDir}");
    try
    {
        string options = Path.Combine(dataDir, "options"), desktopOptions = Path.Combine(desktopDir, "options");
        if (!Directory.Exists(options) && Directory.Exists(desktopOptions))
        {
            Directory.CreateDirectory(options);
            foreach (string file in Directory.GetFiles(desktopOptions))
                File.Copy(file, Path.Combine(options, Path.GetFileName(file)), false);
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Copied CoreBots options from VibeSkua desktop.");
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Could not copy VibeSkua options: {e.Message}");
    }
    string scripts = Skua.Core.Models.ClientFileSources.SkuaScriptsDIR;
    if (Directory.Exists(scripts) && Directory.EnumerateFiles(scripts, "*.cs", SearchOption.AllDirectories).Any())
        return;
    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Downloading the scripts (first start) …");
    try
    {
        using SkuaHost host = SkuaHost.Create("listen-local:5149");
        int count = await host.UpdateScriptsAsync(new Progress<string>(_ => { }));
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Scripts ready: {count} files.");
    }
    catch (Exception e)
    {
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Script download failed ({e.Message}); use Update scripts in Skua Mobile.");
    }
}

// skua-headless servertest [--ruffle PATH --swf PATH] : the server as Skua Mobile uses it (CI).
async Task<int> ServerTest()
{
    ServerConfig config = new() { Bind = "127.0.0.1", Port = 6199, Token = "test-token", MaxSlots = 4 };
    string ruffle = Arg("--ruffle", DefaultRuffle()), swf = Arg("--swf", DefaultSwf());
    using SkuaServer server = new(config, slot => BotProcess(slot, ruffle, swf), Path.Combine(Path.GetTempPath(), "skua-server-test-logs"));
    server.Start();
    int fails = 0;
    async Task Check(string label, Func<Task<bool>> test)
    {
        try
        {
            bool ok = await test();
            Console.WriteLine($"{(ok ? "[OK]  " : "[FAIL]")} server {label}");
            if (!ok) fails++;
        }
        catch (Exception e)
        {
            Console.WriteLine($"[FAIL] server {label}: {e.Message}");
            fails++;
        }
    }
    using BotClient wrong = new("127.0.0.1", 6199, "nope", 3);
    using BotClient bot = new("127.0.0.1", 6199, "test-token", 3);
    await Check("rejects a wrong token", async () =>
    {
        try { await wrong.SendAsync("server.info"); return false; }
        catch (InvalidOperationException e) { return e.Message.Contains("token"); }
    });
    await Check("server.info", async () => (await bot.SendAsync("server.info"))?.Value<int>("maxSlots") == 4);
    await Check("status of a stopped bot doesn't start it", async () =>
    {
        try { await bot.SendAsync("status"); return false; }
        catch (InvalidOperationException e) { return e.Message.Contains("isn't running"); }
    });
    await Check("configure starts bot 3", async () =>
    {
        BotProfile profile = new() { Username = "servertest", AutoLogin = false };
        await bot.SendAsync("configure", new Newtonsoft.Json.Linq.JObject { ["profile"] = Newtonsoft.Json.Linq.JObject.FromObject(profile) }, 150000);
        await Task.Delay(2500);
        return (await bot.SendAsync("status"))?.Value<string>("profileUser") == "servertest";
    });
    await Check("scripts.list", async () => (await bot.SendAsync("scripts.list")) is Newtonsoft.Json.Linq.JArray);
    if (File.Exists(ruffle))
    {
        await Check("headless game on the server loads AQW", async () =>
        {
            await bot.SendAsync("headless.start");
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(1000);
                var status = await bot.SendAsync("status");
                if (status?.Value<bool?>("loaded") == true)
                {
                    Console.WriteLine($"       status: {status.ToString(Newtonsoft.Json.Formatting.None)}");
                    return true;
                }
            }
            return false;
        });
    }
    await Check("an account plays in one bot only (bot 3 stops when bot 2 gets its account)", async () =>
    {
        using BotClient bot2 = new("127.0.0.1", 6199, "test-token", 2);
        BotProfile profile = new() { Username = "servertest", AutoLogin = false };
        await bot2.SendAsync("configure", new Newtonsoft.Json.Linq.JObject { ["profile"] = Newtonsoft.Json.Linq.JObject.FromObject(profile) }, 150000);
        var info = await bot2.SendAsync("server.info");
        var running = info?["running"]?.Select(t => (int)t).ToList() ?? new();
        await bot2.SendAsync("shutdown");
        return running.Contains(2) && !running.Contains(3);
    });
    await Check("shutdown", async () =>
    {
        try { await bot.SendAsync("shutdown"); } catch { }
        await Task.Delay(1500);
        var info = await bot.SendAsync("server.info");
        return info?["running"]?.Any() != true;
    });
    Console.WriteLine(fails == 0 ? "SERVERTEST OK" : $"SERVERTEST FAILED ({fails})");
    return fails == 0 ? 0 : 1;
}

// skua-headless e2e --ruffle PATH --swf PATH [--timeout 240]
//   Headless Ruffle (skua-ruffle) plays skua-mobile.swf against a bot until AQW has loaded,
//   driven through the manager protocol like Skua Mobile does. Exit 0 = AQW loaded headless.
async Task<int> EndToEnd()
{
    const int slot = 5;
    using SkuaHost host = SkuaHost.Create($"listen-local:{BotController.GamePort(slot)}");
    using BotController bot = new(host, slot)
    {
        Headless = new ProcessHeadlessGame(Arg("--ruffle"), Arg("--swf")),
    };
    host.Log += line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    host.Start();
    bot.StartControlServer();
    using BotClient client = new(slot);
    var sw = Stopwatch.StartNew();
    await client.SendAsync("headless.start");
    int timeout = int.Parse(Arg("--timeout", "240"));
    while (!host.GameLoaded && sw.Elapsed.TotalSeconds < timeout)
        await Task.Delay(1000);
    bool ok = host.GameLoaded;
    if (ok)
    {
        // The login screen of the real game, read through the bridge.
        await Task.Delay(5000);
        Console.WriteLine($"login screen visible: {host.Bot.Flash.GetGameObject<bool>("mcLogin.visible", false)}");

        // With a test account (CI secrets), log in all the way into the world.
        string user = Environment.GetEnvironmentVariable("SKUA_E2E_USER") ?? "";
        if (user.Length > 0)
        {
            bool playing = await host.LoginAsync(user, Environment.GetEnvironmentVariable("SKUA_E2E_PASS") ?? "", "Twilly");
            if (playing)
            {
                await Task.Delay(5000);
                Console.WriteLine($"in game: map {host.Bot.Map.Name}, level {host.Bot.Player.Level}, cell {host.Bot.Player.Cell}");
                await Task.Delay(10000);
                string check1 = host.WorldCheck();
                Console.WriteLine(check1);
                Console.WriteLine($"::notice title=e2e world (login)::{check1}");
                host.Bot.Map.Join("battleon");
                await Task.Delay(15000);
                string check2 = host.WorldCheck();
                Console.WriteLine(check2);
                Console.WriteLine($"::notice title=e2e world (battleon)::{check2}");
            }
            Console.WriteLine(playing ? "E2E LOGIN OK" : "E2E LOGIN FAILED");
            ok &= playing;
        }
        else
        {
            Console.WriteLine("no SKUA_E2E_USER secret: login not tested");
        }
    }
    var status = await client.SendAsync("status");
    Console.WriteLine($"status: {status?.ToString(Newtonsoft.Json.Formatting.None)}");
    await client.SendAsync("headless.stop");
    Console.WriteLine(ok ? $"E2E OK: AQW loaded headless in {sw.Elapsed.TotalSeconds:F0} s" : "E2E FAILED: AQW did not load");
    if (!ok && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
        Console.WriteLine("::error title=e2e::AQW did not load in headless Ruffle");
    return ok ? 0 : 1;
}

async Task<int> Run()
{
    using SkuaHost host = SkuaHost.Create(Arg("--endpoint", "listen:5150"));
    host.Log += line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    using CancellationTokenSource cts = new();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    if (Flag("--update-scripts"))
        await host.UpdateScriptsAsync(token: cts.Token);

    host.Start();
    string user = Arg("--user"), pass = Arg("--pass");
    if (user.Length > 0)
    {
        if (!await host.LoginAsync(user, pass, Arg("--server", "Twilly"), cts.Token))
            return 1;
    }

    string script = Arg("--script");
    if (script.Length > 0)
    {
        if (!Path.IsPathRooted(script))
            script = Path.Combine(SkuaHost.ScriptsDirectory, script);
        Exception? error = await host.StartScriptAsync(script);
        if (error is not null)
            return 1;
    }

    try
    {
        await Task.Delay(Timeout.Infinite, cts.Token);
    }
    catch (OperationCanceledException)
    {
    }
    await host.StopScriptAsync();
    return 0;
}


/// <summary>Headless game as a skua-ruffle process (Linux, Windows).</summary>
// skua-headless lab [--ruffle PATH --swf PATH] [--script S] [--seconds 150]
//   The test account (SKUA_E2E_USER/PASS) through a Skua Server exactly as Skua Mobile drives it:
//   own data folder, bot process started on demand, real script. For debugging.
async Task<int> Lab()
{
    string desktopDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSkua");
    Environment.SetEnvironmentVariable("SKUA_DATA_DIR", Path.Combine(AppContext.BaseDirectory, "data"));
    ServerConfig config = new() { Bind = "127.0.0.1", Port = 6198, Token = "lab-token", MaxSlots = 4 };
    string ruffle = Arg("--ruffle", DefaultRuffle()), swf = Arg("--swf", DefaultSwf());
    string logDir = Path.Combine(AppContext.BaseDirectory, "logs");
    using SkuaServer server = new(config, slot => BotProcess(slot, ruffle, swf), logDir);
    server.Ready = PrepareServerData(desktopDir);
    server.Start();
    using BotClient bot = new("127.0.0.1", 6198, "lab-token", 1);
    string script = Arg("--script", "Other/FarmJoeKits/0FarmerJoeDoAll.cs");
    int seconds = int.Parse(Arg("--seconds", "150"));

    await bot.SendAsync("configure", new Newtonsoft.Json.Linq.JObject
    {
        ["profile"] = Newtonsoft.Json.Linq.JObject.FromObject(new BotProfile
        {
            Username = Environment.GetEnvironmentVariable("SKUA_E2E_USER") ?? "",
            Password = Environment.GetEnvironmentVariable("SKUA_E2E_PASS") ?? "",
            Server = Arg("--server", "Twilly"),
            Script = script == "none" ? "" : script,
            AutoLogin = true,
            AutoStart = script != "none",
        }),
    }, 10 * 60 * 1000);
    await bot.SendAsync("headless.start", null, 60000);
    string join = Arg("--join");
    var sw = Stopwatch.StartNew();
    long seq = 0;
    while (sw.Elapsed.TotalSeconds < seconds)
    {
        await Task.Delay(10000);
        try
        {
            var status = await bot.SendAsync("status");
            Console.WriteLine($"LAB {sw.Elapsed.TotalSeconds:F0}s status: {status?.ToString(Newtonsoft.Json.Formatting.None)}");
            if ((int)sw.Elapsed.TotalSeconds % 60 < 10)
                Console.WriteLine($"LAB PROGRESS {sw.Elapsed.TotalMinutes:F0} min: lv {status?["level"]} xp {status?["xp"]}/{status?["xpNeeded"]} gold {status?["gold"]} map {status?["map"]} mem {status?["memMb"]} MB");
            var logs = await bot.SendAsync("logs", new Newtonsoft.Json.Linq.JObject { ["since"] = seq });
            foreach (var line in logs?["lines"] ?? new Newtonsoft.Json.Linq.JArray())
                Console.WriteLine($"BOT {line}");
            seq = logs?.Value<long?>("seq") ?? seq;
            if (join.Length > 0 && status?.Value<bool?>("playing") == true)
            {
                Console.WriteLine($"LAB joining {join}");
                await bot.SendAsync("join", new Newtonsoft.Json.Linq.JObject { ["map"] = join }, 60000);
                join = "";
            }
            if ((int)sw.Elapsed.TotalSeconds % 30 < 10)
                Console.WriteLine($"LAB {sw.Elapsed.TotalSeconds:F0}s {await bot.SendAsync("worldCheck")}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"LAB {sw.Elapsed.TotalSeconds:F0}s error: {e.Message}");
        }
    }
    // CPU use of the bot and its game over the run (a bot should idle near 0, a game ~ a few %).
    foreach (string name in new[] { "skua-headless", "skua-ruffle", "dotnet" })
        foreach (Process p in Process.GetProcessesByName(name))
            try { Console.WriteLine($"LAB CPU {name} pid {p.Id}: {p.TotalProcessorTime.TotalSeconds:F0} s CPU in {(DateTime.Now - p.StartTime).TotalSeconds:F0} s, {p.WorkingSet64 / (1024 * 1024)} MB"); } catch { }
    try { await bot.SendAsync("stop", null, 30000); } catch (Exception e) { Console.WriteLine($"LAB stop: {e.Message}"); }
    try { await bot.SendAsync("shutdown", null, 15000); } catch { }
    string file = Path.Combine(logDir, "bot1.log");
    if (File.Exists(file))
        foreach (string line in new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)).ReadToEnd().Split('\n').Where(l => !l.TrimStart().StartsWith("at ")).TakeLast(40))
            Console.WriteLine($"FILE {line}");
    Console.WriteLine("LAB DONE");
    return 0;
}

sealed class ProcessHeadlessGame(string ruffle, string swf) : IHeadlessGame
{
    private Process? _process;

    public bool Running => _process is { HasExited: false };

    public bool Start(int gamePort)
    {
        // stdin stays open while this bot lives; skua-ruffle quits when it closes, so a crashed
        // bot never leaves a game behind that would fight the next one for its port.
        ProcessStartInfo info = new(ruffle) { RedirectStandardError = true, RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(swf);
        info.ArgumentList.Add($"127.0.0.1:{gamePort}");
        info.ArgumentList.Add("--until-stdin-closes");
        _process = Process.Start(info);
        if (_process is null)
            return false;
        _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.WriteLine($"  {e.Data}"); };
        _process.BeginErrorReadLine();
        return true;
    }

    public void Stop()
    {
        try { _process?.Kill(); } catch { }
        _process = null;
    }
}
