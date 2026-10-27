using CommunityToolkit.Mvvm.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Skua.Core.Flash;
using Skua.Core.GameProxy;
using Skua.Core.Interfaces;
using Skua.Core.Interfaces.Services;
using Skua.Core.Models;
using Skua.Core.Options;
using Skua.Core.Plugins;
using Skua.Core.Scripts;
using Skua.Core.Scripts.Helpers;
using Skua.Core.Services;
using Skua.Core.Skills;
using System.Diagnostics;

namespace Skua.Host;

/// <summary>
/// Skua without a desktop window: the same Skua.Core services the desktop client uses, with
/// headless platform services and <see cref="RemoteFlashUtil"/> as the game connection
/// (skua-mobile.swf in the Artix Games Launcher, or the Ruffle Skua PoC app).
/// </summary>
public sealed class SkuaHost : IDisposable
{
    private readonly RemoteFlashUtil _flash;
    private volatile bool _loadRequested;
    private volatile bool _loaded;

    public IServiceProvider Services { get; }
    public IScriptInterface Bot { get; }
    public IScriptManager Manager { get; }

    /// <summary>Log lines from Skua (script log, dialogs, connection).</summary>
    public event Action<string>? Log;

    public bool GameConnected => _flash.IsConnected;
    public bool GameLoaded => _loaded;

    private SkuaHost(IServiceProvider services)
    {
        Services = services;
        _flash = (RemoteFlashUtil)services.GetRequiredService<IFlashUtil>();
        Bot = services.GetRequiredService<IScriptInterface>();
        Manager = services.GetRequiredService<IScriptManager>();
    }

    /// <param name="flashEndpoint">"listen[:port]" (skua.swf connects to us) or "host[:port]".</param>
    public static SkuaHost Create(string flashEndpoint = "listen:5150", Action<IServiceCollection>? configure = null)
    {
        ServiceCollection services = new();
        services.AddCommonServices();
        services.AddTransient(Skua.Core.AppStartup.Services.CreateCompiler);
        AddScriptableObjects(services);

        services.AddSingleton<ISettingsService, HeadlessSettingsService>();
        services.AddSingleton<IDispatcherService, InlineDispatcherService>();
        services.AddSingleton<IDialogService, HeadlessDialogService>();
        services.AddSingleton<IWindowService, HeadlessWindowService>();
        services.AddSingleton<IScreenshotService, HeadlessScreenshotService>();
        services.AddSingleton<IHotKeyService, HeadlessHotKeyService>();
        services.AddSingleton<IPluginHelper, HeadlessPluginHelper>();
        services.AddSingleton<IFlashUtil>(s => new RemoteFlashUtil(
            flashEndpoint,
            s.GetRequiredService<IMessenger>(),
            new Lazy<IScriptManager>(() => s.GetRequiredService<IScriptManager>())));

        configure?.Invoke(services);

        IServiceProvider provider = services.BuildServiceProvider();
        Ioc.Default.ConfigureServices(provider);

        IClientFilesService clientFiles = provider.GetRequiredService<IClientFilesService>();
        clientFiles.CreateDirectories();
        clientFiles.CreateFiles();

        return new SkuaHost(provider);
    }

    /// <summary>Same registrations as Skua.App.Avalonia's AddScriptableObjects.</summary>
    private static void AddScriptableObjects(IServiceCollection services)
    {
        services.AddSingleton<IScriptInterface, ScriptInterface>();
        services.AddSingleton<IScriptManager, ScriptManager>();
        services.AddSingleton<IScriptStatus, ScriptManager>();

        services.AddSingleton<IScriptInventoryHelper, ScriptInventoryHelper>();
        services.AddSingleton<IScriptInventory, ScriptInventory>();
        services.AddSingleton<IScriptHouseInv, ScriptHouseInv>();
        services.AddSingleton<IScriptTempInv, ScriptTempInv>();
        services.AddSingleton<IScriptBank, ScriptBank>();

        services.AddSingleton<IAdvancedSkillContainer, AdvancedSkillContainer>();
        services.AddSingleton<IUltraBossHelper, UltraBossHelper>();
        services.AddSingleton<IScriptCombat, ScriptCombat>();
        services.AddSingleton<IScriptKill, ScriptKill>();
        services.AddSingleton<IScriptHunt, ScriptHunt>();
        services.AddSingleton<IScriptSkill, ScriptSkill>();
        services.AddSingleton<IScriptAuto, ScriptAuto>();
        services.AddSingleton<IScriptSelfAuras, ScriptSelfAuras>();
        services.AddSingleton<IScriptTargetAuras, ScriptTargetAuras>();

        services.AddSingleton<IScriptFaction, ScriptFaction>();
        services.AddSingleton<IScriptMonster, ScriptMonster>();
        services.AddSingleton<IScriptPlayer, ScriptPlayer>();
        services.AddSingleton<IScriptQuest, ScriptQuest>();
        services.AddSingleton<IScriptBoost, ScriptBoost>();
        services.AddSingleton<IScriptShop, ScriptShop>();
        services.AddSingleton<IScriptDrop, ScriptDrop>();
        services.AddSingleton<IScriptMap, ScriptMap>();

        services.AddSingleton<IScriptServers, ScriptServers>();
        services.AddSingleton<IScriptEvent, ScriptEvent>();
        services.AddSingleton<IScriptSend, ScriptSend>();

        services.AddTransient<IScriptOptionContainer, ScriptOptionContainer>();
        services.AddTransient<IOptionContainer, OptionContainer>();
        services.AddSingleton<IScriptOption, ScriptOption>();
        services.AddSingleton<IScriptLite, ScriptLite>();

        services.AddSingleton<IScriptBotStats, ScriptBotStats>();
        services.AddSingleton<IScriptHandlers, ScriptHandlers>();
        services.AddSingleton<IScriptWait, ScriptWait>();
        services.AddSingleton<IScriptAccounts, ScriptAccounts>();

        services.AddSingleton<ICaptureProxy, CaptureProxy>();

        services.AddSingleton<IPluginManager, PluginManager>();
        services.AddTransient<IPluginContainer, PluginContainer>();

        services.AddSingleton<IMapService, MapService>();
        services.AddSingleton<ILogService, LogService>();
        services.AddSingleton<IQuestDataLoaderService, QuestDataLoaderService>();
        services.AddSingleton<IGrabberService, GrabberService>();
        services.AddSingleton<IClientFilesService, ClientFilesService>();
        services.AddSingleton<IAuraMonitorService, AuraMonitorService>();
        services.AddSingleton<IJunkService, JunkService>();
        services.AddSingleton<BackgroundThemeService>();
    }

