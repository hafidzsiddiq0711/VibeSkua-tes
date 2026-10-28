using Android.Content;
using Newtonsoft.Json.Linq;
using Skua.Host;
using System.Collections.Concurrent;
using System.Text.Json;

namespace Skua.Mobile;

/// <summary>Live view of one account's bot, refreshed by <see cref="BotHub"/>.</summary>
public sealed class BotState
{
    public bool Alive { get; set; }
    public JObject Status { get; set; } = new();
    public List<string> Log { get; } = new();
    public long LogSeq { get; set; }
    public string Instance { get; set; } = "";
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public string? Error { get; set; }

    // ------------------------------------------------------------------ farming rate

    private readonly List<(DateTime At, long Xp, long Gold)> _samples = new();
    private long _totalXp;
    private int _lastLevel = -1, _lastXp, _lastNeeded;

    /// <summary>Feed a status: keeps XP gained (across level-ups) and gold over the last 15 minutes.</summary>
    public void Sample(JObject s)
    {
        if (s.Value<bool?>("playing") != true || s.Value<int?>("level") is not int level || s.Value<int?>("xp") is not int xp)
            return;
        if (_lastLevel >= 0)
        {
            if (level == _lastLevel && xp >= _lastXp)
                _totalXp += xp - _lastXp;
            else if (level == _lastLevel + 1)
                _totalXp += Math.Max(0, _lastNeeded - _lastXp) + xp;
        }
        _lastLevel = level;
        _lastXp = xp;
        _lastNeeded = s.Value<int?>("xpNeeded") ?? 0;
        DateTime now = DateTime.UtcNow;
        _samples.Add((now, _totalXp, s.Value<long?>("gold") ?? 0));
        _samples.RemoveAll(x => now - x.At > TimeSpan.FromMinutes(15));
    }

    /// <summary>"+480k XP/h · +3.5k gold/h" once there are 2 minutes of samples; "" before.</summary>
    public string Rate()
    {
        if (_samples.Count < 2)
            return "";
        var (t0, x0, g0) = _samples[0];
        var (t1, x1, g1) = _samples[^1];
        double hours = (t1 - t0).TotalHours;
        if (hours < 2 / 60.0)
            return "";
        return $"+{Short((x1 - x0) / hours)} XP/h · {(g1 >= g0 ? "+" : "")}{Short((g1 - g0) / hours)} gold/h";
    }

    private static string Short(double v) =>
        Math.Abs(v) >= 1e6 ? $"{v / 1e6:0.#}M" : Math.Abs(v) >= 1e3 ? $"{v / 1e3:0.#}k" : $"{v:0}";

    public string Describe()
    {
        if (!Alive)
            return Error is { Length: > 0 } e ? e : "Starting bot …";
        JObject s = Status;
        if (s.Value<bool?>("connected") != true)
            return s.Value<bool?>("headless") == true ? "Starting headless game …" : "Waiting for the game window …";
        if (s.Value<bool?>("loaded") != true)
            return "Loading AQW …";
        if (s.Value<bool?>("playing") != true)
            return "On the login screen";
        string where = $"{s.Value<string>("map")} · Lv {s.Value<int?>("level")}";
        return s.Value<bool?>("running") == true ? $"Running {s.Value<string>("script")} · {where}" : $"In game · {where}";
    }
}

/// <summary>
/// The manager's side of all bots: on this phone (one process per account) and on Skua Servers
/// (PC/VPS). Which account runs in which slot, starting/stopping bots, polling status and logs
/// (like VibeSkua.Manager watching each VibeSkua.exe).
/// </summary>
public static class BotHub
{
    /// <summary>Accounts at once on this phone.</summary>
    public const int MaxSlots = 6;
    private const int RemoteSlots = 32;

    private static readonly BotClient[] _local = Enumerable.Range(0, MaxSlots + 1).Select(i => new BotClient(i)).ToArray();
    private static readonly ConcurrentDictionary<string, BotClient> _remote = new();
    private static readonly ConcurrentDictionary<string, BotState> _states = new();
    private static readonly ConcurrentDictionary<string, DateTime> _startedAt = new();
    private static Dictionary<string, int> _slots = new();
    private static string _slotsFile = "";
    private static Context _app = null!;
    private static bool _started;

