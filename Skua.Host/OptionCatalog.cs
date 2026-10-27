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
        new("LagKiller", "Lag Killer", "bool", "false", "Performance"),
        new("HidePlayers", "Hide other players", "bool", "false", "Performance"),
        new("OptimizePlayers", "Optimize players", "bool", "false", "Performance"),
        new("DisableFX", "Disable effects (FX)", "bool", "false", "Performance"),
        new("SkipCutscenes", "Skip cutscenes", "bool", "true", "Performance"),
        new("SetFPS", "FPS limit", "int", "30", "Performance"),
        new("ShowFPS", "Show FPS", "bool", "false", "Performance"),
        new("DisableDeathAds", "Disable death ads", "bool", "true", "Performance"),
        new("DisableCollisions", "Disable collisions", "bool", "false", "Performance"),
        // Rooms / relogin
        new("PrivateRooms", "Private rooms", "bool", "false", "Rooms & relogin"),
        new("PrivateNumber", "Private room number", "int", "100000", "Rooms & relogin"),
        new("AutoRelogin", "Auto relogin", "bool", "true", "Rooms & relogin"),
        new("AutoReloginAny", "Relogin to any server", "bool", "false", "Rooms & relogin"),
        new("SafeRelogin", "Safe relogin (slower)", "bool", "false", "Rooms & relogin"),
        new("RetryRelogin", "Retry relogin", "bool", "true", "Rooms & relogin"),
        new("ReloginServer", "Relogin server", "string", "", "Rooms & relogin"),
        // Combat / drops
        new("AcceptACDrops", "Accept AC drops", "bool", "false", "Combat & drops"),
        new("AggroMonsters", "Aggro monster", "bool", "false", "Combat & drops"),
        new("Magnetise", "Magnetise monsters", "bool", "false", "Combat & drops"),
        new("InfiniteRange", "Infinite range", "bool", "false", "Combat & drops"),
        new("WalkSpeed", "Walk speed", "int", "8", "Combat & drops"),
        // Privacy
        new("StreamerMode", "Streamer mode", "bool", "false", "Privacy"),
        new("CustomName", "Custom name", "string", "", "Privacy"),
        new("CustomGuild", "Custom guild", "string", "", "Privacy"),
    };

    private static readonly string[] Modes = Enum.GetNames<Skua.Core.Models.Skills.ClassUseMode>();

    public static readonly IReadOnlyList<OptionDef> CoreBotsOptions = new OptionDef[]
    {
        new("PrivateRooms", "Private Rooms", "bool", "True", "Options"),
        new("PublicDifficult", "Public on difficult parts", "bool", "False", "Options"),
        new("BankMiscAC", "Bank misc. AC items on start", "bool", "True", "Options"),
        new("LoggerInChat", "Logger in chat", "bool", "True", "Options"),
        new("MessageBoxCheck", "Force off message boxes", "bool", "False", "Options"),
        new("RestCheck", "Should rest", "bool", "False", "Options"),
        new("DisableAutoEnhance", "Disable AutoEnhance", "bool", "False", "Options"),
        new("DisableBestGear", "Disable BestGear", "bool", "False", "Options"),
        new("AntiLag", "Anti Lag", "bool", "True", "Options"),
        new("IncognitoMode", "Incognito Mode", "bool", "True", "Options"),
        new("PrivateRoomNr", "Room number", "int", "100000", "Options"),
        new("ActionDelayNr", "Action Delay", "int", "700", "Options"),
        new("ExitCombatNr", "Exit Combat Delay", "int", "1600", "Options"),
        new("HuntDelayNr", "Hunt Delay", "int", "100", "Options"),
        new("QuestTriesNr", "Accept and complete tries", "int", "20", "Options"),
        new("QuestMaxNr", "Loaded quests max.", "int", "150", "Options"),
        new("StopLocationSelect", "Map after the bot stops", "string", "Home", "Options"),

        new("SoloClassSelect", "Solo class", "string", "Generic", "Loadout"),
        new("SoloModeSelect", "Solo mode", "choice", "Base", "Loadout", Modes),
        new("SoloEquipCheck", "Equip solo gear", "bool", "False", "Loadout"),
        new("FarmClassSelect", "Farm class", "string", "Generic", "Loadout"),
        new("FarmModeSelect", "Farm mode", "choice", "Base", "Loadout", Modes),
        new("FarmEquipCheck", "Equip farm gear", "bool", "False", "Loadout"),
        new("DodgeClassSelect", "Dodge class", "string", "Generic", "Loadout"),
        new("DodgeModeSelect", "Dodge mode", "choice", "Base", "Loadout", Modes),
        new("DodgeEquipCheck", "Equip dodge gear", "bool", "False", "Loadout"),
        new("BossClassSelect", "Boss class", "string", "Generic", "Loadout"),
        new("BossModeSelect", "Boss mode", "choice", "Base", "Loadout", Modes),
        new("BossEquipCheck", "Equip boss gear", "bool", "False", "Loadout"),

        new("doGoldBoost", "Use gold boosts when farming gold", "bool", "False", "Other"),
        new("doClassBoost", "Use class boosts when ranking a class", "bool", "False", "Other"),
        new("doRepBoost", "Use rep boosts when farming REP", "bool", "False", "Other"),
        new("doExpBoost", "Use EXP boosts when farming EXP", "bool", "False", "Other"),
        new("Nation_SellMemVoucher", "Sell Voucher of Nulgath if not needed", "bool", "True", "Other"),
        new("Nation_ReturnPolicyDuringSupplies", "Swindles Return during Supplies", "bool", "True", "Other"),
        new("UltraAlteonForSupplies", "Ultra Alteon for Supplies", "bool", "False", "Other"),
        new("PvP_SoloPvPBoss", "Bludrut Brawl: kill adds before the boss", "bool", "False", "Other"),
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
