using System.Drawing;
using PKHeX.Core;

namespace ShinyForge.Core;

public enum Platform { GB, GBC, GBA, NDS, N3DS, Switch }

public enum BotKind
{
    None,
    FrlgSwitch,     // our FRLG bot (Python or the SysBot FRLG_EncounterBot routine)
    SysBotLGPE,
    SysBotSWSH,
    SysBotBDSP,
    ThreeDS,        // ShinyForge's 3DS bot (Luma3DS InputRedirection + debugger)
}

public sealed record Game(
    string Id, string Title, Platform Platform, int Gen, ushort Mascot,
    Color Color1, Color Color2, string Spreadsheet, GameVersion[] Versions,
    BotKind Bot = BotKind.None, string[]? SwitchTitleIds = null)
{
    public string PlatformLabel => Platform switch
    {
        Platform.GB => "Game Boy",
        Platform.GBC => "Game Boy Color",
        Platform.GBA => "Game Boy Advance",
        Platform.NDS => "Nintendo DS",
        Platform.N3DS => "Nintendo 3DS",
        _ => "Nintendo Switch",
    };

    public bool IsSwitch => Platform == Platform.Switch;
}

/// <summary>All main-series Pokémon games ShinyForge knows about.</summary>
public static class GameCatalog
{
    private static Color C(string hex) => ColorTranslator.FromHtml(hex);

