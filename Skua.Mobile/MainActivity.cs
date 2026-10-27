using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Provider;
using Android.Text;
using Android.Views;
using Android.Widget;
using Newtonsoft.Json.Linq;
using Skua.Host;

namespace Skua.Mobile;

/// <summary>The manager: accounts, groups, Start/Stop All, options (like VibeSkua.Manager).</summary>
[Activity(Label = "Skua Mobile", MainLauncher = true, Exported = true, Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : Activity
{
    private const string AllGroups = "Semua grup";

    private Handler _ui = null!;
    private LinearLayout _list = null!;
    private TextView _summary = null!;
    private Spinner _groupSpinner = null!;
    private readonly List<string> _groupItems = new();
    private readonly Dictionary<string, TextView> _statusViews = new();
    private readonly Dictionary<string, TextView> _statusDots = new();
    private string _group = AllGroups;
    private bool _visible;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _ui = new Handler(Looper.MainLooper!);
        AppFiles.InstallCrashLogging(this, "manager", AppLog.Add);
        AccountStore.Init(this);
        BotHub.Init(this);
        Window!.SetStatusBarColor(Ui.Background);
        SetContentView(BuildUi());

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 1);

        // Extract the compiler files now so the first bot starts faster.
        Task.Run(() =>
        {
            try { AppFiles.EnsureAssets(this); }
            catch (Exception e) { AppLog.Add($"Aset gagal disiapkan: {e.Message}"); }
        });

        AccountStore.Changed += OnAccountsChanged;
        BotHub.Changed += OnBotsChanged;
        RebuildList();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _visible = true;
        RefreshStatus();
    }

    protected override void OnPause()
    {
        _visible = false;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        AccountStore.Changed -= OnAccountsChanged;
        BotHub.Changed -= OnBotsChanged;
        base.OnDestroy();
    }

    private void OnAccountsChanged() => _ui.Post(RebuildList);

    private void OnBotsChanged()
    {
        if (_visible)
            _ui.Post(RefreshStatus);
    }

    // ------------------------------------------------------------------ layout

    private View BuildUi()
    {
        LinearLayout root = new(this) { Orientation = Orientation.Vertical };
        root.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12));
        root.SetBackgroundColor(Ui.Background);

        // Header: logo, name, version, summary
        LinearLayout header = new(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        ImageView logo = new(this);
        logo.SetImageResource(Resource.Mipmap.ic_launcher);
        header.AddView(logo, new LinearLayout.LayoutParams(Ui.Dp(this, 52), Ui.Dp(this, 52)));
        LinearLayout titles = new(this) { Orientation = Orientation.Vertical };
        titles.SetPadding(Ui.Dp(this, 10), 0, 0, 0);
        titles.AddView(Ui.Label(this, "Skua Mobile", 22, Ui.Gold, bold: true));
        _summary = Ui.Label(this, $"v{AppFiles.VersionName(this)}", 12, Ui.Muted);
        titles.AddView(_summary);
        header.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        header.AddView(Ui.Button(this, "Log", ShowAppLog, sp: 12));
        root.AddView(header);

        // Group filter
        _groupSpinner = new Spinner(this);
        _groupSpinner.ItemSelected += (_, e) =>
        {
            if (e.Position >= 0 && e.Position < _groupItems.Count && _groupItems[e.Position] != _group)
            {
                _group = _groupItems[e.Position];
                RebuildList();
            }
        };
        root.AddView(_groupSpinner);

        root.AddView(Ui.Row(this,
            Ui.Button(this, "▶ Start All", StartAll, primary: true),
            Ui.Button(this, "■ Stop All", StopAll)));
        root.AddView(Ui.Row(this,
            Ui.Button(this, "+ Akun", () => EditAccount(null)),
            Ui.Button(this, "Script ke grup", ScriptToGroup),
            Ui.Button(this, "⟳ Update scripts", UpdateScripts)));

        _list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        ScrollView scroll = new(this) { FillViewport = true };
        scroll.AddView(_list);
        root.AddView(scroll, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1f));
        return root;
    }

    private List<Account> Visible() =>
        AccountStore.All.Where(a => _group == AllGroups || string.Equals(a.Group, _group, StringComparison.OrdinalIgnoreCase)).ToList();

    private void RebuildList()
    {
        _groupItems.Clear();
        _groupItems.Add(AllGroups);
        _groupItems.AddRange(AccountStore.Groups());
        if (!_groupItems.Contains(_group))
            _group = AllGroups;
        _groupSpinner.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, _groupItems);
        _groupSpinner.SetSelection(_groupItems.IndexOf(_group));

        _list.RemoveAllViews();
        _statusViews.Clear();
        _statusDots.Clear();
        List<Account> accounts = Visible();
        if (accounts.Count == 0)
        {
            TextView empty = Ui.Label(this, "Belum ada akun. Tekan \"+ Akun\" untuk menambah akun AQW.", 14, Ui.Muted);
            empty.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 24), Ui.Dp(this, 8), 0);
            _list.AddView(empty);
        }
        foreach (Account account in accounts)
            _list.AddView(Card(account));
        RefreshStatus();
    }

    private View Card(Account account)
    {
        LinearLayout card = new(this) { Orientation = Orientation.Vertical };
        card.Background = Ui.Rounded(Ui.Card, 14, this, Ui.CardBorder);
        card.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 10), Ui.Dp(this, 12), Ui.Dp(this, 8));
        LinearLayout.LayoutParams lp = new(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.SetMargins(0, Ui.Dp(this, 6), 0, Ui.Dp(this, 6));
        card.LayoutParameters = lp;

        LinearLayout top = new(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        TextView dot = Ui.Label(this, "●", 14, Ui.Muted);
        dot.SetPadding(0, 0, Ui.Dp(this, 6), 0);
        top.AddView(dot);
        top.AddView(Ui.Label(this, account.Title, 16, Ui.Text, bold: true), new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        top.AddView(Ui.Label(this, $"{account.Server} · {account.Group}", 12, Ui.Muted));
        card.AddView(top);

        TextView status = Ui.Label(this, "", 13, Ui.Info);
        card.AddView(status);
        string script = account.Script.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(account.Script) : "(belum pilih script)";
        card.AddView(Ui.Label(this, $"Script: {script}{(account.AutoStart ? "  · auto-start" : "")}", 12, Ui.Muted));

        card.AddView(Ui.Row(this,
            Ui.Button(this, "▶ Main", () => Play(account), primary: true),
            Ui.Button(this, "■ Stop", () => Send(account, "stop", "Script dihentikan.")),
            Ui.Button(this, "Game", () => OpenGame(account)),
            Ui.Button(this, "⚙", () => AccountMenu(account))));

        _statusViews[account.Id] = status;
        _statusDots[account.Id] = dot;
        return card;
    }

    private void RefreshStatus()
    {
        foreach (Account account in AccountStore.All)
        {
            if (!_statusViews.TryGetValue(account.Id, out TextView? view))
                continue;
            BotState? state = BotHub.StateOf(account);
            TextView dot = _statusDots[account.Id];
            if (state is null)
            {
                view.Text = "Mati";
                dot.SetTextColor(Ui.Muted);
                continue;
            }
            view.Text = state.Describe();
            bool running = state.Alive && state.Status.Value<bool?>("running") == true;
            bool playing = state.Alive && state.Status.Value<bool?>("playing") == true;
            dot.SetTextColor(running ? Ui.Good : playing ? Ui.Gold : state.Alive ? Ui.Info : Ui.Bad);
        }
        _summary.Text = $"v{AppFiles.VersionName(this)} · {AccountStore.All.Count} akun · {BotHub.RunningCount} bot jalan";
    }

    // ------------------------------------------------------------------ account actions

    private void Play(Account account)
    {
        if (!CheckArtix())
            return;
        if (account.User.Length == 0)
        {
            Ui.Toast(this, "Isi username & password dulu (⚙ → Edit akun).");
            return;
        }
        Ui.Toast(this, $"Menyalakan {account.Title} …");
        Run(async () => await BotHub.PlayAsync(this, account), $"Main {account.Title}");
    }

    private void OpenGame(Account account)
    {
        if (BotHub.SlotOf(account) is int slot)
            AppFiles.LaunchGame(this, slot);
        else
            Ui.Toast(this, "Bot akun ini belum menyala. Tekan ▶ Main.");
    }

    private void Send(Account account, string cmd, string done, int timeoutMs = 20000)
    {
        if (BotHub.SlotOf(account) is null)
        {
            Ui.Toast(this, "Bot akun ini belum menyala.");
            return;
        }
        Run(async () =>
        {
            await BotHub.SendAsync(account, cmd, null, timeoutMs);
            _ui.Post(() => Ui.Toast(this, done));
        }, cmd);
    }

    private void AccountMenu(Account account)
    {
        (string Label, Action Action)[] items =
        {
            ("Edit akun", () => EditAccount(account)),
            ("Pilih script", () => PickScript(path =>
            {
                account.Script = path;
                AccountStore.Save();
                Reconfigure(account);
            })),
            ("Opsi Skua (Lag Killer, Hide Players, FPS …)", () => SkuaOptions(account)),
            ("Opsi CoreBots (class, room, delay …)", () => CoreBotsOptions(account)),
            ("Opsi script", () => ScriptOptions(account)),
            ("Jalankan script", () => Send(account, "start", "Script dijalankan.", 180000)),
            ("Login ulang", () => Send(account, "login", "Login selesai.", 120000)),
            ("Lihat log bot", () => ShowBotLog(account)),
            ("Matikan bot", () => Run(async () => await BotHub.ShutdownAsync(account), "Matikan bot")),
            ("Naik ↑", () => AccountStore.Move(account.Id, -1)),
            ("Turun ↓", () => AccountStore.Move(account.Id, 1)),
            ("Hapus akun", () => ConfirmDelete(account)),
        };
        Ui.Dialog(this, account.Title)
            .SetItems(items.Select(i => i.Label).ToArray(), (_, e) => items[e.Which].Action())!
            .SetNegativeButton("Tutup", (_, _) => { })!
            .Show();
    }

    private void ConfirmDelete(Account account)
    {
        Ui.Dialog(this, $"Hapus {account.Title}?")
            .SetMessage("Akun dihapus dari Skua Mobile (bot-nya dimatikan dulu).")!
            .SetPositiveButton("Hapus", (_, _) => Run(async () =>
            {
                await BotHub.ShutdownAsync(account);
                AccountStore.Remove(account.Id);
            }, "Hapus akun"))!
            .SetNegativeButton("Batal", (_, _) => { })!
            .Show();
    }

    private void Reconfigure(Account account)
    {
        if (BotHub.SlotOf(account) is not null)
            Run(() => BotHub.ConfigureAsync(account), "Kirim profil");
    }

    private void EditAccount(Account? existing)
    {
        Account account = existing ?? new Account { Group = _group == AllGroups ? "Default" : _group };
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Ui.Dp(this, 18), Ui.Dp(this, 8), Ui.Dp(this, 18), 0);
        EditText name = Ui.Field(this, "Nama tampilan (opsional)", account.Name);
        EditText user = Ui.Field(this, "Username AQW", account.User);
        EditText pass = Ui.Field(this, "Password", account.Pass, InputTypes.ClassText | InputTypes.TextVariationPassword);
        AutoCompleteTextView group = new(this) { Hint = "Grup (mis. Army, Farm)", Text = account.Group };
        group.SetSingleLine(true);
        group.SetTextColor(Ui.Text);
        group.Threshold = 0;
        group.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleDropDownItem1Line, AccountStore.Groups().ToList());
        string server = account.Server;
        Android.Widget.Button serverButton = null!;
        serverButton = Ui.Button(this, $"Server: {server} ▾", () => PickServer(s =>
        {
            server = s;
            serverButton.Text = $"Server: {server} ▾";
        }));
        CheckBox autoStart = new(this) { Text = "Jalankan script otomatis setelah login", Checked = account.AutoStart };
        autoStart.SetTextColor(Ui.Text);
        box.AddView(name);
        box.AddView(user);
        box.AddView(pass);
        box.AddView(group);
        box.AddView(serverButton);
        box.AddView(autoStart);

        Ui.Dialog(this, existing is null ? "Akun baru" : "Edit akun")
            .SetView(box)!
            .SetPositiveButton("Simpan", (_, _) =>
            {
                account.Name = name.Text?.Trim() ?? "";
                account.User = user.Text?.Trim() ?? "";
                account.Pass = pass.Text ?? "";
                account.Group = string.IsNullOrWhiteSpace(group.Text) ? "Default" : group.Text!.Trim();
                account.Server = server;
                account.AutoStart = autoStart.Checked;
                AccountStore.Upsert(account);
                Reconfigure(account);
            })!
            .SetNegativeButton("Batal", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ options

    private void SkuaOptions(Account account)
    {
        Run(async () =>
        {
            Dictionary<string, string> shown = OptionCatalog.SkuaOptions.ToDictionary(d => d.Key, d => d.Default);
            if (BotHub.StateOf(account)?.Alive == true)
            {
                // Show what the bot uses right now.
                JToken? live = await BotHub.SendAsync(account, "skuaOptions.get");
                foreach (JProperty p in (live as JObject)?.Properties() ?? Enumerable.Empty<JProperty>())
                    shown[p.Name] = p.Value.ToString();
            }
            foreach (var kv in account.SkuaOptions)
                shown[kv.Key] = kv.Value;
            _ui.Post(() => Ui.ShowOptions(this, $"Opsi Skua · {account.Title}", OptionCatalog.SkuaOptions, shown, result =>
            {
                Dictionary<string, string> changed = result
                    .Where(kv => !string.Equals(shown.GetValueOrDefault(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var kv in changed)
                    account.SkuaOptions[kv.Key] = kv.Value;
                AccountStore.Save();
                if (changed.Count > 0 && BotHub.StateOf(account)?.Alive == true)
                    Run(async () => await BotHub.SendAsync(account, "skuaOptions.set", new JObject { ["values"] = JObject.FromObject(changed) }), "Opsi Skua");
                Ui.Toast(this, changed.Count == 0 ? "Tidak ada perubahan." : $"{changed.Count} opsi disimpan.");
            }, "Dipakai setelah login. Untuk HP, Lag Killer + Sembunyikan pemain + FPS rendah bikin hemat baterai & RAM."));
        }, "Opsi Skua");
    }

    private void CoreBotsOptions(Account account)
    {
        if (account.User.Length == 0)
        {
            Ui.Toast(this, "Isi username dulu: opsi CoreBots disimpan per username.");
            return;
        }
        Dictionary<string, string> values = OptionCatalog.ReadCbo(account.User);
        Ui.ShowOptions(this, $"Opsi CoreBots · {account.User}", OptionCatalog.CoreBotsOptions, values, result =>
        {
            try
            {
                OptionCatalog.WriteCbo(account.User, result);
                Ui.Toast(this, "Opsi CoreBots disimpan. Dipakai saat script dijalankan berikutnya.");
            }
            catch (Exception e)
            {
                Ui.Toast(this, $"Gagal menyimpan: {e.Message}");
            }
        }, "Sama dengan CoreBots Options di VibeSkua PC (file CBO_Storage). Nama class harus persis seperti di game, mis. \"Legion Revenant\".");
    }

    private void ScriptOptions(Account account)
    {
        if (account.Script.Length == 0)
        {
            Ui.Toast(this, "Pilih script dulu.");
            return;
        }
        Ui.Toast(this, "Membaca opsi script (compile dulu, bisa ~1 menit) …");
        Run(async () =>
        {
            BotClient bot = await BotHub.HelperAsync();
            JArray items = (JArray)(await bot.SendAsync("scriptOptions.get", new JObject { ["script"] = account.Script }, 300000))!;
            if (items.Count == 0)
            {
                _ui.Post(() => Ui.Toast(this, "Script ini tidak punya opsi."));
                return;
            }
            List<OptionDef> defs = new();
            Dictionary<string, string> values = new();
            foreach (JToken item in items)
            {
                string category = item.Value<string>("category") ?? "";
                string key = (category.Length > 0 ? category + "/" : "") + item.Value<string>("name");
                defs.Add(new OptionDef(key, item.Value<string>("label") ?? key, item.Value<string>("type") ?? "string", item.Value<string>("value") ?? "",
                    category.Length > 0 ? category : "Opsi", item["choices"]?.Select(c => c.ToString()).ToArray(), item.Value<string>("description") ?? ""));
                values[key] = item.Value<string>("value") ?? "";
            }
            _ui.Post(() => Ui.ShowOptions(this, $"Opsi {System.IO.Path.GetFileNameWithoutExtension(account.Script)}", defs, values, result =>
            {
                Run(async () =>
                {
                    await bot.SendAsync("scriptOptions.set", new JObject { ["script"] = account.Script, ["values"] = JObject.FromObject(result) }, 300000);
                    _ui.Post(() => Ui.Toast(this, "Opsi script disimpan (berlaku untuk semua akun yang pakai script ini)."));
                }, "Simpan opsi script");
            }));
        }, "Opsi script");
    }

    // ------------------------------------------------------------------ group actions

    private void StartAll()
    {
        if (!CheckArtix())
            return;
        List<Account> accounts = Visible().Where(a => a.User.Length > 0).ToList();
        if (accounts.Count == 0)
        {
            Ui.Toast(this, "Tidak ada akun di grup ini.");
            return;
        }
        if (accounts.Count > BotHub.MaxSlots)
            Ui.Toast(this, $"Maksimal {BotHub.MaxSlots} akun sekaligus; yang lain dilewati.");

        bool canLaunchFromBackground = Settings.CanDrawOverlays(this);
        if (accounts.Count > 1 && !canLaunchFromBackground)
        {
            Ui.Dialog(this, "Izin buka banyak game")
                .SetMessage("Supaya Start All bisa membuka semua game sendiri, izinkan Skua Mobile \"Tampil di atas aplikasi lain\". " +
                            "Tanpa izin ini, game pertama dibuka otomatis dan sisanya dibuka dengan mengetuk notifikasi bot masing-masing.")!
                .SetPositiveButton("Buka pengaturan", (_, _) =>
                    StartActivity(new Intent(Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse($"package:{PackageName}"))))!
                .SetNegativeButton("Lanjut tanpa izin", (_, _) => StartAllNow(accounts.Take(BotHub.MaxSlots).ToList(), false))!
                .Show();
            return;
        }
        StartAllNow(accounts.Take(BotHub.MaxSlots).ToList(), canLaunchFromBackground);
    }

    private void StartAllNow(List<Account> accounts, bool launchAll)
    {
        Ui.Toast(this, $"Start All: {accounts.Count} akun …");
        Run(async () =>
        {
            // Bots first (while the manager is in front), then the games one by one.
            List<(Account Account, int Slot)> started = new();
            foreach (Account account in accounts)
            {
                try
                {
                    started.Add((account, await BotHub.StartBotAsync(account)));
                }
                catch (Exception e)
                {
                    AppLog.Add($"{account.Title}: {e.Message}");
                }
            }
            for (int i = 0; i < started.Count; i++)
            {
                if (i > 0 && !launchAll)
                    break;
                int slot = started[i].Slot;
                _ui.Post(() =>
                {
                    try { AppFiles.LaunchGame(this, slot); }
                    catch (Exception e) { AppLog.Add($"Game {slot}: {e.Message}"); }
                });
                // Let each game load before the next one starts (memory and CPU).
                if (i < started.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(25));
            }
            if (!launchAll && started.Count > 1)
                AppLog.Add("Game akun lain: ketuk notifikasi \"Bot N\" masing-masing.");
        }, "Start All");
    }

    private void StopAll()
    {
        List<Account> accounts = Visible().Where(a => BotHub.SlotOf(a) is not null).ToList();
        if (accounts.Count == 0)
        {
            Ui.Toast(this, "Tidak ada bot yang menyala di grup ini.");
            return;
        }
        Ui.Dialog(this, $"Stop All ({accounts.Count} bot)")
            .SetItems(new[] { "Hentikan script semua", "Matikan semua bot" }, (_, e) =>
            {
                bool shutdown = e.Which == 1;
                Run(async () =>
                {
                    foreach (Account account in accounts)
                    {
                        try
                        {
                            if (shutdown)
                                await BotHub.ShutdownAsync(account);
                            else
                                await BotHub.SendAsync(account, "stop");
                        }
                        catch (Exception ex)
                        {
                            AppLog.Add($"{account.Title}: {ex.Message}");
                        }
                    }
                    _ui.Post(() => Ui.Toast(this, shutdown ? "Semua bot dimatikan." : "Semua script dihentikan."));
                }, "Stop All");
            })!
            .SetNegativeButton("Batal", (_, _) => { })!
            .Show();
    }

    private void ScriptToGroup()
    {
        List<Account> accounts = Visible();
        if (accounts.Count == 0)
            return;
        PickScript(path =>
        {
            Ui.Dialog(this, System.IO.Path.GetFileNameWithoutExtension(path))
                .SetMessage($"Pasang script ini ke {accounts.Count} akun ({_group})?")!
                .SetPositiveButton("Pasang & jalankan", (_, _) => Assign(true))!
                .SetNeutralButton("Pasang saja", (_, _) => Assign(false))!
                .SetNegativeButton("Batal", (_, _) => { })!
                .Show();

            void Assign(bool start)
            {
                foreach (Account a in accounts)
                    a.Script = path;
                AccountStore.Save();
                Run(async () =>
                {
                    foreach (Account a in accounts.Where(a => BotHub.SlotOf(a) is not null))
                    {
                        try
                        {
                            await BotHub.ConfigureAsync(a);
                            if (start)
                                await BotHub.SendAsync(a, "start", new JObject { ["script"] = path }, 180000);
                        }
                        catch (Exception e)
                        {
                            AppLog.Add($"{a.Title}: {e.Message}");
                        }
                    }
                }, "Script ke grup");
            }
        });
    }

    private void UpdateScripts()
    {
        Ui.Toast(this, "Update scripts dari GitHub (auqw/Scripts) …");
        Run(async () =>
        {
            BotClient bot = await BotHub.HelperAsync();
            JToken? count = await bot.SendAsync("updateScripts", null, 30 * 60 * 1000);
            _ui.Post(() => Ui.Toast(this, $"Script diperbarui: {count} file."));
            AppLog.Add($"Update scripts selesai: {count} file.");
        }, "Update scripts");
    }

    // ------------------------------------------------------------------ pickers

    private void PickServer(Action<string> chosen)
    {
        Ui.Toast(this, "Mengambil daftar server …");
        Run(async () =>
        {
            BotClient bot = await BotHub.HelperAsync();
            JArray servers = (JArray)(await bot.SendAsync("servers", null, 30000))!;
            List<JToken> list = servers.Where(s => s.Value<string>("Name") is { Length: > 0 }).ToList();
            _ui.Post(() =>
            {
                if (list.Count == 0)
                {
                    Ui.Toast(this, "Daftar server belum bisa diambil.");
                    return;
                }
                string[] labels = list.Select(s =>
                    $"{s.Value<string>("Name")}  ({s.Value<int>("PlayerCount")}/{s.Value<int>("MaxPlayers")})" +
                    (s.Value<bool>("Upgrade") ? " · Member" : "") + (s.Value<bool>("Online") ? "" : " · offline")).ToArray();
                Ui.Dialog(this, $"Pilih server ({list.Count})")
                    .SetItems(labels, (_, e) => chosen(list[e.Which].Value<string>("Name")!))!
                    .SetNegativeButton("Batal", (_, _) => { })!
                    .Show();
            });
        }, "Daftar server");
    }

    private void PickScript(Action<string> chosen)
    {
        string dir = SkuaHost.ScriptsDirectory;
        List<string> all = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(dir, f))
                .Where(f => !f.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("Cached-Scripts", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new();
        if (all.Count == 0)
        {
            Ui.Toast(this, "Belum ada script. Tekan \"⟳ Update scripts\" dulu.");
            return;
        }

        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        EditText search = Ui.Field(this, "Cari script (mis. ultra, farm, daily) …", "");
        ListView list = new(this);
        ArrayAdapter<string> adapter = new(this, Android.Resource.Layout.SimpleListItem1, all.Take(300).ToList());
        list.Adapter = adapter;
        box.AddView(search);
        box.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 420)));

        AlertDialog dialog = Ui.Dialog(this, $"Pilih script ({all.Count})")
            .SetView(box)!
            .SetNegativeButton("Batal", (_, _) => { })!
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
            chosen(adapter.GetItem(e.Position)!);
            dialog.Dismiss();
        };
        dialog.Show();
    }

    // ------------------------------------------------------------------ logs

    private void ShowBotLog(Account account) =>
        ShowText($"Log bot · {account.Title}", string.Join("\n", BotHub.LogOf(account).TakeLast(300)));

    private void ShowAppLog() =>
        ShowText("Log Skua Mobile", string.Join("\n", AppLog.Snapshot()));

    private void ShowText(string title, string text)
    {
        TextView view = Ui.Label(this, text.Length > 0 ? text : "(kosong)", 11, Ui.Text);
        view.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        view.SetTextIsSelectable(true);
        view.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 8), Ui.Dp(this, 14), Ui.Dp(this, 8));
        ScrollView scroll = new(this);
        scroll.AddView(view);
        scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
        Ui.Dialog(this, title)
            .SetView(scroll)!
            .SetPositiveButton("Salin", (_, _) =>
            {
                Android.Content.ClipboardManager clipboard = (Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
                clipboard.PrimaryClip = ClipData.NewPlainText(title, text);
                Ui.Toast(this, "Log disalin. Tempel (paste) ke chat.");
            })!
            .SetNegativeButton("Tutup", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ helpers

    private bool CheckArtix()
    {
        if (AppFiles.IsArtixInstalled(this))
            return true;
        Ui.Toast(this, "Artix Games Launcher belum terpasang di HP ini.");
        return false;
    }

    /// <summary>Run work off the UI thread; errors go to a toast and the app log.</summary>
    private void Run(Func<Task> work, string what)
    {
        Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception e)
            {
                string message = e.GetBaseException().Message;
                AppLog.Add($"{what}: {message}");
                _ui.Post(() => Ui.Toast(this, $"{what}: {message}"));
            }
        });
    }
}
