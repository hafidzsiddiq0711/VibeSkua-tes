using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Text;
using Android.Views;
using Android.Widget;
using Skua.Host;

namespace Skua.Mobile;

[Activity(Label = "Skua Mobile", MainLauncher = true, Exported = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : Activity
{
    private const uint Gold = 0xFFFDAF2D;

    private ISharedPreferences _prefs = null!;
    private EditText _user = null!, _pass = null!, _server = null!;
    private CheckBox _autoLogin = null!, _autoStart = null!;
    private TextView _status = null!, _script = null!, _log = null!;
    private ScrollView _logScroll = null!;
    private Handler _ui = null!;
    private bool _busy;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        SkuaRuntime.InstallCrashLogging(this);
        _prefs = GetSharedPreferences("skua", FileCreationMode.Private)!;
        _ui = new Handler(Looper.MainLooper!);
        SetContentView(BuildUi());

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 1);

        SkuaRuntime.LogAdded += OnLog;
        foreach (string line in SkuaRuntime.LogSnapshot())
            _log.Append(line + "\n");

        SkuaService.Start(this);
        _ui.PostDelayed(RefreshStatus, 500);
    }

    protected override void OnDestroy()
    {
        SkuaRuntime.LogAdded -= OnLog;
        base.OnDestroy();
    }

    // ------------------------------------------------------------------ UI

    private View BuildUi()
    {
        LinearLayout root = new(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Dp(16), Dp(16), Dp(16), Dp(16));
        root.SetBackgroundColor(Color.ParseColor("#101014"));

        TextView title = Label("Skua Mobile", 24, Gold);
        title.SetTypeface(null, TypefaceStyle.Bold);
        root.AddView(title);

        _status = Label("…", 13, 0xFF8CD5FF);
        root.AddView(_status);

        _user = Field("Username AQW", _prefs.GetString("user", "")!, InputTypes.ClassText);
        _pass = Field("Password", _prefs.GetString("pass", "")!, InputTypes.ClassText | InputTypes.TextVariationPassword);
        _server = Field("Server (mis. Twilly)", _prefs.GetString("server", "Twilly")!, InputTypes.ClassText);
        root.AddView(_user);
        root.AddView(_pass);
        root.AddView(_server);

        _autoLogin = Check("Login otomatis saat game siap", _prefs.GetBoolean("autoLogin", true));
        _autoStart = Check("Jalankan script otomatis setelah login", _prefs.GetBoolean("autoStart", false));
        root.AddView(_autoLogin);
        root.AddView(_autoStart);

        _script = Label(ScriptLabel(), 13, 0xFFFFFFFF);
        root.AddView(_script);

        root.AddView(Row(
            Button("Pilih script", PickScript),
            Button("Update scripts", UpdateScripts)));
        root.AddView(Row(
            Button("▶ MAIN (buka game)", Play, primary: true)));
        root.AddView(Row(
            Button("Login", () => _ = LoginAsync(startScriptAfter: false)),
            Button("Start script", () => _ = StartScriptAsync()),
            Button("Stop", () => _ = StopScriptAsync())));
        root.AddView(Row(
            Button("Salin log", CopyLog)));

        _log = Label("", 11, 0xFFC8C8C8);
        _log.SetTextIsSelectable(true);
        _log.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        _logScroll = new ScrollView(this);
        _logScroll.AddView(_log);
        root.AddView(_logScroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));

        ScrollView page = new(this) { FillViewport = true };
        page.AddView(root);
        return page;
    }

    private int Dp(int dp) => (int)(dp * Resources!.DisplayMetrics!.Density + 0.5f);

    private TextView Label(string text, int sp, uint color)
    {
        TextView t = new(this) { Text = text };
        t.SetTextSize(Android.Util.ComplexUnitType.Sp, sp);
        t.SetTextColor(new Color((int)color));
        t.SetPadding(0, Dp(4), 0, Dp(4));
        return t;
    }

    private EditText Field(string hint, string value, InputTypes type)
    {
        EditText e = new(this) { Hint = hint, Text = value, InputType = type };
        e.SetSingleLine(true);
        e.SetTextColor(Color.White);
        e.SetHintTextColor(Color.Gray);
        return e;
    }

    private CheckBox Check(string text, bool value)
    {
        CheckBox c = new(this) { Text = text, Checked = value };
        c.SetTextColor(Color.White);
        return c;
    }

    private Android.Widget.Button Button(string text, Action onClick, bool primary = false)
    {
        Android.Widget.Button b = new(this) { Text = text };
        b.SetAllCaps(false);
        if (primary)
        {
            b.SetBackgroundColor(new Color(unchecked((int)Gold)));
            b.SetTextColor(Color.Black);
        }
        b.Click += (_, _) => onClick();
        return b;
    }

    private LinearLayout Row(params View[] views)
    {
        LinearLayout row = new(this) { Orientation = Orientation.Horizontal };
        foreach (View v in views)
            row.AddView(v, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        return row;
    }

    private string ScriptLabel()
    {
        string s = _prefs.GetString("script", "")!;
        return s.Length == 0 ? "Script: (belum dipilih)" : $"Script: {s}";
    }

    private void OnLog(string line) => _ui.Post(() =>
    {
        _log.Append(line + "\n");
        if (_log.LineCount > 500)
            _log.Text = string.Join("\n", _log.Text!.Split('\n').TakeLast(300)) + "\n";
        _logScroll.Post(() => _logScroll.FullScroll(FocusSearchDirection.Down));
    });

    private void RefreshStatus()
    {
        SkuaHost? host = SkuaRuntime.Host;
        string game = host is null ? "Skua menyala …"
            : !host.GameConnected ? "Game: belum tersambung (tekan MAIN)"
            : !host.GameLoaded ? "Game: tersambung, memuat AQW …"
            : "Game: siap";
        string script = host?.Manager.ScriptRunning == true ? " · Script: jalan" : "";
        _status.Text = game + script;
        _ui.PostDelayed(RefreshStatus, 1000);
    }

    private void SavePrefs()
    {
        _prefs.Edit()!
            .PutString("user", _user.Text?.Trim() ?? "")!
            .PutString("pass", _pass.Text ?? "")!
            .PutString("server", _server.Text?.Trim() ?? "Twilly")!
            .PutBoolean("autoLogin", _autoLogin.Checked)!
            .PutBoolean("autoStart", _autoStart.Checked)!
            .Apply();
    }

    private void CopyLog()
    {
        ClipboardManager clipboard = (ClipboardManager)GetSystemService(ClipboardService)!;
        clipboard.PrimaryClip = ClipData.NewPlainText("Skua Mobile log", string.Join("\n", SkuaRuntime.LogSnapshot()));
        Toast("Log disalin. Tempel (paste) ke chat.");
    }

    private void Toast(string text) => Android.Widget.Toast.MakeText(this, text, ToastLength.Long)!.Show();

    // ------------------------------------------------------------------ actions

    private void Play()
    {
        SavePrefs();
        if (!SkuaRuntime.IsArtixInstalled(this))
        {
            Toast("Artix Games Launcher belum terpasang di HP ini.");
            return;
        }
        SkuaService.Start(this);
        Task.Run(async () =>
        {
            try
            {
                SkuaRuntime.EnsureStarted(this);
                _ui.Post(() =>
                {
                    try { SkuaRuntime.LaunchGame(this); }
                    catch (Exception e) { SkuaRuntime.AddLog($"Gagal membuka Artix: {e.Message}"); }
                });
                if (_autoLogin.Checked && _user.Text?.Trim().Length > 0)
                    await LoginAsync(startScriptAfter: _autoStart.Checked);
            }
            catch (Exception e)
            {
                SkuaRuntime.AddLog($"Error: {e.Message}");
            }
        });
    }

    private async Task LoginAsync(bool startScriptAfter)
    {
        SavePrefs();
        SkuaHost host = SkuaRuntime.EnsureStarted(this);
        string user = _prefs.GetString("user", "")!, pass = _prefs.GetString("pass", "")!, server = _prefs.GetString("server", "Twilly")!;
        if (user.Length == 0)
        {
            SkuaRuntime.AddLog("Isi username & password dulu.");
            return;
        }
        try
        {
            bool ok = await Task.Run(() => host.LoginAsync(user, pass, server));
            if (ok && startScriptAfter)
                await StartScriptAsync();
        }
        catch (Exception e)
        {
            SkuaRuntime.AddLog($"Login error: {e.Message}");
        }
    }

    private async Task StartScriptAsync()
    {
        string rel = _prefs.GetString("script", "")!;
        if (rel.Length == 0)
        {
            SkuaRuntime.AddLog("Pilih script dulu.");
            return;
        }
        SkuaHost host = SkuaRuntime.EnsureStarted(this);
        string path = System.IO.Path.Combine(SkuaHost.ScriptsDirectory, rel);
        try
        {
            await Task.Run(() => host.StartScriptAsync(path));
        }
        catch (Exception e)
        {
            SkuaRuntime.AddLog($"Script error: {e.Message}");
        }
    }

    private async Task StopScriptAsync()
    {
        SkuaHost? host = SkuaRuntime.Host;
        if (host is null)
            return;
        try { await Task.Run(host.StopScriptAsync); }
        catch (Exception e) { SkuaRuntime.AddLog($"Stop error: {e.Message}"); }
    }

    private void UpdateScripts()
    {
        if (_busy)
            return;
        _busy = true;
        SkuaRuntime.AddLog("Update scripts dari GitHub (auqw/Scripts) …");
        Task.Run(async () =>
        {
            try
            {
                SkuaHost host = SkuaRuntime.EnsureStarted(this);
                int last = 0;
                await host.UpdateScriptsAsync(new Progress<string>(p =>
                {
                    // Throttle progress spam.
                    if (System.Environment.TickCount - last > 1500)
                    {
                        last = System.Environment.TickCount;
                        SkuaRuntime.AddLog(p);
                    }
                }));
            }
            catch (Exception e)
            {
                SkuaRuntime.AddLog($"Update gagal: {e.Message}");
            }
            finally
            {
                _busy = false;
            }
        });
    }

    private void PickScript()
    {
        string dir = SkuaHost.ScriptsDirectory;
        List<string> all = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(dir, f))
                .Where(f => !f.StartsWith("Core", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new();
        if (all.Count == 0)
        {
            Toast("Belum ada script. Tekan 'Update scripts' dulu.");
            return;
        }

        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        EditText search = new(this) { Hint = "Cari script (mis. ultra, farm, daily) …" };
        search.SetSingleLine(true);
        ListView list = new(this);
        ArrayAdapter<string> adapter = new(this, Android.Resource.Layout.SimpleListItem1, all.Take(300).ToList());
        list.Adapter = adapter;
        box.AddView(search);
        box.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(420)));

        AlertDialog dialog = new AlertDialog.Builder(this)
            .SetTitle($"Pilih script ({all.Count})")!
            .SetView(box)!
            .SetNegativeButton("Batal", (s, e) => { })!
            .Create()!;

        search.TextChanged += (_, _) =>
        {
            string q = search.Text ?? "";
            adapter.Clear();
            adapter.AddAll(all.Where(f => f.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(300).ToList());
            adapter.NotifyDataSetChanged();
        };
        list.ItemClick += (_, e) =>
        {
            string chosen = adapter.GetItem(e.Position)!;
            _prefs.Edit()!.PutString("script", chosen)!.Apply();
            _script.Text = ScriptLabel();
            dialog.Dismiss();
        };
        dialog.Show();
    }
}
