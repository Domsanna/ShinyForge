using PKHeX.Core;

namespace ShinyForge.Services;

/// <summary>
/// Whether a Pokémon is shiny once it moves up to later games:
///   Gen 1 / Gen 2 (Virtual Console → Poké Transporter → Pokémon Bank): the Gen 2 DV rule decides - Defense, Speed and
///   Special DVs all 10 and Attack DV 2, 3, 6, 7, 10, 11, 14 or 15 (HP DV then is 0 or 8). Transporter uses it for Gen 1 too
///   (since version 1.3; Gen 1 itself never shows shinies). Source: Bulbapedia "Shiny Pokémon".
///   Gen 3 - 5 (Pal Park, Poké Transfer, Poké Transporter): the PID and IDs are kept, so a shiny stays shiny. Gen 6+ counts a
///   "shiny value" (TID ^ SID ^ PID halves) below 16 instead of 8; for 8 - 15 Pokémon Bank is reported to change one PID bit
///   so the Pokémon stays normal (Project Pokémon forum), so those aren't promised as shiny.
/// </summary>
public static class TransferShiny
{
    /// <summary>The Gen 2 DV rule (what Pokémon Bank applies to Gen 1 and Gen 2 Pokémon).</summary>
    public static bool IsShinyDV(int atk, int def, int spe, int spc) => def == 10 && spe == 10 && spc == 10 && (atk & 2) == 2;

    /// <summary>A line for the Pokémon's info: will it be shiny after moving up? Null when there is nothing to say.</summary>
    public static string? Line(PKM pk)
    {
        if (pk.Format <= 2)
        {
            int atk = pk.IV_ATK, def = pk.IV_DEF, spe = pk.IV_SPE, spc = pk.IV_SPA;
            var dvs = $"DVs Atk {atk} Def {def} Spe {spe} Spc {spc} (HP {pk.IV_HP})";
            return IsShinyDV(atk, def, spe, spc)
                ? $"★ {dvs} - shiny when moved to Pokémon Bank (Gen 2 DV rule)" + (pk.Format == 1 ? "; Gen 1 itself doesn't show shinies." : ".")
                : $"{dvs} - not shiny after Poké Transporter (needs Def, Spe and Spc 10 and Atk 2, 3, 6, 7, 10, 11, 14 or 15).";
        }
        if (pk.Format <= 5)
        {
            var value = pk.ShinyXor;
            if (value < 8) return "★ Shiny in every later game - Pal Park, Poké Transfer and Pokémon Bank keep its PID.";
            if (value < 16) return $"Shiny value {value}: below the Gen 6+ limit of 16, but Pokémon Bank is reported to adjust such Pokémon so they stay normal.";
        }
        return null;
    }
}
