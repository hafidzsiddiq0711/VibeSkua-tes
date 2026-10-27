using Android.Content;
using Android.Content.PM;
using System.IO.Compression;

namespace Skua.Mobile;

/// <summary>Things both the manager (main process) and the bot processes need.</summary>
public static class AppFiles
{
    public const string ArtixPackage = "com.artix.launcher.ruffle";
    /// <summary>The private patched Artix: PlayerActivity1..6 each in its own process.</summary>
    public const string ArtixSkuaPackage = "com.artix.launcher.ruffle.skua";
    public const string ArtixPlayer = "rs.ruffle.PlayerActivity";
    public const string SwfFileName = "skua-mobile.swf";

    /// <summary>Copy APK assets to files/ once per app version: the swf, default data, compiler references.</summary>
    public static void EnsureAssets(Context context)
    {
        string files = context.FilesDir!.AbsolutePath;
        // Several processes (manager + bots) may start at once: one extracts, the others wait.
        using FileStream lockFile = AcquireLock(Path.Combine(files, "assets.lock"));

        string stamp = AppStamp(context);
        string stampFile = Path.Combine(files, "assets.stamp");
        string refs = Path.Combine(files, "refs");
        bool fresh = !File.Exists(stampFile) || File.ReadAllText(stampFile) != stamp;
        if (!fresh && Directory.Exists(refs) && Directory.EnumerateFiles(refs, "*.dll").Any())
            return;

        Directory.CreateDirectory(Path.Combine(files, "defaults"));
        CopyAsset(context, SwfFileName, Path.Combine(files, SwfFileName));
        CopyAsset(context, "AdvancedSkills.json", Path.Combine(files, "defaults", "AdvancedSkills.json"));
        CopyAsset(context, "QuestData.json", Path.Combine(files, "defaults", "QuestData.json"));

        AppLog.Add("Menyiapkan compiler script (sekali per versi app) ...");
        if (Directory.Exists(refs))
            Directory.Delete(refs, true);
        Directory.CreateDirectory(refs);
        try
        {
            using Stream zip = context.Assets!.Open("refs.zip");
            using ZipArchive archive = new(zip, ZipArchiveMode.Read);
            archive.ExtractToDirectory(refs, true);
        }
        catch (Exception e)
        {
            AppLog.Add($"refs.zip tidak ada/rusak, compile script mungkin gagal: {e.Message}");
        }
        File.WriteAllText(stampFile, stamp);
    }

    /// <summary>Point Skua at the extracted compiler references and default data.</summary>
    public static void ConfigureSkua(Context context)
    {
        string files = context.FilesDir!.AbsolutePath;
        Skua.Core.AppStartup.Services.ReferenceDirectory = Path.Combine(files, "refs");
        Skua.Host.SkuaHost.DefaultFilesDirectory = Path.Combine(files, "defaults");
    }

    private static FileStream AcquireLock(string path)
    {
        for (int i = 0; ; i++)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (i < 600)
            {
                Thread.Sleep(200);
            }
        }
    }

    private static void CopyAsset(Context context, string asset, string target)
    {
        try
        {
            using Stream input = context.Assets!.Open(asset);
            using FileStream output = File.Create(target);
            input.CopyTo(output);
        }
        catch (Exception e)
        {
            AppLog.Add($"Asset {asset} gagal disalin: {e.Message}");
        }
    }

    private static string AppStamp(Context context)
    {
        PackageInfo info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
        return $"{info.VersionName}-{info.LastUpdateTime}";
    }

    public static string VersionName(Context context)
    {
        try { return context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!.VersionName ?? "?"; }
        catch { return "?"; }
    }

    private static bool IsInstalled(Context context, string package)
    {
        try
        {
            context.PackageManager!.GetPackageInfo(package, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsArtixInstalled(Context context) => IsArtixSkuaInstalled(context) || IsInstalled(context, ArtixPackage);

    /// <summary>True when the multi-account Artix (Skua) is installed: one game process per slot.</summary>
    public static bool IsArtixSkuaInstalled(Context context) => IsInstalled(context, ArtixSkuaPackage);

    /// <summary>
    /// The Artix player pointed at bot <paramref name="slot"/>. Each slot is its own document
    /// (own task in Recents), so several games can run at once and reopening one brings it back.
    /// </summary>
    public static Intent GameIntent(Context context, int slot, int renderScale = 100)
    {
        int port = Skua.Host.BotController.GamePort(slot);
        // The address rides in the URL query: Ruffle turns it into loaderInfo.parameters even
        // when the player ignores the FLASHVARS extra for local (content://) files.
        Android.Net.Uri uri = Android.Net.Uri.Parse($"content://{SwfProvider.Authority}/{SwfFileName}?skuaHost=127.0.0.1:{port}&slot={slot}")!;
        Intent intent = new(Intent.ActionView);
        intent.SetDataAndType(uri, "application/x-shockwave-flash");
        if (IsArtixSkuaInstalled(context))
        {
            // Own process per slot, so games don't freeze each other.
            intent.SetClassName(ArtixSkuaPackage, slot > 0 ? $"{ArtixPlayer}{slot}" : ArtixPlayer);
            if (renderScale is > 0 and < 100)
                intent.PutExtra("skua.RENDER_SCALE", renderScale);
        }
        else
        {
            intent.SetClassName(ArtixPackage, ArtixPlayer);
        }
        intent.PutExtra("rs.ruffle.FLASHVARS", $"skuaHost=127.0.0.1:{port}");
        intent.PutExtra("rs.ruffle.CHROMELESS", true);
        intent.PutExtra("rs.ruffle.GAME_NAME", "aqw");
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask | ActivityFlags.NewDocument);
        return intent;
    }

    public static void LaunchGame(Context context, int slot, int renderScale = 100) =>
        context.StartActivity(GameIntent(context, slot, renderScale));

    /// <summary>Unhandled .NET exceptions go to a crash file shown on the next start.</summary>
    public static void InstallCrashLogging(Context context, string name, Action<string> log)
    {
        string crashFile = Path.Combine(context.FilesDir!.AbsolutePath, $"crash-{name}.txt");
        if (File.Exists(crashFile))
        {
            log("Crash sebelumnya:\n" + File.ReadAllText(crashFile));
            File.Delete(crashFile);
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.WriteAllText(crashFile, e.ExceptionObject?.ToString() ?? "?"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            log($"Task error: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            try { File.WriteAllText(crashFile, e.Exception?.ToString() ?? "?"); } catch { }
        };
    }
}

/// <summary>Messages of the manager itself (bots keep their own logs).</summary>
public static class AppLog
{
    private static readonly List<string> _lines = new();

    public static event Action<string>? Added;

    public static void Add(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (_lines)
        {
            _lines.Add(stamped);
            if (_lines.Count > 300)
                _lines.RemoveRange(0, _lines.Count - 300);
        }
        Added?.Invoke(stamped);
    }

    public static IReadOnlyList<string> Snapshot()
    {
        lock (_lines)
            return _lines.ToList();
    }
}
