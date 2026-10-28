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

/// <summary>The manager: accounts, groups, Start/Stop all, watch or headless, options.</summary>
[Activity(Label = "Skua Mobile", MainLauncher = true, Exported = true, Icon = "@mipmap/ic_launcher", RoundIcon = "@mipmap/ic_launcher_round",
    Theme = "@android:style/Theme.DeviceDefault.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    WindowSoftInputMode = SoftInput.AdjustResize)]
public class MainActivity : Activity
{
    private const string AllGroups = "All";

    private Handler _ui = null!;
    private LinearLayout _list = null!, _chips = null!, _banner = null!;
    private TextView _subtitle = null!, _bots = null!, _freeRam = null!, _botRam = null!, _accountsHeader = null!;
    private readonly Dictionary<string, CardViews> _cards = new();
    private string _group = AllGroups;
    private bool _visible;

    private sealed record CardViews(View Accent, TextView Avatar, TextView Status, TextView Meta, TextView Activity, TextView ModeChip, Android.Widget.Button ModeButton);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _ui = new Handler(Looper.MainLooper!);
        AppFiles.InstallCrashLogging(this, "manager", AppLog.Add);
        AccountStore.Init(this);
        BotHub.Init(this);
        Window!.SetStatusBarColor(Ui.Background);
        Window!.SetNavigationBarColor(Ui.Background);
        SetContentView(BuildUi());

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
            RequestPermissions(new[] { Android.Manifest.Permission.PostNotifications }, 1);

        // Extract the compiler files now so the first bot starts faster.
        Task.Run(() =>
        {
            try { AppFiles.EnsureAssets(this); }
            catch (Exception e) { AppLog.Add($"Could not prepare app files: {e.Message}"); }
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
        RefreshBanner();
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
        root.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 14), Ui.Dp(this, 16), Ui.Dp(this, 24));

        // Header
        LinearLayout header = new(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        ImageView logo = new(this);
        logo.SetImageResource(Resource.Mipmap.ic_launcher);
        header.AddView(logo, new LinearLayout.LayoutParams(Ui.Dp(this, 46), Ui.Dp(this, 46)));
        LinearLayout titles = new(this) { Orientation = Orientation.Vertical };
        titles.SetPadding(Ui.Dp(this, 12), 0, 0, 0);
        titles.AddView(Ui.Label(this, "Skua Mobile", 22, Ui.Gold, bold: true));
        _subtitle = Ui.Label(this, "", 12, Ui.Muted);
        titles.AddView(_subtitle);
        header.AddView(titles, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        Android.Widget.Button menu = Ui.Button(this, "⋯", AppMenu, sp: 18);
        header.AddView(menu, new LinearLayout.LayoutParams(Ui.Dp(this, 48), Ui.Dp(this, 44)));
        root.AddView(header);

        // Background access warning
        _banner = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        _banner.SetGravity(GravityFlags.CenterVertical);
        _banner.Background = Ui.Rounded(Color.Argb(36, 253, 175, 45), 14, this, Color.Argb(110, 253, 175, 45));
        _banner.SetPadding(Ui.Dp(this, 14), Ui.Dp(this, 10), Ui.Dp(this, 10), Ui.Dp(this, 10));
        TextView bannerText = Ui.Label(this, "Let Skua Mobile run in the background, or the phone may stop your bots when the app is closed.", 12, Ui.Text);
        _banner.AddView(bannerText, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        _banner.AddView(Ui.Button(this, "Fix", BackgroundAccess, primary: true, sp: 12), new LinearLayout.LayoutParams(Ui.Dp(this, 64), Ui.Dp(this, 38)));
        root.AddView(_banner, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 14, 0, 0));

        // Stats
        var (botsTile, bots) = Ui.Tile(this, "Bots", "0 / 6");
        var (ramTile, freeRam) = Ui.Tile(this, "Free RAM", "–");
        var (botRamTile, botRam) = Ui.Tile(this, "Bots use", "–");
        (_bots, _freeRam, _botRam) = (bots, freeRam, botRam);
        LinearLayout stats = new(this) { Orientation = Orientation.Horizontal };
        stats.AddView(botsTile, Ui.Margins(this, 0, ViewGroup.LayoutParams.WrapContent, 0, 0, 4, 0, 1));
        stats.AddView(ramTile, Ui.Margins(this, 0, ViewGroup.LayoutParams.WrapContent, 4, 0, 4, 0, 1));
        stats.AddView(botRamTile, Ui.Margins(this, 0, ViewGroup.LayoutParams.WrapContent, 4, 0, 0, 0, 1));
        root.AddView(stats, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 14, 0, 0));

        // Group chips
        HorizontalScrollView chipScroll = new(this) { HorizontalScrollBarEnabled = false };
        _chips = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        chipScroll.AddView(_chips);
        root.AddView(chipScroll, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 14, 0, 4));

        // Group actions
        LinearLayout actions = Ui.Row(this,
            Ui.Button(this, "▶  Start all", StartAll, primary: true, sp: 14),
            Ui.Button(this, "■  Stop all", StopAll, sp: 14),
            Ui.Button(this, "⟳  Scripts", UpdateScripts, sp: 14));
        root.AddView(actions, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, -3, 4, -3, 0));

        // Accounts
        LinearLayout listHeader = new(this) { Orientation = Orientation.Horizontal };
        listHeader.SetGravity(GravityFlags.CenterVertical);
        _accountsHeader = Ui.Label(this, "ACCOUNTS", 11, Ui.Muted, bold: true);
        listHeader.AddView(_accountsHeader, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        TextView servers = Ui.Label(this, "Servers", 13, Ui.Gold, bold: true);
        servers.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 8), Ui.Dp(this, 14), Ui.Dp(this, 8));
        servers.Click += (_, _) => ManageServers();
        listHeader.AddView(servers);
        TextView add = Ui.Label(this, "+ Add account", 13, Ui.Gold, bold: true);
        add.SetPadding(Ui.Dp(this, 8), Ui.Dp(this, 8), 0, Ui.Dp(this, 8));
        add.Click += (_, _) => EditAccount(null);
        listHeader.AddView(add);
        root.AddView(listHeader, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 14, 0, 0));

        _list = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.AddView(_list);

        ScrollView page = new(this) { FillViewport = true };
        page.SetBackgroundColor(Ui.Background);
        page.AddView(root);
        return page;
    }

    private List<Account> Visible() =>
        AccountStore.All.Where(a => _group == AllGroups || string.Equals(a.Group, _group, StringComparison.OrdinalIgnoreCase)).ToList();

    private void RebuildList()
    {
        List<string> groups = AccountStore.Groups().ToList();
        if (_group != AllGroups && !groups.Contains(_group, StringComparer.OrdinalIgnoreCase))
            _group = AllGroups;
        _chips.RemoveAllViews();
        foreach (string g in new[] { AllGroups }.Concat(groups))
        {
            string group = g;
            _chips.AddView(Ui.FilterChip(this, group, string.Equals(group, _group, StringComparison.OrdinalIgnoreCase), () =>
            {
                _group = group;
                RebuildList();
            }), Ui.Margins(this, ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent, 0, 0, 8, 0));
        }

        _list.RemoveAllViews();
        _cards.Clear();
        List<Account> accounts = Visible();
        _accountsHeader.Text = $"ACCOUNTS · {accounts.Count}";
        if (accounts.Count == 0)
        {
            LinearLayout empty = new(this) { Orientation = Orientation.Vertical };
            empty.SetGravity(GravityFlags.CenterHorizontal);
            empty.Background = Ui.Rounded(Ui.Surface, 16, this, Ui.Border);
            empty.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 28), Ui.Dp(this, 20), Ui.Dp(this, 28));
            empty.AddView(Ui.Label(this, "No accounts yet", 16, Ui.Text, bold: true));
            empty.AddView(Ui.Label(this, "Add your AQW accounts to run them here.", 13, Ui.Muted));
            empty.AddView(Ui.Button(this, "+ Add account", () => EditAccount(null), primary: true),
                Ui.Margins(this, Ui.Dp(this, 200), ViewGroup.LayoutParams.WrapContent, 0, 14, 0, 0));
            _list.AddView(empty, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 8, 0, 0));
        }
        foreach (Account account in accounts)
            _list.AddView(Card(account));
        RefreshStatus();
    }

    private View Card(Account account)
    {
        LinearLayout card = new(this) { Orientation = Orientation.Horizontal };
        card.Background = Ui.Rounded(Ui.Surface, 16, this, Ui.Border);
        card.ClipToOutline = true;
        card.LayoutParameters = Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 6, 0, 6);

        View accent = new(this);
        accent.SetBackgroundColor(Ui.Muted);
        card.AddView(accent, new LinearLayout.LayoutParams(Ui.Dp(this, 4), ViewGroup.LayoutParams.MatchParent));

        LinearLayout body = new(this) { Orientation = Orientation.Vertical };
        body.SetPadding(Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 12), Ui.Dp(this, 10));
        card.AddView(body, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));

        LinearLayout top = new(this) { Orientation = Orientation.Horizontal };
        top.SetGravity(GravityFlags.CenterVertical);
        TextView avatar = Ui.Avatar(this, account.Title, Ui.Muted);
        top.AddView(avatar, new LinearLayout.LayoutParams(Ui.Dp(this, 40), Ui.Dp(this, 40)));
        LinearLayout names = new(this) { Orientation = Orientation.Vertical };
        names.SetPadding(Ui.Dp(this, 12), 0, Ui.Dp(this, 8), 0);
        TextView name = Ui.Label(this, account.Title, 16, Ui.Text, bold: true);
        name.SetSingleLine(true);
        name.Ellipsize = TextUtils.TruncateAt.End;
        names.AddView(name);
        names.AddView(Ui.Label(this, $"{account.Server} · {account.Group}", 12, Ui.Muted));
        top.AddView(names, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f));
        TextView modeChip = Ui.Chip(this, account.Headless ? "HEADLESS" : "WINDOW", account.Headless ? Ui.Night : Ui.Info);
        top.AddView(modeChip);
        body.AddView(top);

        TextView status = Ui.Label(this, "", 13, Ui.Info);
        status.SetPadding(0, Ui.Dp(this, 10), 0, 0);
        body.AddView(status);
        TextView meta = Ui.Label(this, "", 12, Ui.Muted);
        meta.SetPadding(0, Ui.Dp(this, 2), 0, 0);
        body.AddView(meta);
        // The bot's latest log line: what it is doing right now.
        TextView activity = Ui.Label(this, "", 11, Ui.Muted);
        activity.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        activity.SetMaxLines(2);
        activity.Ellipsize = TextUtils.TruncateAt.End;
        activity.SetPadding(0, Ui.Dp(this, 4), 0, Ui.Dp(this, 6));
        body.AddView(activity);

        Android.Widget.Button modeButton = Ui.Button(this, "", () => ModeButton(account), sp: 12);
        LinearLayout buttons = Ui.Row(this,
            Ui.Button(this, "▶ Play", () => Play(account), primary: true, sp: 12),
            Ui.Button(this, "■ Stop", () => Send(account, "stop", "Script stopped."), sp: 12),
            modeButton,
            Ui.Button(this, "⋯", () => AccountMenu(account), sp: 15));
        body.AddView(buttons, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, -3, 0, -3, 0));

        _cards[account.Id] = new CardViews(accent, avatar, status, meta, activity, modeChip, modeButton);
        return card;
    }

    private void RefreshStatus()
    {
        long botMb = 0;
        foreach (Account account in AccountStore.All)
        {
            BotState? state = BotHub.StateOf(account);
            if (state?.Alive == true)
                botMb += state.Status.Value<long?>("memMb") ?? 0;
            if (!_cards.TryGetValue(account.Id, out CardViews? views))
                continue;

            bool alive = state?.Alive == true;
            bool running = alive && state!.Status.Value<bool?>("running") == true;
            bool playing = alive && state!.Status.Value<bool?>("playing") == true;
            Color color = state is null ? Ui.Muted : running ? Ui.Good : playing ? Ui.Gold : alive ? Ui.Info : Ui.Bad;
            views.Accent.SetBackgroundColor(color);
            ((Android.Graphics.Drawables.GradientDrawable)views.Avatar.Background!).SetColor(color);
            views.Status.Text = state is null ? "Offline" : state.Describe();
            views.Status.SetTextColor(state is null ? Ui.Muted : color.ToArgb() == Ui.Muted.ToArgb() ? Ui.Info : color);

            List<string> meta = new();
            meta.Add(account.Script.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(account.Script) : "No script");
            if (account.AutoStart)
                meta.Add("auto-start");
            if (!account.Headless && account.RenderScale < 100)
                meta.Add($"{account.RenderScale}% res");
            if (alive && state!.Status.Value<long?>("memMb") is long mb)
                meta.Add($"bot {mb} MB");
            // Is it really farming? XP and gold per hour over the last minutes.
            if (alive && state!.Rate() is { Length: > 0 } rate)
                meta.Add(rate);
            views.Meta.Text = string.Join("  ·  ", meta);
            string last = alive ? state!.Status.Value<string>("lastLog") ?? "" : "";
            // "04:11:50 [04:11:50] (SetOptions) Bot Started" -> "04:11:50 (SetOptions) Bot Started"
            last = System.Text.RegularExpressions.Regex.Replace(last, @"^(\d\d:\d\d:\d\d) \[\d\d:\d\d:\d\d\] ", "$1 ").Replace('\n', ' ');
            views.Activity.Text = last;
            views.Activity.Visibility = last.Length > 0 ? ViewStates.Visible : ViewStates.Gone;

            if (account.Remote)
            {
                views.ModeChip.Text = (ServerStore.Find(account.ServerId)?.Name ?? "SERVER").ToUpperInvariant();
                views.ModeChip.SetTextColor(Ui.Gold);
                views.ModeChip.Background = Ui.Rounded(Color.Argb(40, 253, 175, 45), 20, this);
                views.ModeButton.Text = "Move";
            }
            else
            {
                views.ModeChip.Text = account.Headless ? "HEADLESS" : "WINDOW";
                views.ModeChip.SetTextColor(account.Headless ? Ui.Night : Ui.Info);
                views.ModeChip.Background = Ui.Rounded(account.Headless ? Color.Argb(40, 180, 140, 255) : Color.Argb(40, 108, 196, 255), 20, this);
                views.ModeButton.Text = account.Headless ? "Watch" : "Headless";
            }
        }

        ActivityManager am = (ActivityManager)GetSystemService(ActivityService)!;
        ActivityManager.MemoryInfo memory = new();
        am.GetMemoryInfo(memory);
        _freeRam.Text = $"{memory.AvailMem / 1e9:F1} / {memory.TotalMem / 1e9:F0} GB";
        _freeRam.SetTextColor(memory.LowMemory ? Ui.Bad : Ui.Text);
        _botRam.Text = botMb > 0 ? (botMb >= 1024 ? $"{botMb / 1024.0:F1} GB" : $"{botMb} MB") : "–";
        var (phoneBots, serverBots) = BotHub.RunningCount;
        _bots.Text = serverBots > 0 ? $"{phoneBots} + {serverBots} VPS" : $"{phoneBots} / {BotHub.MaxSlots}";

        string engine = AppFiles.IsArtixSkuaInstalled(this) ? "Artix (Skua)" : AppFiles.IsArtixInstalled(this) ? "Artix (1 window)" : "no Artix";
        _subtitle.Text = $"v{AppFiles.VersionName(this)}  ·  {engine}  ·  headless ready";
    }

    private void RefreshBanner()
    {
        PowerManager power = (PowerManager)GetSystemService(PowerService)!;
        _banner.Visibility = power.IsIgnoringBatteryOptimizations(PackageName) ? ViewStates.Gone : ViewStates.Visible;
    }

    // ------------------------------------------------------------------ account actions

    private void Play(Account account)
    {
        if (account.User.Length == 0)
        {
            Ui.Toast(this, "Add the username and password first (⋯ → Edit account).");
            return;
        }
        if (!account.Headless && !CheckArtix())
            return;
        if (!account.Headless && !AppFiles.IsArtixSkuaInstalled(this) && BotHub.RunningCount.Phone > 0 && BotHub.SlotOf(account) is null)
            Ui.Toast(this, "Stock Artix runs one game at a time: the other window will freeze. Use Artix (Skua) or headless.");
        else
            Ui.Toast(this, account.Headless ? $"Starting {account.Title} headless …" : $"Starting {account.Title} …");
        Run(async () => await BotHub.PlayAsync(this, account), $"Play {account.Title}");
    }

    private void ModeButton(Account account)
    {
        if (account.Remote)
            MoveAccount(account);
        else
            ToggleMode(account);
    }

    /// <summary>Run the account on the phone or on one of the servers.</summary>
    private void MoveAccount(Account account)
    {
        List<(string Id, string Label)> places = new() { ("", "This phone") };
        places.AddRange(ServerStore.All.Select(s => (s.Id, $"{s.Name} ({s.Host})")));
        if (places.Count == 1)
        {
            Ui.Toast(this, "Add a Skua Server first (⋯ → Servers).");
            return;
        }
        Ui.Dialog(this, $"Run {account.Title} on …")
            .SetItems(places.Select(p => (p.Id == account.ServerId ? "✓ " : "") + p.Label).ToArray(), (_, e) =>
            {
                string target = places[e.Which].Id;
                if (target == account.ServerId)
                    return;
                Ui.Toast(this, $"Moving {account.Title} …");
                Run(async () =>
                {
                    bool wasRunning = BotHub.StateOf(account)?.Alive == true;
                    await BotHub.MoveAsync(account, target);
                    if (wasRunning)
                        await BotHub.PlayAsync(this, account);
                }, "Move account");
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private void ToggleMode(Account account)
    {
        bool headless = !account.Headless;
        if (!headless && !CheckArtix())
            return;
        Ui.Toast(this, headless ? $"{account.Title}: switching to headless …" : $"{account.Title}: opening the game window …");
        Run(async () => await BotHub.SetModeAsync(this, account, headless), "Switch mode");
        RefreshStatus();
    }

    private void Send(Account account, string cmd, string done, int timeoutMs = 20000)
    {
        if (BotHub.SlotOf(account) is null)
        {
            Ui.Toast(this, "This account's bot isn't running. Tap ▶ Play.");
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
            ("Edit account", () => EditAccount(account)),
            ("Run on phone / server …", () => MoveAccount(account)),
            ("Choose script", () => PickScript(account, path =>
            {
                account.Script = path;
                AccountStore.Save();
                Reconfigure(account);
            })),
            ("Run script", () => Send(account, "start", "Script started.", 180000)),
            ("Open game window", () => OpenGame(account)),
            ("Eco preset (lightest settings)", () => ApplyEco(new[] { account })),
            ("Skua options (Lag Killer, FPS, rooms …)", () => SkuaOptions(account)),
            ("CoreBots options (classes, delays …)", () => CoreBotsOptions(account)),
            ("Script options", () => ScriptOptions(account)),
            ("Log in again", () => Send(account, "login", "Logged in.", 120000)),
            ("Bot log", () => ShowBotLog(account)),
            ("Server log (full, incl. game)", () => Run(async () =>
            {
                string text = await BotHub.ServerLogAsync(account);
                _ui.Post(() => ShowText($"Server log · {account.Title}", text));
            }, "Server log")),
            ("Stop bot", () => Run(async () => await BotHub.ShutdownAsync(account), "Stop bot")),
            ("Move up", () => AccountStore.Move(account.Id, -1)),
            ("Move down", () => AccountStore.Move(account.Id, 1)),
            ("Delete account", () => ConfirmDelete(account)),
        };
        if (!account.Remote)
            items = items.Where(i => !i.Label.StartsWith("Server log", StringComparison.Ordinal)).ToArray();
        Ui.Dialog(this, account.Title)
            .SetItems(items.Select(i => i.Label).ToArray(), (_, e) => items[e.Which].Action())!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    private void OpenGame(Account account)
    {
        if (account.Remote)
        {
            Ui.Toast(this, "Server accounts play headless. Move it to the phone to watch it.");
            return;
        }
        if (account.Headless)
        {
            ToggleMode(account);
            return;
        }
        if (BotHub.SlotOf(account) is int slot)
            AppFiles.LaunchGame(this, slot, account.RenderScale);
        else
            Ui.Toast(this, "This account's bot isn't running. Tap ▶ Play.");
    }

    private void ConfirmDelete(Account account)
    {
        Ui.Dialog(this, $"Delete {account.Title}?")
            .SetMessage("The account is removed from Skua Mobile (its bot is stopped first).")!
            .SetPositiveButton("Delete", (_, _) => Run(async () =>
            {
                await BotHub.ShutdownAsync(account);
                AccountStore.Remove(account.Id);
            }, "Delete account"))!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private void Reconfigure(Account account)
    {
        if (BotHub.SlotOf(account) is not null)
            Run(() => BotHub.ConfigureAsync(account), "Send profile");
    }

    private void EditAccount(Account? existing)
    {
        Account account = existing ?? new Account { Group = _group == AllGroups ? "Default" : _group };
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 8), Ui.Dp(this, 20), 0);
        EditText name = Ui.Field(this, "Display name (optional)", account.Name);
        EditText user = Ui.Field(this, "AQW username", account.User);
        EditText pass = Ui.Field(this, "Password", account.Pass, InputTypes.ClassText | InputTypes.TextVariationPassword);
        AutoCompleteTextView group = new(this) { Hint = "Group (e.g. Army, Farm)", Text = account.Group };
        group.SetSingleLine(true);
        group.SetTextColor(Ui.Text);
        group.SetHintTextColor(Ui.Muted);
        group.Threshold = 0;
        group.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleDropDownItem1Line, AccountStore.Groups().ToList());
        string server = account.Server;
        Android.Widget.Button serverButton = null!;
        serverButton = Ui.Button(this, $"Server: {server} ▾", () => PickServer(s =>
        {
            server = s;
            serverButton.Text = $"Server: {server} ▾";
        }));

        List<(string Id, string Label)> places = new() { ("", "This phone") };
        places.AddRange(ServerStore.All.Select(s => (s.Id, $"Server: {s.Name}")));
        Spinner where = new(this);
        where.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, places.Select(p => p.Label).ToList());
        where.SetSelection(Math.Max(0, places.FindIndex(p => p.Id == account.ServerId)));

        string[] modes = { "Game window (watch it)", "Headless (no window, lightest)" };
        Spinner mode = new(this);
        mode.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, modes);
        mode.SetSelection(account.Headless ? 1 : 0);

        int[] scales = { 100, 75, 50, 35 };
        string[] scaleLabels = { "Window resolution 100% (sharp)", "Window resolution 75%", "Window resolution 50% (light)", "Window resolution 35% (lightest)" };
        Spinner scale = new(this);
        scale.Adapter = new ArrayAdapter<string>(this, Android.Resource.Layout.SimpleSpinnerDropDownItem, scaleLabels);
        int scaleIndex = Array.IndexOf(scales, account.RenderScale);
        scale.SetSelection(scaleIndex >= 0 ? scaleIndex : 0);

        Switch autoStart = new(this) { Text = "Start the script after logging in", Checked = account.AutoStart };
        autoStart.SetTextColor(Ui.Text);

        box.AddView(name);
        box.AddView(user);
        box.AddView(pass);
        box.AddView(group);
        box.AddView(serverButton, Ui.Margins(this, ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent, 0, 8, 0, 4));
        if (places.Count > 1)
        {
            box.AddView(Ui.Caption(this, "Runs on"));
            box.AddView(where);
        }
        box.AddView(Ui.Caption(this, "How to play (on the phone)"));
        box.AddView(mode);
        box.AddView(scale);
        if (!AppFiles.IsArtixSkuaInstalled(this))
            box.AddView(Ui.Label(this, "Window resolution needs Artix (Skua).", 11, Ui.Muted));
        box.AddView(autoStart);

        ScrollView scroll = new(this);
        scroll.AddView(box);
        Ui.Dialog(this, existing is null ? "New account" : "Edit account")
            .SetView(scroll)!
            .SetPositiveButton("Save", (_, _) =>
            {
                account.Name = name.Text?.Trim() ?? "";
                account.User = user.Text?.Trim() ?? "";
                account.Pass = pass.Text ?? "";
                account.Group = string.IsNullOrWhiteSpace(group.Text) ? "Default" : group.Text!.Trim();
                account.Server = server;
                account.AutoStart = autoStart.Checked;
                account.RenderScale = scales[Math.Clamp(scale.SelectedItemPosition, 0, scales.Length - 1)];
                bool headless = mode.SelectedItemPosition == 1;
                string target = places[Math.Clamp(where.SelectedItemPosition, 0, places.Count - 1)].Id;
                if (existing is null)
                    account.ServerId = target;
                bool modeChanged = !account.Remote && headless != account.Headless;
                AccountStore.Upsert(account);
                if (target != account.ServerId)
                {
                    Ui.Toast(this, $"Moving {account.Title} …");
                    Run(async () =>
                    {
                        bool wasRunning = BotHub.StateOf(account)?.Alive == true;
                        await BotHub.MoveAsync(account, target);
                        if (wasRunning)
                            await BotHub.PlayAsync(this, account);
                    }, "Move account");
                    return;
                }
                Reconfigure(account);
                if (modeChanged)
                {
                    if (BotHub.SlotOf(account) is not null)
                        ToggleMode(account);
                    else
                    {
                        account.Mode = headless ? "headless" : "watch";
                        AccountStore.Save();
                    }
                }
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ options

    private static readonly Dictionary<string, string> EcoOptions = new()
    {
        ["LagKiller"] = "true",
        ["HidePlayers"] = "true",
        ["OptimizePlayers"] = "true",
        // Not DisableFX: it walks every animation each frame (costs more than it saves headless,
        // and throws errors from armor/hair clips).
        ["DisableFX"] = "false",
        ["SkipCutscenes"] = "true",
        ["DisableDeathAds"] = "true",
    };

    /// <summary>Lightest settings: Lag Killer and friends, low FPS, low window resolution.</summary>
    private void ApplyEco(IReadOnlyList<Account> accounts)
    {
        foreach (Account a in accounts)
        {
            foreach (var kv in EcoOptions)
                a.SkuaOptions[kv.Key] = kv.Value;
            a.SkuaOptions["SetFPS"] = a.Headless ? "6" : "20";
            a.RenderScale = Math.Min(a.RenderScale, 50);
        }
        AccountStore.Save();
        Run(async () =>
        {
            foreach (Account a in accounts.Where(a => BotHub.StateOf(a)?.Alive == true))
                await BotHub.SendAsync(a, "skuaOptions.set", new JObject { ["values"] = JObject.FromObject(a.SkuaOptions) });
        }, "Eco preset");
        Ui.Toast(this, accounts.Count == 1
            ? "Eco preset on. Window resolution changes the next time the game window opens."
            : $"Eco preset on for {accounts.Count} accounts.");
    }

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
            _ui.Post(() => Ui.ShowOptions(this, $"Skua options · {account.Title}", OptionCatalog.SkuaOptions, shown, result =>
            {
                Dictionary<string, string> changed = result
                    .Where(kv => !string.Equals(shown.GetValueOrDefault(kv.Key), kv.Value, StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
                foreach (var kv in changed)
                    account.SkuaOptions[kv.Key] = kv.Value;
                AccountStore.Save();
                if (changed.Count > 0 && BotHub.StateOf(account)?.Alive == true)
                    Run(async () => await BotHub.SendAsync(account, "skuaOptions.set", new JObject { ["values"] = JObject.FromObject(changed) }), "Skua options");
                Ui.Toast(this, changed.Count == 0 ? "Nothing changed." : $"{changed.Count} option(s) saved.");
            }, "Applied after login. On a phone, Lag Killer + Hide other players + a low FPS limit save the most battery and RAM."));
        }, "Skua options");
    }

    private void CoreBotsOptions(Account account)
    {
        if (account.User.Length == 0)
        {
            Ui.Toast(this, "Add the username first: CoreBots options are saved per username.");
            return;
        }
        const string note = "Same as CoreBots Options in VibeSkua on PC (the CBO_Storage file). Class names must match the game exactly, e.g. \"Legion Revenant\".";
        if (account.Remote)
        {
            // The CBO file lives on the server.
            Run(async () =>
            {
                BotClient server = await BotHub.HelperAsync(account);
                Dictionary<string, string> remote = (await server.SendAsync("cbo.get", new JObject { ["user"] = account.User }, 150000))?
                    .ToObject<Dictionary<string, string>>() ?? new();
                _ui.Post(() => Ui.ShowOptions(this, $"CoreBots options · {account.User}", OptionCatalog.CoreBotsOptions, remote, result =>
                    Run(async () =>
                    {
                        await server.SendAsync("cbo.set", new JObject { ["user"] = account.User, ["values"] = JObject.FromObject(result) }, 60000);
                        _ui.Post(() => Ui.Toast(this, "CoreBots options saved on the server."));
                    }, "CoreBots options"), note));
            }, "CoreBots options");
            return;
        }
        Dictionary<string, string> values = OptionCatalog.ReadCbo(account.User);
        Ui.ShowOptions(this, $"CoreBots options · {account.User}", OptionCatalog.CoreBotsOptions, values, result =>
        {
            try
            {
                OptionCatalog.WriteCbo(account.User, result);
                Ui.Toast(this, "CoreBots options saved. Used the next time a script starts.");
            }
            catch (Exception e)
            {
                Ui.Toast(this, $"Could not save: {e.Message}");
            }
        }, note);
    }

    private void ScriptOptions(Account account)
    {
        if (account.Script.Length == 0)
        {
            Ui.Toast(this, "Choose a script first.");
            return;
        }
        Ui.Toast(this, "Reading the script's options (compiling first, up to a minute) …");
        Run(async () =>
        {
            BotClient bot = await BotHub.HelperAsync(account);
            JArray items = (JArray)(await bot.SendAsync("scriptOptions.get", new JObject { ["script"] = account.Script }, 300000))!;
            if (items.Count == 0)
            {
                _ui.Post(() => Ui.Toast(this, "This script has no options."));
                return;
            }
            List<OptionDef> defs = new();
            Dictionary<string, string> values = new();
            foreach (JToken item in items)
            {
                string category = item.Value<string>("category") ?? "";
                string key = (category.Length > 0 ? category + "/" : "") + item.Value<string>("name");
                defs.Add(new OptionDef(key, item.Value<string>("label") ?? key, item.Value<string>("type") ?? "string", item.Value<string>("value") ?? "",
                    category.Length > 0 ? category : "Options", item["choices"]?.Select(c => c.ToString()).ToArray(), item.Value<string>("description") ?? ""));
                values[key] = item.Value<string>("value") ?? "";
            }
            _ui.Post(() => Ui.ShowOptions(this, System.IO.Path.GetFileNameWithoutExtension(account.Script), defs, values, result =>
            {
                Run(async () =>
                {
                    await bot.SendAsync("scriptOptions.set", new JObject { ["script"] = account.Script, ["values"] = JObject.FromObject(result) }, 300000);
                    _ui.Post(() => Ui.Toast(this, "Script options saved (for every account using this script)."));
                }, "Save script options");
            }));
        }, "Script options");
    }

    // ------------------------------------------------------------------ app menu

    private void AppMenu()
    {
        (string Label, Action Action)[] items =
        {
            ("Servers (PC / VPS)", ManageServers),
            ("Update scripts", UpdateScripts),
            ("Script for this group", ScriptToGroup),
            ("Eco preset for this group", () => ApplyEco(Visible())),
            ("Background access", BackgroundAccess),
            ("App log", ShowAppLog),
        };
        Ui.Dialog(this, "Skua Mobile")
            .SetItems(items.Select(i => i.Label).ToArray(), (_, e) => items[e.Which].Action())!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    /// <summary>The settings that keep bots alive when the app is closed (and open game windows).</summary>
    private void BackgroundAccess()
    {
        (string Label, Action Action)[] items =
        {
            ("1. Battery: no restrictions", () =>
            {
                try
                {
                    StartActivity(new Intent(Settings.ActionRequestIgnoreBatteryOptimizations, Android.Net.Uri.Parse($"package:{PackageName}")));
                }
                catch
                {
                    StartActivity(new Intent(Settings.ActionIgnoreBatteryOptimizationSettings));
                }
            }),
            ("2. Autostart (Xiaomi / Redmi / POCO)", () =>
            {
                try
                {
                    Intent miui = new();
                    miui.SetComponent(new ComponentName("com.miui.securitycenter", "com.miui.permcenter.autostart.AutoStartManagementActivity"));
                    StartActivity(miui);
                }
                catch
                {
                    StartActivity(new Intent(Settings.ActionApplicationDetailsSettings, Android.Net.Uri.Parse($"package:{PackageName}")));
                }
            }),
            ("3. Open game windows from the background", () =>
                StartActivity(new Intent(Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse($"package:{PackageName}")))),
            ("4. Lock Skua Mobile in Recent apps", () =>
                Ui.Toast(this, "Open Recent apps, long-press the Skua Mobile card and tap the lock, like the Artix windows. Then closing the app doesn't stop the bots.")),
        };
        Ui.Dialog(this, "Keep bots running")
            .SetItems(items.Select(i => i.Label).ToArray(), (_, e) => items[e.Which].Action())!
            .SetNegativeButton("Done", (_, _) => RefreshBanner())!
            .Show();
    }

    // ------------------------------------------------------------------ group actions

    private void StartAll()
    {
        List<Account> accounts = Visible().Where(a => a.User.Length > 0).ToList();
        if (accounts.Count == 0)
        {
            Ui.Toast(this, "No accounts in this group.");
            return;
        }
        if (accounts.Count > BotHub.MaxSlots)
            Ui.Toast(this, $"At most {BotHub.MaxSlots} accounts at once; the rest are skipped.");
        accounts = accounts.Take(BotHub.MaxSlots).ToList();
        int windows = accounts.Count(a => !a.Headless);
        if (windows > 0 && !CheckArtix())
            return;
        if (windows > 1 && !AppFiles.IsArtixSkuaInstalled(this))
        {
            Ui.Dialog(this, "Needs Artix (Skua)")
                .SetMessage("Stock Artix runs one game at a time: a second window freezes the first. Install Artix (Skua), or switch the other accounts to headless.")!
                .SetPositiveButton("OK", (_, _) => { })!
                .Show();
            return;
        }
        bool canLaunchFromBackground = Settings.CanDrawOverlays(this);
        if (windows > 1 && !canLaunchFromBackground)
        {
            Ui.Dialog(this, "Open all game windows?")
                .SetMessage("To open every game window by itself, allow Skua Mobile to \"display over other apps\". " +
                            "Without it, the first window opens and you open the others by tapping each bot's notification.")!
                .SetPositiveButton("Open settings", (_, _) =>
                    StartActivity(new Intent(Settings.ActionManageOverlayPermission, Android.Net.Uri.Parse($"package:{PackageName}"))))!
                .SetNegativeButton("Continue", (_, _) => StartAllNow(accounts, false))!
                .Show();
            return;
        }
        StartAllNow(accounts, canLaunchFromBackground);
    }

    private void StartAllNow(List<Account> accounts, bool launchAll)
    {
        Ui.Toast(this, $"Starting {accounts.Count} account(s) …");
        Run(async () =>
        {
            // Bots first (while the manager is in front), headless games right away,
            // then the game windows one by one.
            List<(Account Account, int Slot)> windows = new();
            foreach (Account account in accounts)
            {
                try
                {
                    int slot = await BotHub.StartBotAsync(account);
                    if (account.Headless)
                        await BotHub.SendAsync(account, "headless.start");
                    else
                        windows.Add((account, slot));
                }
                catch (Exception e)
                {
                    AppLog.Add($"{account.Title}: {e.Message}");
                }
            }
            for (int i = 0; i < windows.Count; i++)
            {
                if (i > 0 && !launchAll)
                    break;
                int slot = windows[i].Slot;
                int scale = windows[i].Account.RenderScale;
                _ui.Post(() =>
                {
                    try { AppFiles.LaunchGame(this, slot, scale); }
                    catch (Exception e) { AppLog.Add($"Window {slot}: {e.Message}"); }
                });
                // Let each game load before the next one starts (memory and CPU).
                if (i < windows.Count - 1)
                    await Task.Delay(TimeSpan.FromSeconds(25));
            }
            if (!launchAll && windows.Count > 1)
                AppLog.Add("Other game windows: tap each \"Bot N\" notification.");
        }, "Start all");
    }

    private void StopAll()
    {
        List<Account> accounts = Visible().Where(a => BotHub.SlotOf(a) is not null).ToList();
        if (accounts.Count == 0)
        {
            Ui.Toast(this, "No bots running in this group.");
            return;
        }
        Ui.Dialog(this, $"Stop all ({accounts.Count})")
            .SetItems(new[] { "Stop every script", "Stop every bot" }, (_, e) =>
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
                    _ui.Post(() => Ui.Toast(this, shutdown ? "All bots stopped." : "All scripts stopped."));
                }, "Stop all");
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    private void ScriptToGroup()
    {
        List<Account> accounts = Visible();
        if (accounts.Count == 0)
            return;
        Account? place = accounts.Select(a => a.ServerId).Distinct().Count() == 1 ? accounts[0] : null;
        PickScript(place, path =>
        {
            Ui.Dialog(this, System.IO.Path.GetFileNameWithoutExtension(path))
                .SetMessage($"Use this script for {accounts.Count} account(s) ({_group})?")!
                .SetPositiveButton("Use and start", (_, _) => Assign(true))!
                .SetNeutralButton("Use only", (_, _) => Assign(false))!
                .SetNegativeButton("Cancel", (_, _) => { })!
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
                }, "Script for group");
            }
        });
    }

    private void UpdateScripts()
    {
        Ui.Toast(this, "Updating scripts from GitHub (auqw/Scripts) …");
        Run(async () =>
        {
            BotClient bot = await BotHub.HelperAsync();
            JToken? count = await bot.SendAsync("updateScripts", null, 30 * 60 * 1000);
            _ui.Post(() => Ui.Toast(this, $"Scripts updated: {count} file(s)."));
            AppLog.Add($"Scripts updated: {count} file(s).");
        }, "Update scripts");
        // Servers keep their own copy of the scripts.
        foreach (RemoteServer server in ServerStore.All)
        {
            Run(async () =>
            {
                JToken? count = await BotHub.ServerHelper(server).SendAsync("updateScripts", null, 30 * 60 * 1000);
                AppLog.Add($"Scripts updated on {server.Name}: {count} file(s).");
            }, $"Update scripts on {server.Name}");
        }
    }

    // ------------------------------------------------------------------ servers

    private void ManageServers()
    {
        List<RemoteServer> servers = ServerStore.All;
        List<string> items = servers.Select(s => $"{s.Name}  ·  {s.Host}:{s.Port}").ToList();
        items.Add("+ Add a server");
        Ui.Dialog(this, "Skua Servers")
            .SetItems(items.ToArray(), (_, e) =>
            {
                if (e.Which == servers.Count)
                    EditServer(null);
                else
                    ServerMenu(servers[e.Which]);
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    private void ServerMenu(RemoteServer server)
    {
        (string Label, Action Action)[] items =
        {
            ("Test connection", () => TestServer(server)),
            ("Edit", () => EditServer(server)),
            ("Remove", () =>
            {
                if (AccountStore.All.Any(a => a.ServerId == server.Id))
                {
                    Ui.Toast(this, "Move its accounts to the phone or another server first.");
                    return;
                }
                ServerStore.Remove(server.Id);
                RebuildList();
            }),
        };
        Ui.Dialog(this, server.Name)
            .SetItems(items.Select(i => i.Label).ToArray(), (_, e) => items[e.Which].Action())!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    private void TestServer(RemoteServer server)
    {
        Ui.Toast(this, $"Connecting to {server.Host}:{server.Port} …");
        Run(async () =>
        {
            JObject info = await BotHub.ServerInfoAsync(server);
            int running = info["running"]?.Count() ?? 0;
            string text = $"{info.Value<string>("name")}: {info.Value<string>("os")}\n" +
                          $"{info.Value<int>("cpus")} CPUs, {info.Value<long>("memoryMb") / 1024.0:F0} GB RAM\n" +
                          $"{running} bot(s) running, up to {info.Value<int>("maxSlots")}";
            _ui.Post(() => Ui.Dialog(this, "Connected ✓").SetMessage(text)!.SetPositiveButton("OK", (_, _) => { })!.Show());
        }, "Server");
    }

    private void EditServer(RemoteServer? existing)
    {
        RemoteServer server = existing ?? new RemoteServer();
        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Ui.Dp(this, 20), Ui.Dp(this, 8), Ui.Dp(this, 20), 0);
        box.AddView(Ui.Label(this, "Run SkuaServer on the PC/VPS (start-skua-server.bat). Use its Tailscale IP, and the token shown in its window.", 12, Ui.Muted));
        EditText name = Ui.Field(this, "Name (e.g. VPS)", server.Name);
        EditText host = Ui.Field(this, "Address (Tailscale IP, e.g. 100.101.102.103)", server.Host, InputTypes.ClassText | InputTypes.TextVariationUri);
        EditText port = Ui.Field(this, "Port", server.Port.ToString(), InputTypes.ClassNumber);
        EditText token = Ui.Field(this, "Token", server.Token, InputTypes.ClassText | InputTypes.TextVariationVisiblePassword);
        box.AddView(name);
        box.AddView(host);
        box.AddView(port);
        box.AddView(token);
        Ui.Dialog(this, existing is null ? "Add a server" : "Edit server")
            .SetView(box)!
            .SetPositiveButton("Save & test", (_, _) =>
            {
                server.Name = string.IsNullOrWhiteSpace(name.Text) ? "Server" : name.Text!.Trim();
                server.Host = host.Text?.Trim() ?? "";
                server.Port = int.TryParse(port.Text, out int p) ? p : 6100;
                server.Token = token.Text?.Trim() ?? "";
                ServerStore.Upsert(server);
                RebuildList();
                TestServer(server);
            })!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ pickers

    private void PickServer(Action<string> chosen)
    {
        Ui.Toast(this, "Getting the server list …");
        Run(async () =>
        {
            BotClient bot = await BotHub.HelperAsync();
            JArray servers = (JArray)(await bot.SendAsync("servers", null, 30000))!;
            List<JToken> list = servers.Where(s => s.Value<string>("Name") is { Length: > 0 }).ToList();
            _ui.Post(() =>
            {
                if (list.Count == 0)
                {
                    Ui.Toast(this, "Couldn't get the server list yet.");
                    return;
                }
                string[] labels = list.Select(s =>
                    $"{s.Value<string>("Name")}  ({s.Value<int>("PlayerCount")}/{s.Value<int>("MaxPlayers")})" +
                    (s.Value<bool>("Upgrade") ? " · Member" : "") + (s.Value<bool>("Online") ? "" : " · offline")).ToArray();
                Ui.Dialog(this, $"Server ({list.Count})")
                    .SetItems(labels, (_, e) => chosen(list[e.Which].Value<string>("Name")!))!
                    .SetNegativeButton("Cancel", (_, _) => { })!
                    .Show();
            });
        }, "Server list");
    }

    private void PickScript(Account? forAccount, Action<string> chosen)
    {
        if (forAccount?.Remote == true)
        {
            // The server's scripts.
            Run(async () =>
            {
                JArray list = (JArray)(await (await BotHub.HelperAsync(forAccount)).SendAsync("scripts.list", null, 150000))!;
                List<string> scripts = list.Select(t => t.ToString()).ToList();
                _ui.Post(() => ShowScriptPicker(scripts, chosen));
            }, "Scripts");
            return;
        }
        string dir = SkuaHost.ScriptsDirectory;
        List<string> all = Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories)
                .Select(f => System.IO.Path.GetRelativePath(dir, f))
                .Where(f => !f.StartsWith("Core", StringComparison.OrdinalIgnoreCase) && !f.StartsWith("Cached-Scripts", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new();
        ShowScriptPicker(all, chosen);
    }

    private void ShowScriptPicker(List<string> all, Action<string> chosen)
    {
        if (all.Count == 0)
        {
            Ui.Toast(this, "No scripts yet. Use ⋯ → Update scripts first.");
            return;
        }

        LinearLayout box = new(this) { Orientation = Orientation.Vertical };
        box.SetPadding(Ui.Dp(this, 16), 0, Ui.Dp(this, 16), 0);
        EditText search = Ui.Field(this, "Search (e.g. ultra, farm, daily) …", "");
        ListView list = new(this);
        LightListAdapter adapter = new(this, all.Take(300).ToList());
        list.Adapter = adapter;
        box.AddView(search);
        box.AddView(list, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Ui.Dp(this, 420)));

        AlertDialog dialog = Ui.Dialog(this, $"Choose a script ({all.Count})")
            .SetView(box)!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .Create()!;
        search.TextChanged += (_, _) =>
        {
            adapter.Clear();
            adapter.AddAll(ScriptSearch.Find(all, search.Text ?? "").Take(300).ToList());
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
        ShowText($"Bot log · {account.Title}", string.Join("\n", BotHub.LogOf(account).TakeLast(300)));

    private void ShowAppLog() =>
        ShowText("App log", string.Join("\n", AppLog.Snapshot()));

    private void ShowText(string title, string text)
    {
        TextView view = Ui.Label(this, text.Length > 0 ? text : "(empty)", 11, Ui.Text);
        view.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        view.SetTextIsSelectable(true);
        view.SetPadding(Ui.Dp(this, 16), Ui.Dp(this, 8), Ui.Dp(this, 16), Ui.Dp(this, 8));
        ScrollView scroll = new(this);
        scroll.AddView(view);
        scroll.Post(() => scroll.FullScroll(FocusSearchDirection.Down));
        Ui.Dialog(this, title)
            .SetView(scroll)!
            .SetPositiveButton("Copy", (_, _) =>
            {
                Android.Content.ClipboardManager clipboard = (Android.Content.ClipboardManager)GetSystemService(ClipboardService)!;
                clipboard.PrimaryClip = ClipData.NewPlainText(title, text);
                Ui.Toast(this, "Copied. Paste it into the chat.");
            })!
            .SetNegativeButton("Close", (_, _) => { })!
            .Show();
    }

    // ------------------------------------------------------------------ helpers

    private bool CheckArtix()
    {
        if (AppFiles.IsArtixInstalled(this))
            return true;
        Ui.Toast(this, "The Artix Games Launcher isn't installed (or use headless mode).");
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

/// <summary>List rows with light text on the dark dialogs.</summary>
public sealed class LightListAdapter : ArrayAdapter<string>
{
    public LightListAdapter(Context context, IList<string> items)
        : base(context, Android.Resource.Layout.SimpleListItem1, items)
    {
    }

    public override View GetView(int position, View? convertView, ViewGroup parent)
    {
        View view = base.GetView(position, convertView, parent);
        if (view is TextView text)
        {
            text.SetTextColor(Ui.Text);
            text.SetTextSize(Android.Util.ComplexUnitType.Sp, 14);
        }
        return view;
    }
}

/// <summary>Script search that forgives typos: exact text first, then letters in order.</summary>
public static class ScriptSearch
{
    public static IEnumerable<string> Find(IEnumerable<string> scripts, string query)
    {
        string q = Normalize(query);
        if (q.Length == 0)
            return scripts;
        List<(string Script, int Rank)> hits = new();
        foreach (string script in scripts)
        {
            string name = Normalize(System.IO.Path.GetFileNameWithoutExtension(script));
            string full = Normalize(script);
            if (name.Contains(q))
                hits.Add((script, name.StartsWith(q) ? 0 : 1));
            else if (full.Contains(q))
                hits.Add((script, 2));
            else if (InOrder(name, q))
                hits.Add((script, 3));
            else if (InOrder(full, q))
                hits.Add((script, 4));
        }
        return hits.OrderBy(h => h.Rank).ThenBy(h => h.Script.Length).Select(h => h.Script);
    }

    private static string Normalize(string s) =>
        new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    /// <summary>Every letter of the query appears in the name, in order ("kathooldept" in "kathooladept").</summary>
    private static bool InOrder(string text, string query)
    {
        int i = 0;
        foreach (char c in text)
            if (i < query.Length && c == query[i])
                i++;
        return i == query.Length;
    }
}
