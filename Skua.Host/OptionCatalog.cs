using Skua.Core.Models;

namespace Skua.Host;

/// <summary>One editable setting: key, label, type ("bool", "int", "string" or "choice"), default.</summary>
public sealed record OptionDef(string Key, string Label, string Type, string Default, string Group = "", string[]? Choices = null, string Description = "");

/// <summary>
/// Settings the Skua Mobile manager shows. Skua options are <see cref="Skua.Core.Interfaces.IScriptOption"/>
/// properties (set by name on the bot); CoreBots options are the CBO file the desktop's
/// "CoreBots Options" window writes (options/CBO_Storage(username).txt).
/// </summary>
public static class OptionCatalog
{
    public static readonly IReadOnlyList<OptionDef> SkuaOptions = new OptionDef[]
    {
        // Performance (matters most on a phone)
        new("LagKiller", "Lag Killer", "bool", "false", "Performa"),
        new("HidePlayers", "Sembunyikan pemain lain", "bool", "false", "Performa"),
        new("OptimizePlayers", "Optimalkan pemain", "bool", "false", "Performa"),
        new("DisableFX", "Matikan efek (FX)", "bool", "false", "Performa"),
        new("SkipCutscenes", "Lewati cutscene", "bool", "true", "Performa"),
        new("SetFPS", "Batas FPS", "int", "30", "Performa"),
        new("ShowFPS", "Tampilkan FPS", "bool", "false", "Performa"),
        new("DisableDeathAds", "Matikan iklan saat mati", "bool", "true", "Performa"),
        new("DisableCollisions", "Matikan tabrakan", "bool", "false", "Performa"),
        // Rooms / relogin
        new("PrivateRooms", "Private room", "bool", "false", "Room & Relogin"),
        new("PrivateNumber", "Nomor private room", "int", "100000", "Room & Relogin"),
        new("AutoRelogin", "Relogin otomatis", "bool", "true", "Room & Relogin"),
        new("AutoReloginAny", "Relogin ke server mana saja", "bool", "false", "Room & Relogin"),
        new("SafeRelogin", "Relogin aman (lebih lambat)", "bool", "false", "Room & Relogin"),
        new("RetryRelogin", "Ulangi relogin kalau gagal", "bool", "true", "Room & Relogin"),
        new("ReloginServer", "Server relogin", "string", "", "Room & Relogin"),
        // Combat / drops
        new("AcceptACDrops", "Terima drop AC", "bool", "false", "Combat & Drop"),
        new("AggroMonsters", "Aggro monster", "bool", "false", "Combat & Drop"),
        new("Magnetise", "Tarik monster (magnet)", "bool", "false", "Combat & Drop"),
        new("InfiniteRange", "Jangkauan tak terbatas", "bool", "false", "Combat & Drop"),
        new("WalkSpeed", "Kecepatan jalan", "int", "8", "Combat & Drop"),
        // Privacy
        new("StreamerMode", "Mode streamer", "bool", "false", "Privasi"),
        new("CustomName", "Nama samaran", "string", "", "Privasi"),
        new("CustomGuild", "Guild samaran", "string", "", "Privasi"),
    };

    private static readonly string[] Modes = Enum.GetNames<Skua.Core.Models.Skills.ClassUseMode>();

