using Android.Content;
using Android.Content.PM;
using Skua.Host;
using System.IO.Compression;

namespace Skua.Mobile;

/// <summary>The one Skua instance of the app (lives in the process, kept alive by SkuaService).</summary>
public static class SkuaRuntime
{
    public const int Port = 5150;
    public const string ArtixPackage = "com.artix.launcher.ruffle";
    public const string ArtixPlayer = "rs.ruffle.PlayerActivity";
    public const string SwfFileName = "skua-mobile.swf";

    private static readonly object _lock = new();
    private static readonly List<string> _log = new();

    public static SkuaHost? Host { get; private set; }

    public static event Action<string>? LogAdded;

    public static IReadOnlyList<string> LogSnapshot()
    {
        lock (_log)
            return _log.ToList();
    }

    public static void AddLog(string line)
    {
        string stamped = $"{DateTime.Now:HH:mm:ss} {line}";
        lock (_log)
        {
            _log.Add(stamped);
            if (_log.Count > 400)
                _log.RemoveRange(0, _log.Count - 400);
        }
        LogAdded?.Invoke(stamped);
    }

    /// <summary>Prepare files and start Skua once. Safe to call from any thread.</summary>
    public static SkuaHost EnsureStarted(Context context)
    {
        lock (_lock)
        {
            if (Host is not null)
                return Host;

            string files = context.FilesDir!.AbsolutePath;
            PrepareAssets(context, files);

            // Android packs assemblies inside the APK, so the script compiler gets its
            // reference DLLs from the extracted refs.zip.
            Skua.Core.AppStartup.Services.ReferenceDirectory = Path.Combine(files, "refs");
            SkuaHost.DefaultFilesDirectory = Path.Combine(files, "defaults");

            AddLog("Menyalakan Skua ...");
            SkuaHost host = SkuaHost.Create($"listen-local:{Port}");
            host.Log += AddLog;
            host.Start();
            Host = host;
            AddLog($"Skua siap. Folder script: {SkuaHost.ScriptsDirectory}");
            Task.Run(() => CompilerCheck(host));
            return host;
        }
    }

    /// <summary>Compile a tiny script once so problems with the compiler setup show up in the log right away.</summary>
    private static void CompilerCheck(SkuaHost host)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            object? script = host.CompileOnly("""
                using Skua.Core.Interfaces;
                public class Script
                {
                    public void ScriptMain(IScriptInterface bot) => bot.Log("ok");
                }
                """);
            AddLog(script is null ? "Compiler: hasil kosong (?)" : $"Compiler OK ({sw.ElapsedMilliseconds} ms)");
        }
        catch (Exception e)
        {
            AddLog($"Compiler GAGAL: {e}");
        }
    }

    /// <summary>Unhandled .NET exceptions go to the log (and logs.txt) instead of vanishing.</summary>
    public static void InstallCrashLogging(Context context)
    {
        string crashFile = Path.Combine(context.FilesDir!.AbsolutePath, "crash.txt");
        if (File.Exists(crashFile))
        {
            AddLog("Crash sebelumnya:\n" + File.ReadAllText(crashFile));
            File.Delete(crashFile);
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.WriteAllText(crashFile, e.ExceptionObject?.ToString() ?? "?"); } catch { }
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AddLog($"Task error: {e.Exception.GetBaseException().Message}");
            e.SetObserved();
        };
        Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
        {
            try { File.WriteAllText(crashFile, e.Exception?.ToString() ?? "?"); } catch { }
        };
    }

    /// <summary>Copy APK assets to files/: the swf, default data, and the compiler references.</summary>
    private static void PrepareAssets(Context context, string files)
    {
        string stamp = AppStamp(context);
        string stampFile = Path.Combine(files, "assets.stamp");
        bool fresh = !File.Exists(stampFile) || File.ReadAllText(stampFile) != stamp;

        Directory.CreateDirectory(Path.Combine(files, "defaults"));
        CopyAsset(context, SwfFileName, Path.Combine(files, SwfFileName), fresh);
        CopyAsset(context, "AdvancedSkills.json", Path.Combine(files, "defaults", "AdvancedSkills.json"), fresh);
        CopyAsset(context, "QuestData.json", Path.Combine(files, "defaults", "QuestData.json"), fresh);

        string refs = Path.Combine(files, "refs");
        if (fresh || !Directory.Exists(refs) || !Directory.EnumerateFiles(refs, "*.dll").Any())
        {
            AddLog("Menyiapkan compiler script (sekali per versi app) ...");
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
                AddLog($"refs.zip tidak ada/rusak, compile script mungkin gagal: {e.Message}");
            }
        }

        File.WriteAllText(stampFile, stamp);
    }

    private static void CopyAsset(Context context, string asset, string target, bool overwrite)
    {
        if (!overwrite && File.Exists(target))
            return;
        try
        {
            using Stream input = context.Assets!.Open(asset);
            using FileStream output = File.Create(target);
            input.CopyTo(output);
        }
        catch (Exception e)
        {
            AddLog($"Asset {asset} gagal disalin: {e.Message}");
        }
    }

    private static string AppStamp(Context context)
    {
        PackageInfo info = context.PackageManager!.GetPackageInfo(context.PackageName!, 0)!;
        return $"{info.VersionName}-{info.LastUpdateTime}";
    }

    public static bool IsArtixInstalled(Context context)
    {
        try
        {
            context.PackageManager!.GetPackageInfo(ArtixPackage, 0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Open skua-mobile.swf in the Artix Games Launcher's player, pointed at this Skua.</summary>
    public static void LaunchGame(Context context)
    {
        // The address also rides in the URL query: Ruffle turns it into loaderInfo.parameters even
        // when the player ignores the FLASHVARS extra for local (content://) files.
        Android.Net.Uri uri = Android.Net.Uri.Parse($"content://{SwfProvider.Authority}/{SwfFileName}?skuaHost=127.0.0.1:{Port}")!;
        Intent intent = new(Intent.ActionView);
        intent.SetDataAndType(uri, "application/x-shockwave-flash");
        intent.SetClassName(ArtixPackage, ArtixPlayer);
        // Artix's PlayerActivity reads these extras (flashvars become loaderInfo.parameters).
        intent.PutExtra("rs.ruffle.FLASHVARS", $"skuaHost=127.0.0.1:{Port}");
        intent.PutExtra("rs.ruffle.CHROMELESS", true);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        context.StartActivity(intent);
    }
}