    public static readonly Game[] All =
    {
        // ---- Game Boy
        new("gb_red", "Red", Platform.GB, 1, 6, C("#C0392B"), C("#5B0F0F"), "Red", new[] { GameVersion.RD }),
        new("gb_green", "Green (JPN)", Platform.GB, 1, 3, C("#2E8B57"), C("#0F3D24"), "", new[] { GameVersion.GN }),
        new("gb_blue", "Blue", Platform.GB, 1, 9, C("#2F6FD6"), C("#0B2559"), "Blue", new[] { GameVersion.BU }),
        new("gb_yellow", "Yellow", Platform.GB, 1, 25, C("#F2C531"), C("#7A5A00"), "Yellow", new[] { GameVersion.YW }),
        // ---- Game Boy Color
        new("gbc_gold", "Gold", Platform.GBC, 2, 250, C("#D4A017"), C("#5E4300"), "Gold", new[] { GameVersion.GD }),
        new("gbc_silver", "Silver", Platform.GBC, 2, 249, C("#AEB6C2"), C("#3B4250"), "Silver", new[] { GameVersion.SI }),
        new("gbc_crystal", "Crystal", Platform.GBC, 2, 245, C("#4FC3F7"), C("#0D3B66"), "Crystal", new[] { GameVersion.C }),
        // ---- Game Boy Advance
        new("gba_ruby", "Ruby", Platform.GBA, 3, 383, C("#D7263D"), C("#4A0A12"), "Ruby", new[] { GameVersion.R }),
        new("gba_sapphire", "Sapphire", Platform.GBA, 3, 382, C("#2364AA"), C("#0A1F3D"), "Sapphire", new[] { GameVersion.S }),
        new("gba_emerald", "Emerald", Platform.GBA, 3, 384, C("#1E9E5A"), C("#063D22"), "Emerald", new[] { GameVersion.E }),
        new("gba_firered", "FireRed", Platform.GBA, 3, 6, C("#F05A28"), C("#5C1A05"), "Fire Red", new[] { GameVersion.FR }),
        new("gba_leafgreen", "LeafGreen", Platform.GBA, 3, 3, C("#5DBB46"), C("#1C4A12"), "Leaf Green", new[] { GameVersion.LG }),
        // ---- Nintendo DS
        new("nds_diamond", "Diamond", Platform.NDS, 4, 483, C("#5DA9E9"), C("#13315C"), "Diamond", new[] { GameVersion.D }),
        new("nds_pearl", "Pearl", Platform.NDS, 4, 484, C("#E89BC4"), C("#5C2443"), "Pearl", new[] { GameVersion.P }),
        new("nds_platinum", "Platinum", Platform.NDS, 4, 487, C("#9A9FAB"), C("#2B2D33"), "Platinum", new[] { GameVersion.Pt }),
        new("nds_heartgold", "HeartGold", Platform.NDS, 4, 250, C("#E2B13C"), C("#6B3F00"), "Heart Gold", new[] { GameVersion.HG }),
        new("nds_soulsilver", "SoulSilver", Platform.NDS, 4, 249, C("#B9C3D3"), C("#2E3A55"), "Soul Silver", new[] { GameVersion.SS }),
        new("nds_black", "Black", Platform.NDS, 5, 643, C("#3A3A3A"), C("#0D0D0D"), "Black", new[] { GameVersion.B }),
        new("nds_white", "White", Platform.NDS, 5, 644, C("#E8E8E8"), C("#7C8594"), "White", new[] { GameVersion.W }),
        new("nds_black2", "Black 2", Platform.NDS, 5, 646, C("#2C3E50"), C("#0A0F14"), "Black 2", new[] { GameVersion.B2 }),
        new("nds_white2", "White 2", Platform.NDS, 5, 646, C("#DDE6F0"), C("#5D7A99"), "White 2", new[] { GameVersion.W2 }),
        // ---- Nintendo 3DS
        new("3ds_x", "X", Platform.N3DS, 6, 716, C("#1E6BD6"), C("#0A1E45"), "X", new[] { GameVersion.X }, BotKind.ThreeDS),
        new("3ds_y", "Y", Platform.N3DS, 6, 717, C("#D62839"), C("#450A12"), "Y", new[] { GameVersion.Y }, BotKind.ThreeDS),
        new("3ds_or", "Omega Ruby", Platform.N3DS, 6, 383, C("#C1121F"), C("#3D0309"), "Omega Ruby", new[] { GameVersion.OR }, BotKind.ThreeDS),
        new("3ds_as", "Alpha Sapphire", Platform.N3DS, 6, 382, C("#1D4ED8"), C("#081A47"), "Alpha Sapphire", new[] { GameVersion.AS }, BotKind.ThreeDS),
        new("3ds_sun", "Sun", Platform.N3DS, 7, 791, C("#F59E0B"), C("#6B2E00"), "Sun", new[] { GameVersion.SN }, BotKind.ThreeDS),
        new("3ds_moon", "Moon", Platform.N3DS, 7, 792, C("#6D28D9"), C("#1E0A45"), "Moon", new[] { GameVersion.MN }, BotKind.ThreeDS),
        new("3ds_usun", "Ultra Sun", Platform.N3DS, 7, 800, C("#F97316"), C("#5C1A00"), "Ultra Sun", new[] { GameVersion.US }, BotKind.ThreeDS),
        new("3ds_umoon", "Ultra Moon", Platform.N3DS, 7, 800, C("#7C3AED"), C("#1F0B4D"), "Ultra Moon", new[] { GameVersion.UM }, BotKind.ThreeDS),
        // ---- Nintendo Switch
        new("sw_lgp", "Let's Go, Pikachu!", Platform.Switch, 7, 25, C("#FACC15"), C("#7A5A00"), "Let's Go Pikachu", new[] { GameVersion.GP }, BotKind.SysBotLGPE),
        new("sw_lge", "Let's Go, Eevee!", Platform.Switch, 7, 133, C("#C08457"), C("#4A2A12"), "Let's Go Eevee", new[] { GameVersion.GE }, BotKind.SysBotLGPE),
        new("sw_sword", "Sword", Platform.Switch, 8, 888, C("#0EA5E9"), C("#0B2545"), "Sword", new[] { GameVersion.SW }, BotKind.SysBotSWSH),
        new("sw_shield", "Shield", Platform.Switch, 8, 889, C("#E11D48"), C("#4C0519"), "Shield", new[] { GameVersion.SH }, BotKind.SysBotSWSH),
        new("sw_bd", "Brilliant Diamond", Platform.Switch, 8, 483, C("#3B82F6"), C("#0B1F4D"), "Brillant Diamond", new[] { GameVersion.BD }, BotKind.SysBotBDSP),
        new("sw_sp", "Shining Pearl", Platform.Switch, 8, 484, C("#EC4899"), C("#4A0D2E"), "Shining Pearl", new[] { GameVersion.SP }, BotKind.SysBotBDSP),
        new("sw_pla", "Legends: Arceus", Platform.Switch, 8, 493, C("#D9C59A"), C("#4A3B1F"), "Legends Arceus", new[] { GameVersion.PLA }),
        new("sw_scarlet", "Scarlet", Platform.Switch, 9, 1007, C("#EF4444"), C("#4A0B0B"), "Scarlet", new[] { GameVersion.SL }),
        new("sw_violet", "Violet", Platform.Switch, 9, 1008, C("#8B5CF6"), C("#2A0F5C"), "Violet", new[] { GameVersion.VL }),
        new("sw_za", "Legends: Z-A", Platform.Switch, 9, 718, C("#22C55E"), C("#063D1C"), "Legends Z-A", new[] { GameVersion.ZA }),
        new("sw_frlg_fr", "FireRed (Switch)", Platform.Switch, 3, 6, C("#F05A28"), C("#5C1A05"), "Fire Red", new[] { GameVersion.FR },
            BotKind.FrlgSwitch, new[] { "0100554023408000", "01004B3023412000", "010092302342A000", "01007F8023416000", "0100EB702342C000", "01006FA0233F8000" }),
        new("sw_frlg_lg", "LeafGreen (Switch)", Platform.Switch, 3, 3, C("#5DBB46"), C("#1C4A12"), "Leaf Green", new[] { GameVersion.LG },
            BotKind.FrlgSwitch, new[] { "010034D02340E000", "010087C02342E000", "01005C7023432000", "0100FD6023430000", "01002B5023434000", "0100F1E0233FA000" }),
    };

    /// <summary>Games offered after choosing "Nintendo 3DS": everything from Game Boy up to 3DS.</summary>
    public static IEnumerable<Game> ForHandheld => All.Where(g => !g.IsSwitch);
    public static IEnumerable<Game> ForSwitch => All.Where(g => g.IsSwitch);

    public static Game Get(string id) => All.First(g => g.Id == id);
}