    /// <summary>Raised after each status refresh (on a worker thread).</summary>
    public static event Action? Changed;

    public static void Init(Context context)
    {
        if (_started)
            return;
        _started = true;
        _app = context.ApplicationContext!;
        ServerStore.Init(_app);
        _slotsFile = Path.Combine(_app.FilesDir!.AbsolutePath, "slots.json");
        try
        {
            if (File.Exists(_slotsFile))
                _slots = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_slotsFile)) ?? new();
        }
        catch { }
        Task.Run(PollLoop);
    }

    public static int? SlotOf(Account account)
    {
        lock (_slots)
            return _slots.TryGetValue(account.Id, out int slot) ? slot : null;
    }

    public static BotState? StateOf(Account account) =>
        SlotOf(account) is null ? null : State(account.Id);

    private static BotState State(string accountId) => _states.GetOrAdd(accountId, _ => new BotState());

    /// <summary>Running bots on this phone / on servers.</summary>
    public static (int Phone, int Servers) RunningCount
    {
        get
        {
            int phone = 0, servers = 0;
            foreach (Account a in AccountStore.All)
            {
                if (SlotOf(a) is null || !State(a.Id).Alive)
                    continue;
                if (a.Remote) servers++;
                else phone++;
            }
            return (phone, servers);
        }
    }

    private static void SaveSlots()
    {
        lock (_slots)
            File.WriteAllText(_slotsFile, JsonSerializer.Serialize(_slots));
    }

    // ------------------------------------------------------------------ clients

    private static BotClient ClientFor(Account account, int slot)
    {
        if (!account.Remote)
            return _local[slot];
        return ServerClient(account.ServerId, slot);
    }

    private static BotClient ServerClient(string serverId, int slot)
    {
        RemoteServer server = ServerStore.Find(serverId) ?? throw new InvalidOperationException("This account's server was removed.");
        string key = $"{server.Id}:{server.Host}:{server.Port}:{server.Token}:{slot}";
        return _remote.GetOrAdd(key, _ => new BotClient(server.Host, server.Port, server.Token, slot));
    }

    /// <summary>The server's own info (name, CPUs, running bots), to test a connection.</summary>
    public static async Task<JObject> ServerInfoAsync(RemoteServer server)
    {
        using BotClient client = new(server.Host, server.Port, server.Token, 0);
        return (JObject)(await client.SendAsync("server.info", null, 10000))!;
    }

    // ------------------------------------------------------------------ polling

    private static async Task PollLoop()
    {
        while (true)
        {
            List<Account> active = AccountStore.All.Where(a => SlotOf(a) is not null).ToList();
            // Servers are polled in parallel so one slow VPS doesn't hold up the phone's bots.
            await Task.WhenAll(active.Select(PollAsync));
            try { Changed?.Invoke(); } catch { }
            await Task.Delay(2000);
        }
    }

    private static async Task PollAsync(Account account)
    {
        if (SlotOf(account) is not int slot)
            return;
        BotState state = State(account.Id);
        try
        {
            BotClient client = ClientFor(account, slot);
            JObject status = (JObject)(await client.SendAsync("status", null, account.Remote ? 15000 : 4000))!;
            state.Status = status;
            state.Sample(status);
            state.Alive = true;
            state.Error = null;
            state.LastSeenUtc = DateTime.UtcNow;
            long seq = status.Value<long?>("logSeq") ?? 0;
            // A new bot process (restarted, or the account moved) counts its log from zero again.
            string instance = status.Value<string>("instance") ?? "";
            if (instance != state.Instance)
            {
                if (state.Instance.Length > 0)
                    lock (state.Log)
                        state.Log.Add("───── bot restarted ─────");
                state.Instance = instance;
                state.LogSeq = 0;
            }
            if (seq > state.LogSeq)
            {
                JToken? logs = await client.SendAsync("logs", new JObject { ["since"] = state.LogSeq }, 8000);
                lock (state.Log)
                {
                    foreach (JToken line in logs?["lines"] ?? new JArray())
                        state.Log.Add(line.ToString());
                    if (state.Log.Count > 600)
                        state.Log.RemoveRange(0, state.Log.Count - 600);
                }
                state.LogSeq = logs?.Value<long?>("seq") ?? seq;
            }
        }
        catch (Exception e)
        {
            state.Alive = false;
            if (account.Remote)
                state.Error = e.Message.Contains("isn't running") ? "Bot stopped on the server" : $"Server unreachable ({e.GetBaseException().Message})";
            // Busy, booting or briefly unreachable bots miss a poll now and then; a phone bot silent
            // for two minutes is let go. Server bots are kept (the server may just be offline).
            DateTime started = _startedAt.GetValueOrDefault(account.Id, DateTime.MinValue);
            DateTime last = state.LastSeenUtc > started ? state.LastSeenUtc : started;
            if (!account.Remote && DateTime.UtcNow - last > TimeSpan.FromMinutes(2))
            {
                lock (_slots)
                    _slots.Remove(account.Id);
                SaveSlots();
            }
            if (account.Remote && state.Error == "Bot stopped on the server")
            {
                lock (_slots)
                    _slots.Remove(account.Id);
                SaveSlots();
            }
        }
    }

    // ------------------------------------------------------------------ slots

    private static int? FreeSlot(Account account)
    {
        int max = account.Remote ? RemoteSlots : MaxSlots;
        lock (_slots)
        {
            List<Account> others = AccountStore.All.Where(a => a.ServerId == account.ServerId && a.Id != account.Id).ToList();
            HashSet<int> used = others.Where(a => _slots.ContainsKey(a.Id)).Select(a => _slots[a.Id]).ToHashSet();
            if (account.Remote)
            {
                // On a server each account keeps its own bot number, so a bot the phone lost track
                // of (busy, server offline for a while) is found again instead of a second one
                // logging in as the same account.
                used.UnionWith(others.Where(a => a.ServerSlot > 0).Select(a => a.ServerSlot));
                if (account.ServerSlot > 0 && account.ServerSlot <= max && !used.Contains(account.ServerSlot))
                    return account.ServerSlot;
            }
            for (int slot = 1; slot <= max; slot++)
            {
                if (!used.Contains(slot))
                {
                    if (account.Remote)
                    {
                        account.ServerSlot = slot;
                        Task.Run(AccountStore.Save);
                    }
                    return slot;
                }
            }
        }
        return null;
    }

    /// <summary>Start (or reuse) this account's bot and hand it the account. Returns the slot.</summary>
    public static async Task<int> StartBotAsync(Account account)
    {
        int slot = SlotOf(account) ?? FreeSlot(account)
            ?? throw new InvalidOperationException(account.Remote ? "No free bot slot on that server." : $"At most {MaxSlots} accounts at once on the phone.");
        lock (_slots)
            _slots[account.Id] = slot;
        SaveSlots();

        BotClient client = ClientFor(account, slot);
        BotState state = State(account.Id);
        if (!await client.PingAsync(account.Remote ? 5000 : 1500))
        {
            AppLog.Add($"Starting bot {slot} for {account.Title}{(account.Remote ? " on " + ServerStore.Find(account.ServerId)?.Name : "")} …");
            _startedAt[account.Id] = DateTime.UtcNow;
            lock (state.Log)
                state.Log.Clear();
            state.LogSeq = 0;
            // A server starts its bot when it gets the first command (configure, below).
            if (!account.Remote)
            {
                BotServiceBase.Start(_app, slot, account.Title, account.RenderScale);
                await WaitAliveAsync(client, TimeSpan.FromSeconds(120));
            }
        }
        await ConfigureAsync(account);
        return slot;
    }

    /// <summary>Send the account (login, script, options) to its bot.</summary>
    public static async Task ConfigureAsync(Account account)
    {
        if (SlotOf(account) is not int slot)
            return;
        BotProfile profile = new()
        {
            Username = account.User,
            Password = account.Pass,
            Server = account.Server,
            Script = account.Script,
            AutoLogin = account.User.Length > 0,
            AutoStart = account.AutoStart,
            SkuaOptions = new Dictionary<string, string>(account.SkuaOptions),
        };
        await ClientFor(account, slot).SendAsync("configure", new JObject { ["profile"] = JObject.FromObject(profile) },
            account.Remote ? 180000 : 15000);
    }

    private static async Task WaitAliveAsync(BotClient client, TimeSpan timeout)
    {
        DateTime until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (await client.PingAsync())
                return;
            await Task.Delay(1000);
        }
        throw new TimeoutException($"Bot {client.Slot} didn't start (see its log).");
    }

    // ------------------------------------------------------------------ actions

    /// <summary>Start the account's bot and its game (an Artix window, or headless).</summary>
    public static async Task PlayAsync(Context context, Account account, bool openGame = true)
    {
        int slot = await StartBotAsync(account);
        if (account.Headless)
            await ClientFor(account, slot).SendAsync("headless.start", null, 30000);
        else if (openGame)
            AppFiles.LaunchGame(context, slot, account.RenderScale);
    }

    /// <summary>
    /// Switch a phone account between its Artix window and headless. The new game takes over
    /// the bot; the old one is told to stand down (it shows "you can close this window").
    /// </summary>
    public static async Task SetModeAsync(Context context, Account account, bool headless)
    {
        if (account.Remote)
            throw new InvalidOperationException("Accounts on a server always play headless.");
        account.Mode = headless ? "headless" : "watch";
        AccountStore.Save();
        if (SlotOf(account) is not int slot || !await _local[slot].PingAsync())
            return;
        if (headless)
        {
            await _local[slot].SendAsync("headless.start");
        }
        else
        {
            await _local[slot].SendAsync("headless.stop");
            AppFiles.LaunchGame(context, slot, account.RenderScale);
        }
    }

    /// <summary>Stop the bot where it runs and move the account to the phone or a server.</summary>
    public static async Task MoveAsync(Account account, string serverId)
    {
        await ShutdownAsync(account);
        account.ServerId = serverId;
        account.ServerSlot = 0;
        if (serverId.Length > 0)
            account.Mode = "headless";
        AccountStore.Save();
    }

    public static Task<JToken?> SendAsync(Account account, string cmd, JObject? args = null, int timeoutMs = 15000)
    {
        if (SlotOf(account) is not int slot)
            throw new InvalidOperationException("This account's bot isn't running.");
        return ClientFor(account, slot).SendAsync(cmd, args, account.Remote ? Math.Max(timeoutMs, 30000) : timeoutMs);
    }

    /// <summary>The server's file log of this account's bot (game output included).</summary>
    public static async Task<string> ServerLogAsync(Account account)
    {
        int slot = SlotOf(account) ?? -1;
        using BotClient client = new(ServerStore.Find(account.ServerId)?.Host ?? throw new InvalidOperationException("This account's server was removed."),
            ServerStore.Find(account.ServerId)!.Port, ServerStore.Find(account.ServerId)!.Token, slot);
        return (await client.SendAsync("server.botLog", null, 20000))?.ToString() ?? "";
    }

    public static async Task ShutdownAsync(Account account)
    {
        if (SlotOf(account) is not int slot)
            return;
        try { await ClientFor(account, slot).SendAsync("shutdown", null, 10000); } catch { }
        lock (_slots)
            _slots.Remove(account.Id);
        SaveSlots();
        State(account.Id).Alive = false;
    }

    /// <summary>
    /// A bot for work that needs Skua but no game (script updates, script options, server list):
    /// for a server account, the server's helper bot; on the phone, any running bot that isn't
    /// busy with a script, else the helper bot (slot 0).
    /// </summary>
    public static async Task<BotClient> HelperAsync(Account? forAccount = null)
    {
        if (forAccount?.Remote == true)
            return ServerClient(forAccount.ServerId, 0);
        foreach (Account a in AccountStore.All.Where(a => !a.Remote && SlotOf(a) is not null))
        {
            BotState state = State(a.Id);
            int slot = SlotOf(a)!.Value;
            if (state.Alive && state.Status.Value<bool?>("running") != true && await _local[slot].PingAsync())
                return _local[slot];
        }
        BotClient helper = _local[0];
        if (!await helper.PingAsync())
        {
            AppLog.Add("Starting helper bot …");
            BotServiceBase.Start(_app, 0, "Helper");
            await WaitAliveAsync(helper, TimeSpan.FromSeconds(120));
        }
        return helper;
    }

    /// <summary>The helper bot of a server (scripts, options, CoreBots settings live there).</summary>
    public static BotClient ServerHelper(RemoteServer server) => ServerClient(server.Id, 0);

    public static List<string> LogOf(Account account)
    {
        BotState? state = StateOf(account);
        if (state is null)
            return new();
        lock (state.Log)
            return state.Log.ToList();
    }
}
