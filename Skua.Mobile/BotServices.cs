using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Skua.Host;

namespace Skua.Mobile;

/// <summary>
/// One bot = one account = one process (":bot1" … ":bot6"), like one VibeSkua.exe per account on
/// the desktop: Skua.Core keeps global state (Ioc.Default), so accounts can't share a process.
/// Slot 0 (":bot0") is a helper bot without a game, for script updates and script options.
/// </summary>
public abstract class BotServiceBase : Service
{
    private const string Channel = "bots";
    private PowerManager.WakeLock? _wakeLock;
    private int _renderScale = 100;

    protected abstract int Slot { get; }

    public static readonly Type[] Types =
    {
        typeof(BotService0), typeof(BotService1), typeof(BotService2), typeof(BotService3),
        typeof(BotService4), typeof(BotService5), typeof(BotService6),
    };

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        string label = intent?.GetStringExtra("label") ?? (Slot == 0 ? "Helper" : $"Account {Slot}");
        _renderScale = intent?.GetIntExtra("renderScale", _renderScale) ?? _renderScale;
        // Foreground first: Android kills a service that doesn't call StartForeground quickly.
        StartForegroundCompat(label);

        if (_wakeLock is null)
        {
            PowerManager power = (PowerManager)GetSystemService(PowerService)!;
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, $"skua:bot{Slot}");
            _wakeLock!.Acquire();
        }

        Task.Run(() =>
        {
            try { BotRuntime.Start(this, Slot, Quit); }
            catch (Exception e) { BotRuntime.Log($"Bot failed to start: {e}"); }
        });
        return StartCommandResult.Sticky;
    }

    private void StartForegroundCompat(string label)
    {
        NotificationManager manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            manager.CreateNotificationChannel(new NotificationChannel(Channel, "Bot Skua", NotificationImportance.Low));

        // Tapping a bot's notification opens its game (allowed from the background, unlike startActivity).
        Intent tap = Slot == 0 ? new Intent(this, typeof(MainActivity)) : AppFiles.GameIntent(this, Slot, _renderScale);
        PendingIntent pending = PendingIntent.GetActivity(this, 100 + Slot, tap, PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent)!;
        Intent manage = new(this, typeof(MainActivity));
        PendingIntent managePending = PendingIntent.GetActivity(this, 200 + Slot, manage, PendingIntentFlags.Immutable)!;

        Notification notification = new Notification.Builder(this, Channel)
            .SetContentTitle(Slot == 0 ? "Skua Mobile (helper)" : $"Bot {Slot}: {label}")
            .SetContentText(Slot == 0 ? "Script updates and script options." : "Tap to open this account's game window.")
            .SetSmallIcon(Resource.Drawable.ic_stat_skua)
            .SetContentIntent(pending)
            .AddAction(new Notification.Action.Builder(Android.Graphics.Drawables.Icon.CreateWithResource(this, Resource.Drawable.ic_stat_skua), "Manager", managePending).Build())
            .SetOngoing(true)
            .Build()!;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            StartForeground(100 + Slot, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(100 + Slot, notification);
    }

    /// <summary>Asked by the manager (shutdown) or idle helper: end this process.</summary>
    private void Quit()
    {
        try { _wakeLock?.Release(); } catch { }
        _wakeLock = null;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.N)
            StopForeground(StopForegroundFlags.Remove);
        StopSelf();
        Process.KillProcess(Process.MyPid());
    }

    public override void OnDestroy()
    {
        try { _wakeLock?.Release(); } catch { }
        _wakeLock = null;
        base.OnDestroy();
    }

    public static void Start(Context context, int slot, string label, int renderScale = 100)
    {
        Intent intent = new(context, Types[slot]);
        intent.PutExtra("label", label);
        intent.PutExtra("renderScale", renderScale);
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }
}

[Service(Name = "com.skua.mobile.BotService0", Process = ":bot0", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class BotService0 : BotServiceBase { protected override int Slot => 0; }

[Service(Name = "com.skua.mobile.BotService1", Process = ":bot1", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class BotService1 : BotServiceBase { protected override int Slot => 1; }

[Service(Name = "com.skua.mobile.BotService2", Process = ":bot2", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class BotService2 : BotServiceBase { protected override int Slot => 2; }

[Service(Name = "com.skua.mobile.BotService3", Process = ":bot3", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class BotService3 : BotServiceBase { protected override int Slot => 3; }

[Service(Name = "com.skua.mobile.BotService4", Process = ":bot4", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class BotService4 : BotServiceBase { protected override int Slot => 4; }

[Service(Name = "com.skua.mobile.BotService5", Process = ":bot5", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class BotService5 : BotServiceBase { protected override int Slot => 5; }

[Service(Name = "com.skua.mobile.BotService6", Process = ":bot6", Exported = false, ForegroundServiceType = ForegroundService.TypeSpecialUse)]
public class BotService6 : BotServiceBase { protected override int Slot => 6; }

/// <summary>The Skua instance of this bot process.</summary>
public static class BotRuntime
{
    private static readonly object _lock = new();
    private static BotController? _controller;
    private static readonly List<string> _early = new();

    public static void Log(string line)
    {
        if (_controller is { } c)
            c.AddLog(line);
        else
            lock (_early)
                _early.Add(line);
    }

    public static void Start(Context context, int slot, Action quit)
    {
        lock (_lock)
        {
            if (_controller is not null)
                return;
            AppFiles.InstallCrashLogging(context, $"bot{slot}", Log);
            AppFiles.EnsureAssets(context);
            AppFiles.ConfigureSkua(context);

            SkuaHost host = SkuaHost.Create($"listen-local:{BotController.GamePort(slot)}");
            BotController controller = new(host, slot);
            if (slot > 0)
                controller.Headless = new HeadlessGame(context, controller.AddLog);
            lock (_early)
            {
                foreach (string line in _early)
                    controller.AddLog(line);
                _early.Clear();
            }
            controller.ShutdownRequested += quit;
            host.Start();
            controller.StartControlServer();
            _controller = controller;

            if (slot == 0)
                _ = IdleWatchAsync(controller, quit);
        }
    }

    /// <summary>The helper bot quits after a few idle minutes so it doesn't hold memory.</summary>
    private static async Task IdleWatchAsync(BotController controller, Action quit)
    {
        while (true)
        {
            await Task.Delay(30000);
            if (DateTime.UtcNow - controller.LastActivityUtc > TimeSpan.FromMinutes(5))
            {
                quit();
                return;
            }
        }
    }
}
