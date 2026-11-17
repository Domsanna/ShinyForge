using PKHeX.Core;

namespace ShinyForge.Services;

/// <summary>
/// Reads the live party and PC boxes of Pokémon FireRed/LeafGreen on a modded Switch through sys-botbase.
/// Offsets from Lusamine's SysBot.NET FRLG research; verified on English LeafGreen 1.0.0.
/// </summary>
public sealed class LiveFrlg : IDisposable
{
    private const uint InitialSeed = 0x1208000, GbaAnchor = 0x02020000;
    private readonly SysBotSession _bot;

    /// <summary>Read-only connection (WiFi or USB, as set): it never touches the virtual controller, so a running hunt keeps it.</summary>
    public LiveFrlg(string host, int port) => _bot = new SysBotSession(host, port, controller: false);

    private byte[] Peek(uint heapOffset, int size) =>
        _bot.Peek(heapOffset, size) ?? throw new IOException("sys-botbase couldn't read the game's memory - is FireRed / LeafGreen running?");

    private static uint ToHeap(uint gba) => InitialSeed + (gba - GbaAnchor);

    private static (uint party, uint seed) Offsets(string title) => title.ToUpperInvariant() switch
    {
        "01006FA0233F8000" or "0100F1E0233FA000" => (0x120C1E0, 0xBD68D230),
        "0100554023408000" or "010034D02340E000" => (0x120C280, 0xBD68D2D0),
        _ => (0x120C280, 0xBD68D220),
    };

    public string TitleId() => _bot.TitleId() ?? throw new IOException("No game is running on the Switch.");

    /// <summary>
    /// Is a real save loaded? The trainer has a name and the first party slot holds a Pokémon. A NEW GAME (no CONTINUE on the
    /// title screen = this Switch user has no save) also sets the save pointers, but in its intro both are still empty.
    /// </summary>
    internal static bool SaveLoaded(SysBotSession bot, string title)
    {
        var (partyOff, seed) = Offsets(title);
        if (bot.Peek(seed + 8, 12) is not { } ptrs) return false;
        var sb2 = BitConverter.ToUInt32(ptrs, 4);
        if (sb2 < GbaAnchor || sb2 > 0x0203FFFF || bot.Peek(ToHeap(sb2), 8) is not { } name) return false;
        if (string.IsNullOrWhiteSpace(StringConverter3.GetString(name, false))) return false;
        return bot.Peek(partyOff, 100) is { } p && new PK3(p).Species != 0;
    }

    /// <summary>Reads trainer, party and all 14 PC boxes. Returns null when the save isn't loaded (title screen).</summary>
    public (string Trainer, ushort TID, ushort SID, List<PK3> Party, List<PK3> Boxes)? Read()
    {
        var (partyOff, seed) = Offsets(TitleId());
        var ptrs = Peek(seed + 8, 12);
        uint sb2 = BitConverter.ToUInt32(ptrs, 4), storage = BitConverter.ToUInt32(ptrs, 8);
        if (sb2 < GbaAnchor || storage < GbaAnchor || sb2 > 0x0203FFFF || storage > 0x0203FFFF)
            return null;

        var t = Peek(ToHeap(sb2), 16);
        var trainer = StringConverter3.GetString(t.AsSpan(0, 8), false);
        ushort tid = BitConverter.ToUInt16(t, 10), sid = BitConverter.ToUInt16(t, 12);

        var party = new List<PK3>();
        var pdata = Peek(partyOff, 6 * 100);
        for (int i = 0; i < 6; i++)
        {
            var pk = new PK3(pdata.AsSpan(i * 100, 100).ToArray());
            if (pk.Species != 0 && pk.ChecksumValid)
                party.Add(pk);
        }

        var boxes = new List<PK3>();
        _boxSlots.Clear();
        const int boxBytes = 14 * 30 * 80;
        var bdata = new byte[boxBytes];
        for (int off = 0; off < boxBytes; off += 0x2000)
        {
            var len = Math.Min(0x2000, boxBytes - off);
            Peek(ToHeap(storage) + 4 + (uint)off, len).CopyTo(bdata, off);
        }
        for (int i = 0; i < 14 * 30; i++)
        {
            var pk = new PK3(bdata.AsSpan(i * 80, 80).ToArray());
            if (pk.Species != 0 && pk.ChecksumValid)
            {
                boxes.Add(pk);
                _boxSlots.Add(i);
            }
        }
        return (trainer, tid, sid, party, boxes);
    }

    private readonly List<int> _boxSlots = new();

    /// <summary>
    /// Party + PC boxes as a collection. <paramref name="expected"/> = the game the caller is about: if another game runs on the
    /// Switch, nothing is read (its Pokémon would otherwise be marked in this game's tracker / Pokédex).
    /// </summary>
    public Collection ReadCollection(Core.Game? expected = null)
    {
        _boxSlots.Clear();
        var title = TitleId();
        if (expected != null && SwitchGameLauncher.TitleIds(expected) is { Length: > 0 } ids && !ids.Contains(title))
            throw new InvalidOperationException($"{SwitchGameLauncher.GameName(title)} is running on the Switch, not Pokémon {expected.Title} - its Pokémon aren't read.");
        var r = Read() ?? throw new InvalidOperationException("The game isn't loaded into a save yet (title screen?) - try again in a moment.");
        // a NEW GAME (no save for this Switch user) has valid pointers but no trainer yet: that's "not loaded", not 0 Pokémon
        if (string.IsNullOrWhiteSpace(r.Trainer))
            throw new InvalidOperationException("The game isn't loaded into a save yet (title screen or a new game without a save).");
        var c = new Collection { Source = $"Live from Switch (trainer {r.Trainer}, TID {r.TID})" };
        bool Own(PK3 pk) => pk.OriginalTrainerName == r.Trainer && pk.TID16 == r.TID && pk.SID16 == r.SID;
        var n = 0;
        foreach (var pk in r.Party)
            c.Add(pk, Own(pk), $"Party slot {++n} (live)");
        for (int i = 0; i < r.Boxes.Count; i++)
        {
            var idx = i < _boxSlots.Count ? _boxSlots[i] : -1;
            c.Add(r.Boxes[i], Own(r.Boxes[i]), idx < 0 ? "PC box (live)" : $"Box {idx / 30 + 1}, slot {idx % 30 + 1} (live)");
        }
        return c;
    }

    public void Dispose()
    {
        try { _bot.Dispose(); } catch { }
    }
}