    /// <summary>Open the game connection and answer the movie's load request.</summary>
    public void Start()
    {
        HeadlessDialogService.Message += Say;
        _flash.FlashCall += OnFlashCall;
        _flash.InitializeFlash();
        Say($"Menunggu game (skua-mobile.swf) ...");
    }

    private void OnFlashCall(string function, params object[] args)
    {
        switch (function)
        {
            case "requestLoadGame":
                // Same as SkuaStartupHandler.LoadGame; on every (re)connect the movie asks again.
                _loadRequested = true;
                _loaded = false;
                Say("Game tersambung, memuat AQW ...");
                Task.Run(() => _flash.Call("loadClient"));
                break;
            case "loaded":
                _loaded = true;
                Say("AQW termuat.");
                break;
            case "debug":
                if (args.Length > 0)
                    Trace.WriteLine(args[0]);
                break;
        }
    }

    /// <summary>Log in like the desktop's auto-login (SkuaStartupHandler.Login).</summary>
    public async Task<bool> LoginAsync(string username, string password, string server, CancellationToken token = default)
    {
        Say("Menunggu AQW termuat ...");
        while (!_loaded)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(250, token);
        }

        Bot.Servers.SetLoginInfo(username, password);
        Say($"Login {username} ke server {server} ...");
        bool ok = await Task.Run(() => Bot.Servers.EnsureRelogin(string.IsNullOrWhiteSpace(server) ? "Twilly" : server), token);

        for (int i = 0; i < 80 && !Bot.Player.Playing; i++)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(250, token);
        }

        ok = Bot.Player.Playing;
        Say(ok ? $"Masuk game sebagai {username}." : "Login gagal / belum masuk game.");
        return ok;
    }

    /// <summary>Download new/outdated scripts from the Skua scripts repository (auqw/Scripts).</summary>
    public async Task<int> UpdateScriptsAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        IGetScriptsService scripts = Services.GetRequiredService<IGetScriptsService>();
        progress ??= new Progress<string>(Say);
        await scripts.GetScriptsAsync(progress, token);
        await scripts.RefreshScriptsAsync(progress, token);
        int count = await scripts.DownloadAllWhereAsync(s => !s.Downloaded || s.Outdated, progress, token);
        try { await scripts.UpdateSkillSetsFile(); } catch { }
        try { await scripts.UpdateQuestDataFile(); } catch { }
        try { await scripts.UpdateJunkItemsFile(); } catch { }
        Say($"Script diperbarui: {count} file. Total {scripts.Downloaded}/{scripts.Total}.");
        return count;
    }

    public static string ScriptsDirectory => ClientFileSources.SkuaScriptsDIR;

    /// <summary>Load and start a script file. Script options use saved values (no dialogs).</summary>
    public async Task<Exception?> StartScriptAsync(string path)
    {
        if (Manager.ScriptRunning)
            await StopScriptAsync();
        Manager.SilentConfig = true;
        Manager.SetLoadedScript(path);
        Say($"Menjalankan script: {Path.GetFileName(path)}");
        Exception? error = await Manager.StartScript();
        if (error is not null)
            Say($"Script error: {error.Message}");
        return error;
    }

    public async Task StopScriptAsync()
    {
        await Manager.StopScript();
        Say("Script dihentikan.");
    }

    /// <summary>Compile a script source without running it (self-test of the compiler setup).</summary>
    public object? CompileOnly(string source) => Manager.Compile(source);

    private void Say(string text)
    {
        Trace.WriteLine(text);
        Log?.Invoke(text);
    }

    public void Dispose()
    {
        HeadlessDialogService.Message -= Say;
        _flash.FlashCall -= OnFlashCall;
        try { _flash.Dispose(); } catch { }
    }
}

public sealed class HeadlessPluginHelper : IPluginHelper
{
    public void AddMenuButton(string text, Action action) { }
    public void RemoveMenuButton(string text) { }
}
