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
    default:
        Console.WriteLine("usage: skua-headless selftest [--refs DIR] [--with-scripts]");
        Console.WriteLine("       skua-headless run --user U --pass P [--server S] [--script PATH] [--endpoint listen:5150] [--update-scripts]");
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

// skua-headless bot --slot N : one account controlled by a manager (control 6150+N, game 5150+N).
async Task<int> Bot()
{
    int slot = int.Parse(Arg("--slot", "1"));
    using SkuaHost host = SkuaHost.Create($"listen:{BotController.GamePort(slot)}");
    using BotController bot = new(host, slot);
    host.Log += line => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {line}");
    TaskCompletionSource quit = new();
    bot.ShutdownRequested += () => quit.TrySetResult();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; quit.TrySetResult(); };
    host.Start();
    bot.StartControlServer();
    await quit.Task;
    return 0;
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
