using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;

namespace Skua.Mobile;

/// <summary>
/// Foreground service: keeps the process (and Skua with it) alive while the game is in front.
/// Declared in AndroidManifest.xml (foregroundServiceType="specialUse").
/// </summary>
[Register("com.skua.mobile.SkuaService")]
public class SkuaService : Service
{
    private const string Channel = "skua";
    private PowerManager.WakeLock? _wakeLock;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        NotificationManager manager = (NotificationManager)GetSystemService(NotificationService)!;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            manager.CreateNotificationChannel(new NotificationChannel(Channel, "Skua", NotificationImportance.Low));

        Intent open = new(this, typeof(MainActivity));
        PendingIntent pending = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.Immutable)!;
        Notification notification = new Notification.Builder(this, Channel)
            .SetContentTitle("Skua Mobile")
            .SetContentText("Skua aktif. Ketuk untuk membuka kontrol.")
            .SetSmallIcon(Android.Resource.Drawable.IcMediaPlay)
            .SetContentIntent(pending)
            .SetOngoing(true)
            .Build()!;

        if (Build.VERSION.SdkInt >= BuildVersionCodes.UpsideDownCake)
            StartForeground(1, notification, ForegroundService.TypeSpecialUse);
        else
            StartForeground(1, notification);

        if (_wakeLock is null)
        {
            PowerManager power = (PowerManager)GetSystemService(PowerService)!;
            _wakeLock = power.NewWakeLock(WakeLockFlags.Partial, "skua:bot");
            _wakeLock!.Acquire();
        }

        Task.Run(() =>
        {
            try { SkuaRuntime.EnsureStarted(this); }
            catch (Exception e) { SkuaRuntime.AddLog($"Gagal menyalakan Skua: {e}"); }
        });
        return StartCommandResult.Sticky;
    }

    public override void OnDestroy()
    {
        try { _wakeLock?.Release(); } catch { }
        _wakeLock = null;
        base.OnDestroy();
    }

    public static void Start(Context context)
    {
        Intent intent = new(context, typeof(SkuaService));
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            context.StartForegroundService(intent);
        else
            context.StartService(intent);
    }
}
