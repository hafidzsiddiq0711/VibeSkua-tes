using Android.Content;
using System.Text.Json;

namespace Skua.Mobile;

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>Name shown in the manager (defaults to the username).</summary>
    public string Name { get; set; } = "";
    public string User { get; set; } = "";
    public string Pass { get; set; } = "";
    public string Server { get; set; } = "Twilly";
    public string Group { get; set; } = "Default";
    /// <summary>Script path relative to the scripts folder.</summary>
    public string Script { get; set; } = "";
    public bool AutoStart { get; set; }
    /// <summary>Game render resolution in percent (Artix (Skua) only): lower = lighter.</summary>
    public int RenderScale { get; set; } = 100;
    /// <summary>"watch": game in an Artix window; "headless": no window, lightest.</summary>
    public string Mode { get; set; } = "watch";

    public bool Headless => Mode == "headless";
    /// <summary>Skua options changed for this account (applied after login).</summary>
    public Dictionary<string, string> SkuaOptions { get; set; } = new();

    public string Title => Name.Length > 0 ? Name : User.Length > 0 ? User : "(no name)";
}

/// <summary>accounts.json in the app's private storage.</summary>
public static class AccountStore
{
    private static readonly object _lock = new();
    private static List<Account>? _accounts;
    private static string _file = "";

    public static event Action? Changed;

    public static void Init(Context context)
    {
        lock (_lock)
        {
            if (_accounts is not null)
                return;
            _file = Path.Combine(context.FilesDir!.AbsolutePath, "accounts.json");
            try
            {
                _accounts = File.Exists(_file) ? JsonSerializer.Deserialize<List<Account>>(File.ReadAllText(_file)) : null;
            }
            catch (Exception e)
            {
                AppLog.Add($"accounts.json rusak: {e.Message}");
            }
            if (_accounts is null)
            {
                _accounts = new();
                // v0.1 kept one account in preferences.
                ISharedPreferences prefs = context.GetSharedPreferences("skua", FileCreationMode.Private)!;
                string user = prefs.GetString("user", "")!;
                if (user.Length > 0)
                {
                    _accounts.Add(new Account
                    {
                        User = user,
                        Pass = prefs.GetString("pass", "")!,
                        Server = prefs.GetString("server", "Twilly")!,
                        Script = prefs.GetString("script", "")!,
                        AutoStart = prefs.GetBoolean("autoStart", false),
                    });
                }
                SaveLocked();
            }
        }
    }

    public static List<Account> All
    {
        get
        {
            lock (_lock)
                return _accounts!.ToList();
        }
    }

    public static Account? Find(string id)
    {
        lock (_lock)
            return _accounts!.Find(a => a.Id == id);
    }

    public static IReadOnlyList<string> Groups()
    {
        lock (_lock)
            return _accounts!.Select(a => a.Group).Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g).ToList();
    }

    public static void Upsert(Account account)
    {
        lock (_lock)
        {
            int i = _accounts!.FindIndex(a => a.Id == account.Id);
            if (i >= 0)
                _accounts[i] = account;
            else
                _accounts.Add(account);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public static void Remove(string id)
    {
        lock (_lock)
        {
            _accounts!.RemoveAll(a => a.Id == id);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public static void Move(string id, int delta)
    {
        lock (_lock)
        {
            int i = _accounts!.FindIndex(a => a.Id == id);
            int j = i + delta;
            if (i < 0 || j < 0 || j >= _accounts.Count)
                return;
            (_accounts[i], _accounts[j]) = (_accounts[j], _accounts[i]);
            SaveLocked();
        }
        Changed?.Invoke();
    }

    public static void Save()
    {
        lock (_lock)
            SaveLocked();
        Changed?.Invoke();
    }

    private static void SaveLocked()
    {
        string tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_accounts, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, _file, true);
    }
}
