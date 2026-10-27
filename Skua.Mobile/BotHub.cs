using Android.Content;
using Newtonsoft.Json.Linq;
using Skua.Host;
using System.Text.Json;

namespace Skua.Mobile;

/// <summary>Live view of one bot, refreshed by <see cref="BotHub"/>.</summary>
public sealed class BotState
{
    public int Slot { get; init; }
    public bool Alive { get; set; }
    public JObject Status { get; set; } = new();
    public List<string> Log { get; } = new();
    public long LogSeq { get; set; }

    public string Describe()
    {
        if (!Alive)
            return "Bot menyala …";
        JObject s = Status;
        if (s.Value<bool?>("connected") != true)
            return "Menunggu game (Artix) …";
        if (s.Value<bool?>("loaded") != true)
            return "Memuat AQW …";
        if (s.Value<bool?>("playing") != true)
            return "Di layar login";
        string where = $"{s.Value<string>("map")} · Lv {s.Value<int?>("level")}";
        return s.Value<bool?>("running") == true ? $"▶ {s.Value<string>("script")} · {where}" : $"Di game · {where}";
    }
}

/// <summary>
/// The manager's side of all bots: which account runs in which slot, starting/stopping bots,
/// and polling their status and logs (like VibeSkua.Manager watching each VibeSkua.exe).
/// </summary>
public static class BotHub
{
    public const int MaxSlots = 6;

    private static readonly BotClient[] _clients = Enumerable.Range(0, MaxSlots + 1).Select(i => new BotClient(i)).ToArray();
    private static readonly Dictionary<int, BotState> _states = new();
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
        SlotOf(account) is int slot ? State(slot) : null;

    private static BotState State(int slot)
    {
        lock (_states)
        {
            if (!_states.TryGetValue(slot, out BotState? state))
                _states[slot] = state = new BotState { Slot = slot };
            return state;
        }
    }

    public static int RunningCount
    {
        get
        {
            lock (_states)
                return _states.Values.Count(s => s.Slot > 0 && s.Alive);
        }
    }

    private static void SaveSlots()
    {
        lock (_slots)
            File.WriteAllText(_slotsFile, JsonSerializer.Serialize(_slots));
    }

    private static async Task PollLoop()
    {
        while (true)
        {
            List<(string Account, int Slot)> active;
            lock (_slots)
                active = _slots.Select(kv => (kv.Key, kv.Value)).ToList();
            foreach (var (accountId, slot) in active)
            {
                BotState state = State(slot);
                BotClient client = _clients[slot];
                try
                {
                    JObject status = (JObject)(await client.SendAsync("status", null, 4000))!;
                    state.Status = status;
                    state.Alive = true;
                    long seq = status.Value<long?>("logSeq") ?? 0;
                    if (seq > state.LogSeq)
                    {
                        JToken? logs = await client.SendAsync("logs", new JObject { ["since"] = state.LogSeq }, 4000);
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
                catch
                {
                    state.Alive = false;
                    // A bot that was asked to start may still be booting; one that died long ago is dropped.
                    if (DateTime.UtcNow - _startedAt.GetValueOrDefault(slot, DateTime.MinValue) > TimeSpan.FromMinutes(2))
                    {
                        lock (_slots)
                            _slots.Remove(accountId);
                        SaveSlots();
                    }
                }
            }
            try { Changed?.Invoke(); } catch { }
            await Task.Delay(2000);
        }
    }

    private static readonly Dictionary<int, DateTime> _startedAt = new();

    private static int? FreeSlot()
    {
        lock (_slots)
        {
            for (int slot = 1; slot <= MaxSlots; slot++)
                if (!_slots.ContainsValue(slot))
                    return slot;
        }
        return null;
    }

    /// <summary>Start (or reuse) this account's bot and hand it the account. Returns the slot.</summary>
    public static async Task<int> StartBotAsync(Account account)
    {
        int slot = SlotOf(account) ?? FreeSlot() ?? throw new InvalidOperationException($"Maksimal {MaxSlots} akun sekaligus.");
        lock (_slots)
            _slots[account.Id] = slot;
        SaveSlots();

        BotClient client = _clients[slot];
        if (!await client.PingAsync())
        {
            AppLog.Add($"Menyalakan bot {slot} untuk {account.Title} …");
            _startedAt[slot] = DateTime.UtcNow;
            lock (State(slot).Log)
                State(slot).Log.Clear();
            State(slot).LogSeq = 0;
            BotServiceBase.Start(_app, slot, account.Title);
            await WaitAliveAsync(client, TimeSpan.FromSeconds(120));
        }
        await ConfigureAsync(account);
        return slot;
    }

    /// <summary>Send the account (login, script, options) to its running bot.</summary>
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
        await _clients[slot].SendAsync("configure", new JObject { ["profile"] = JObject.FromObject(profile) });
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
        throw new TimeoutException($"Bot {client.Slot} tidak menyala (cek log bot).");
    }

    /// <summary>Start the account's bot and open its game.</summary>
    public static async Task PlayAsync(Context context, Account account, bool openGame = true)
    {
        int slot = await StartBotAsync(account);
        if (openGame)
            AppFiles.LaunchGame(context, slot);
    }

    public static Task<JToken?> SendAsync(Account account, string cmd, JObject? args = null, int timeoutMs = 15000)
    {
        if (SlotOf(account) is not int slot)
            throw new InvalidOperationException("Bot akun ini belum menyala.");
        return _clients[slot].SendAsync(cmd, args, timeoutMs);
    }

    public static async Task ShutdownAsync(Account account)
    {
        if (SlotOf(account) is not int slot)
            return;
        try { await _clients[slot].SendAsync("shutdown", null, 10000); } catch { }
        lock (_slots)
            _slots.Remove(account.Id);
        SaveSlots();
        State(slot).Alive = false;
    }

    /// <summary>
    /// A bot for work that needs Skua but no game (script updates, script options, server list):
    /// any running bot that isn't busy with a script, else the helper bot (slot 0).
    /// </summary>
    public static async Task<BotClient> HelperAsync()
    {
        List<int> running;
        lock (_slots)
            running = _slots.Values.ToList();
        foreach (int slot in running)
        {
            BotState state = State(slot);
            if (state.Alive && state.Status.Value<bool?>("running") != true && await _clients[slot].PingAsync())
                return _clients[slot];
        }
        BotClient helper = _clients[0];
        if (!await helper.PingAsync())
        {
            AppLog.Add("Menyalakan bot helper …");
            BotServiceBase.Start(_app, 0, "Helper");
            await WaitAliveAsync(helper, TimeSpan.FromSeconds(120));
        }
        return helper;
    }

    public static List<string> LogOf(Account account)
    {
        BotState? state = StateOf(account);
        if (state is null)
            return new();
        lock (state.Log)
            return state.Log.ToList();
    }
}
