using Android.Content;
using Skua.Host;
using System.Runtime.InteropServices;

namespace Skua.Mobile;

/// <summary>
/// Headless game in this bot process: libskua_ruffle.so (Ruffle without a screen, sound or GPU)
/// plays skua-mobile.swf, which connects to this bot like a game in the Artix player would.
/// </summary>
public sealed class HeadlessGame : IHeadlessGame
{
    private const string Lib = "skua_ruffle";

    // Android 14+ keeps system CAs in the Conscrypt APEX; older versions in /system.
    private const string CaDirs = "/apex/com.android.conscrypt/cacerts:/system/etc/security/cacerts";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void LogCallback(IntPtr message);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int skua_ruffle_start(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string swfPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string skuaHost,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string caDirs,
        LogCallback log);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern void skua_ruffle_stop();

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    private static extern int skua_ruffle_running();

    private readonly string _swfPath;
    private readonly Action<string> _log;
    // Kept alive for as long as native code may call it.
    private static LogCallback? _callback;

    public HeadlessGame(Context context, Action<string> log)
    {
        _swfPath = Path.Combine(context.FilesDir!.AbsolutePath, AppFiles.SwfFileName);
        _log = log;
    }

    public bool Running
    {
        get
        {
            try { return skua_ruffle_running() == 1; }
            catch { return false; }
        }
    }

    public bool Start(int gamePort)
    {
        try
        {
            _callback ??= message => _log(Marshal.PtrToStringUTF8(message) ?? "");
            int result = skua_ruffle_start(_swfPath, $"127.0.0.1:{gamePort}", CaDirs, _callback);
            return result >= 0;
        }
        catch (Exception e)
        {
            _log($"Headless engine unavailable: {e.Message}");
            return false;
        }
    }

    public void Stop()
    {
        try { skua_ruffle_stop(); }
        catch { }
    }
}