    public static readonly IReadOnlyList<OptionDef> CoreBotsOptions = new OptionDef[]
    {
        new("PrivateRooms", "Private Rooms", "bool", "True", "Options"),
        new("PublicDifficult", "Public di bagian sulit", "bool", "False", "Options"),
        new("BankMiscAC", "Bank item Misc AC saat mulai", "bool", "True", "Options"),
        new("LoggerInChat", "Log di chat", "bool", "True", "Options"),
        new("MessageBoxCheck", "Matikan semua MessageBox", "bool", "False", "Options"),
        new("RestCheck", "Rest", "bool", "False", "Options"),
        new("DisableAutoEnhance", "Matikan AutoEnhance", "bool", "False", "Options"),
        new("DisableBestGear", "Matikan BestGear", "bool", "False", "Options"),
        new("AntiLag", "Anti Lag", "bool", "True", "Options"),
        new("IncognitoMode", "Incognito Mode", "bool", "True", "Options"),
        new("PrivateRoomNr", "Nomor room", "int", "100000", "Options"),
        new("ActionDelayNr", "Action Delay", "int", "700", "Options"),
        new("ExitCombatNr", "Exit Combat Delay", "int", "1600", "Options"),
        new("HuntDelayNr", "Hunt Delay", "int", "100", "Options"),
        new("QuestTriesNr", "Accept & Complete Tries", "int", "20", "Options"),
        new("QuestMaxNr", "Maks. quest termuat", "int", "150", "Options"),
        new("StopLocationSelect", "Map setelah bot berhenti", "string", "Home", "Options"),

        new("SoloClassSelect", "Class Solo", "string", "Generic", "Loadout"),
        new("SoloModeSelect", "Mode Solo", "choice", "Base", "Loadout", Modes),
        new("SoloEquipCheck", "Pakai gear Solo", "bool", "False", "Loadout"),
        new("FarmClassSelect", "Class Farm", "string", "Generic", "Loadout"),
        new("FarmModeSelect", "Mode Farm", "choice", "Base", "Loadout", Modes),
        new("FarmEquipCheck", "Pakai gear Farm", "bool", "False", "Loadout"),
        new("DodgeClassSelect", "Class Dodge", "string", "Generic", "Loadout"),
        new("DodgeModeSelect", "Mode Dodge", "choice", "Base", "Loadout", Modes),
        new("DodgeEquipCheck", "Pakai gear Dodge", "bool", "False", "Loadout"),
        new("BossClassSelect", "Class Boss", "string", "Generic", "Loadout"),
        new("BossModeSelect", "Mode Boss", "choice", "Base", "Loadout", Modes),
        new("BossEquipCheck", "Pakai gear Boss", "bool", "False", "Loadout"),

        new("doGoldBoost", "Pakai Gold Boost saat farm gold", "bool", "False", "Other"),
        new("doClassBoost", "Pakai Class Boost saat rank class", "bool", "False", "Other"),
        new("doRepBoost", "Pakai Rep Boost saat farm REP", "bool", "False", "Other"),
        new("doExpBoost", "Pakai EXP Boost saat farm EXP", "bool", "False", "Other"),
        new("Nation_SellMemVoucher", "Jual Voucher of Nulgath kalau tak perlu", "bool", "True", "Other"),
        new("Nation_ReturnPolicyDuringSupplies", "Swindles Return saat Supplies", "bool", "True", "Other"),
        new("UltraAlteonForSupplies", "Ultra Alteon untuk Supplies", "bool", "False", "Other"),
        new("PvP_SoloPvPBoss", "Bludrut: bunuh ads sebelum boss", "bool", "False", "Other"),
    };

    public static string CboPath(string username) =>
        Path.Combine(ClientFileSources.SkuaOptionsDIR, $"CBO_Storage({username}).txt");

    /// <summary>Values in the CBO file ("Key: Value" lines); missing keys are absent.</summary>
    public static Dictionary<string, string> ReadCbo(string username)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        string path = CboPath(username);
        if (!File.Exists(path))
            return values;
        foreach (string line in File.ReadAllLines(path))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0)
                continue;
            values[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }
        return values;
    }

    /// <summary>Write the CBO file, keeping keys this catalog doesn't know (e.g. equipment slots).</summary>
    public static void WriteCbo(string username, IReadOnlyDictionary<string, string> values)
    {
        Dictionary<string, string> merged = ReadCbo(username);
        foreach (OptionDef def in CoreBotsOptions)
            merged[def.Key] = values.TryGetValue(def.Key, out string? v) ? v : merged.GetValueOrDefault(def.Key, def.Default);
        foreach (var kv in values)
            merged[kv.Key] = kv.Value;
        Directory.CreateDirectory(ClientFileSources.SkuaOptionsDIR);
        File.WriteAllLines(CboPath(username), merged.Select(kv => $"{kv.Key}: {kv.Value}"));
    }
}
