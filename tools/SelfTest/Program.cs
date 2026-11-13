using PKHeX.Core;
using ShinyForge.Core;
using ShinyForge.Services;
using ShinyForge.UI;

// Usage: SelfTest <workdir> [saves|sheet|record|live|deps|render]...
var work = args[0];
Directory.CreateDirectory(work);
var tests = args.Skip(1).ToHashSet();
int failures = 0;
void Check(bool ok, string what) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}"); if (!ok) failures++; }

if (tests.Contains("saves"))
{
    foreach (var (ver, id, species) in new[] { (GameVersion.SL, "sw_scarlet", (ushort)1007), (GameVersion.X, "3ds_x", (ushort)716), (GameVersion.SW, "sw_sword", (ushort)888) })
    {
        var game = GameCatalog.Get(id);
        var folder = Path.Combine(work, "saves", id);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);

        var sav = BlankSaveFile.Get(ver, "Dom");
        sav.TID16 = 7397; sav.SID16 = 9996;
        var pk = EntityBlank.GetBlank(sav);
        pk.Species = species; pk.CurrentLevel = 50; pk.OriginalTrainerName = "Dom"; pk.TID16 = 7397; pk.SID16 = 9996;
        pk.Version = ver; pk.Language = 2; pk.PID = 0x12345678; pk.EncryptionConstant = 0x12345678;
        pk.SetShiny();
        pk.RefreshChecksum();
        sav.SetPartySlotAtIndex(pk, 0);
        var boxed = pk.Clone(); boxed.Species = 25; boxed.SetShiny(); boxed.SetPIDGender(0); boxed.RefreshChecksum();
        sav.SetBoxSlotAtIndex(boxed, 0, 3);
        var path = Path.Combine(folder, "nested", $"{id}.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, sav.Write().ToArray());

        var fi = new FileInfo(path);
        var okLoad = SaveUtil.TryGetSaveFile(path, out var probe);
        Console.WriteLine($"   debug: size={fi.Length} sizeValid={SaveUtil.IsSizeValid(fi.Length)} load={okLoad} version={probe?.Version} gen={probe?.Generation}");
        if (!okLoad)
        {
            try { var s2 = SaveUtil.GetSaveFile(path); Console.WriteLine($"   GetSaveFile -> {s2?.GetType().Name}"); }
            catch (Exception ex) { Console.WriteLine($"   GetSaveFile threw {ex.GetType().Name}: {ex.Message}"); }
            var mem = File.ReadAllBytes(path);
            Console.WriteLine($"   TryGetSaveFile(bytes) -> {SaveUtil.TryGetSaveFile(mem, out var s3, path)} {s3?.Version}");
        }
        var found = SaveService.FindSaves(folder, game);
        Check(found.Count == 1, $"{id}: save found in a sub-folder ({found.Count})");
        if (found.Count == 0) continue;
        var (outDir, count) = SaveService.ExportPokemon(found[0].Save, game, folder);
        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
        Check(count == 2 && files.Count == 2, $"{id}: exported {count} Pokémon -> {Path.GetFileName(outDir)} ({string.Join(", ", files.Select(Path.GetFileName))})");
        var reread = EntityFormat.GetFromBytes(File.ReadAllBytes(files[0]));
        Check(reread != null && reread.Species != 0, $"{id}: exported file re-opens with PKHeX ({reread?.GetType().Name} species {reread?.Species})");
        var info = SaveService.WriteGameInfo(found[0].Save, game, found[0].Path, folder);
        var text = File.ReadAllText(info);
        Check(text.Contains("Dom") && text.Contains("7397"), $"{id}: game info.txt written");
        if (id == "3ds_x") Console.WriteLine(text);
        var col = Collection.FromSave(found[0].Save, path);
        Check(col.BySpecies.Count == 2 && col.BySpecies.Values.All(e => e.OwnOT && e.Shiny), $"{id}: collection {string.Join(", ", col.BySpecies.Values.Select(e => $"{e.Name}(OT={e.OwnOT},shiny={e.Shiny})"))}");
    }
}

if (tests.Contains("realsaves"))
{
    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    var roc = Path.Combine(desk, @"RoCs-PC-master\RoCs-PC-master");
    var pk = Path.Combine(desk, "PKHeX (26.10.18)");
    var cases = new (string Game, string Src)[]
    {
        ("gbc_crystal", $@"{roc}\03 - Gen II - GSC Collection\Save Data\Pokemon Crystal.sav"),
        ("gba_firered", $@"{roc}\06 - Gen III - FRLG Collection\Save Data\Pokemon Fire Red.sav"),
        ("gba_leafgreen", $@"{pk}\LeafGreen_e.sav"),
        ("gba_emerald", $@"{roc}\05 - Gen III - RSE Collection\Save Data\Pokemon Emerald.sav"),
        ("nds_heartgold", $@"{pk}\Pokemon HeartGold.sav"),
        ("nds_black", $@"{pk}\Pokemon Black.sav"),
        ("nds_white2", $@"{roc}\15 - Gen V - B2W2 Collection\Save Data\Pokemon White 2.sav"),
        ("3ds_x", $@"{roc}\19 - Gen VI - XY Collection\Save Data\20180219163039 - X\0000055d\main"),
        ("3ds_or", $@"{roc}\20 - Gen VI - ORAS Collection\Save Data\20180219163050 - OR\000011c4\main"),
        ("3ds_usun", $@"{roc}\25 - Gen VII - USUM Collection\Save Data\20180228134249 - Ultra Sun\00001b50\main"),
        ("sw_bd", $@"{roc}\33 - Gen VIII - BDSP Collection\Save Data\0100000011D90000 - BD\SaveData.bin"),
        ("sw_shield", $@"{pk}\shield\20210420-120931 Dom\main"),
        ("sw_pla", $@"{pk}\arceus\20220123-152959 Dom\main"),
        ("sw_violet", $@"{pk}\violet\OG Playthrough\main"),
        ("sw_za", $@"{pk}\legends za\main"),
    };
    foreach (var (id, src) in cases)
    {
        var game = GameCatalog.Get(id);
        if (!File.Exists(src)) { Console.WriteLine($"SKIP  {id}: {src} missing"); continue; }
        var folder = Path.Combine(work, "real", id);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(Path.Combine(folder, "exported"));
        File.Copy(src, Path.Combine(folder, "exported", Path.GetFileName(src)));
        var found = SaveService.FindSaves(folder, game);
        if (found.Count == 0)
        {
            SaveUtil.TryGetSaveFile(Path.Combine(folder, "exported", Path.GetFileName(src)), out var any);
            Check(false, $"{id}: save not matched (PKHeX sees {any?.Version.ToString() ?? "nothing"})");
            continue;
        }
        var sav = found[0].Save;
        var (outDir, count) = SaveService.ExportPokemon(sav, game, folder);
        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
        var info = File.ReadAllText(SaveService.WriteGameInfo(sav, game, found[0].Path, folder));
        var col = Collection.FromSave(sav, src);
        var reopen = files.Count == 0 || EntityFormat.GetFromBytes(File.ReadAllBytes(files[0])) is { Species: > 0 };
        Check(count == files.Count && reopen && info.Contains(sav.OT),
            $"{id,-14} {sav.Version,-4} OT {sav.OT,-8} TID {sav.DisplayTID,-6} -> {count} Pokémon exported, {col.BySpecies.Count} species, {col.BySpecies.Values.Count(e => e.Shiny)} shiny");
        if (id == "sw_shield") Console.WriteLine(info);
    }
}

if (tests.Contains("sheet"))
{
    var src = SpreadsheetService.FindWorkbook(GameCatalog.Get("gb_red"), AppSettings.Current.SpreadsheetRoot)!;
    Check(src != null, $"Red tracker located: {src}");
    var copy = Path.Combine(work, "Red Pokedex Tracker (TEST COPY).xlsm");
    File.Copy(src!, copy, true);
    var col = new Collection();
    col.BySpecies[Collection.Normalize("Bulbasaur")] = new Collection.Entry { Name = "Bulbasaur", Owned = true, OwnOT = true, Shiny = true };
    col.BySpecies[Collection.Normalize("Nidoran♀")] = new Collection.Entry { Name = "Nidoran♀", Owned = true };
    col.BySpecies[Collection.Normalize("Mr. Mime")] = new Collection.Entry { Name = "Mr. Mime", Owned = true, OwnOT = true };
    var t = new Thread(() =>
    {
        var r = SpreadsheetService.Update(copy, col, s => Console.WriteLine("   " + s));
        Console.WriteLine($"   result: inBox+{r.MarkedInBox} OT+{r.MarkedOT} shiny+{r.MarkedShiny} sheets={r.Sheets}");
        Check(r.MarkedInBox >= 3 && r.MarkedOT >= 2, "Red tracker copy updated through Excel");
    });
    t.SetApartmentState(ApartmentState.STA); t.Start(); t.Join();
}

if (tests.Contains("record"))
{
    var rec = new RecordingService();
    var dir = Path.Combine(work, "recordings");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    var (video, audio) = RecordingService.ListDevices();
    Console.WriteLine($"   video devices: {string.Join(" | ", video)}\n   audio devices: {string.Join(" | ", audio)}");
    rec.Status += s => Console.WriteLine("   rec: " + s);
    rec.Start(RecordingService.WindowPrefix + args.SkipWhile(a => a != "--window").Skip(1).FirstOrDefault() ?? "", RecordingService.NoAudio, dir, 1, 6);
    Thread.Sleep(14000);
    rec.Stop();
    var files = Directory.EnumerateFiles(dir, "Recording_*.mp4").OrderBy(f => f).ToList();
    Check(files.Count >= 2, $"segmented recording produced {files.Count} numbered files: {string.Join(", ", files.Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length / 1024} KB)"))}");
    Check(RecordingService.NextNumber(dir) == files.Count + 1, "next recording continues the numbering");
}

if (tests.Contains("live"))
{
    using var live = new LiveFrlg(AppSettings.Current.SwitchIP, AppSettings.Current.SwitchPort);
    for (int i = 0; i < 40; i++)
    {
        try
        {
            var c = live.ReadCollection();
            Console.WriteLine($"   {c.Source}: {c.Total} Pokémon, {c.BySpecies.Count} species, {c.BySpecies.Values.Count(e => e.Shiny)} shiny species");
            Console.WriteLine("   e.g. " + string.Join(", ", c.BySpecies.Values.Take(8).Select(e => e.Name)));
            Check(c.Total > 6, "live party + PC boxes read from the Switch");
            break;
        }
        catch (InvalidOperationException) { Thread.Sleep(1500); } // title screen during the hunt's reset - retry
    }
}

if (tests.Contains("deps"))
{
    var updates = await DependencyService.CheckUpdates();
    foreach (var d in DependencyService.All) Console.WriteLine($"   {d.Name,-20} {d.State,-16} {d.Detail}");
    foreach (var u in updates) Console.WriteLine($"   UPDATE {u.Item}: {u.Message}");
    Check(DependencyService.All.First(d => d.Name == "FFmpeg").State != DepState.Missing, "FFmpeg detected");
}

if (tests.Contains("3ds"))
{
    // ---- Simulated 3DS: Luma3DS GDB stub (TCP 4000) + InputRedirection receiver (UDP 4950) running Ultra Moon.
    var roc = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), @"RoCs-PC-master\RoCs-PC-master");
    SaveUtil.TryGetSaveFile($@"{roc}\25 - Gen VII - USUM Collection\Save Data\20180228134254 - Ultra Moon\00001b51\main", out var umSave);
    var pool = SaveService.Boxes(umSave!).Select(b => b.Pk).Where(p => !p.IsShiny).Take(5).Cast<PK7>().ToList();
    var rng = new Random(7);
    foreach (var p in pool) { do { p.PID = (uint)rng.Next(); } while (p.IsShiny); p.EncryptionConstant = (uint)rng.Next(); p.RefreshChecksum(); }
    var shinyMon = (PK7)pool[2].Clone(); shinyMon.SetShiny(); shinyMon.RefreshChecksum();
    var encounters = new List<PK7> { pool[0], pool[1], shinyMon };
    var memory = new Dictionary<uint, byte[]>();
    byte[] Enc(PKM pk) { var b = new byte[232]; pk.WriteEncryptedDataStored(b); return b; }
    memory[0x3254F4AC] = new byte[232];
    var trainer = new byte[4]; BitConverter.GetBytes((ushort)12345).CopyTo(trainer, 0); BitConverter.GetBytes((ushort)54321).CopyTo(trainer, 2);
    memory[0x33012818] = trainer;
    memory[0x33012850] = System.Text.Encoding.Unicode.GetBytes("Dom\0\0\0\0\0\0\0\0\0\0");
    int next = 0, aPresses = 0, resets = 0;
    bool inBattle = false;
    var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 4950));
    var cts = new CancellationTokenSource();
    uint lastHid = 0xFFF;
    _ = Task.Run(() =>
    {
        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        while (!cts.IsCancellationRequested)
        {
            byte[] p;
            try { p = udp.Receive(ref ep); } catch { break; }
            if (p.Length != 20) continue;
            var hid = BitConverter.ToUInt32(p, 0);
            var pressed = ~hid & lastHid & 0xFFF; // newly pressed (active-low)
            lastHid = hid;
            lock (memory)
            {
                if ((~hid & 0x30C) == 0x30C && (pressed & 0x30C) != 0) { inBattle = false; resets++; aPresses = 0; } // L+R+Start+Select
                else if ((pressed & 1) != 0 && !inBattle && ++aPresses >= 3 && next < encounters.Count)
                {
                    memory[0x3254F4AC] = Enc(encounters[next++]);
                    inBattle = true;
                }
            }
        }
    });
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 4000);
    listener.Start();
    _ = Task.Run(() =>
    {
        using var client = listener.AcceptTcpClient();
        var s = client.GetStream();
        void Send(string payload)
        {
            var sum = (payload.Sum(c => (int)c) & 0xFF).ToString("x2");
            var b = System.Text.Encoding.ASCII.GetBytes($"${payload}#{sum}");
            s.Write(b, 0, b.Length);
        }
        while (!cts.IsCancellationRequested)
        {
            int c;
            try { c = s.ReadByte(); } catch { break; }
            if (c < 0) break;
            if (c == 0x03) { Send("T02"); continue; }
            if (c != '$') continue;
            var sb = new System.Text.StringBuilder();
            while ((c = s.ReadByte()) != '#') sb.Append((char)c);
            s.ReadByte(); s.ReadByte(); s.WriteByte((byte)'+');
            var cmd = sb.ToString();
            if (cmd.StartsWith("qSupported")) Send("PacketSize=400;multiprocess+");
            else if (cmd == "!") Send("OK");
            else if (cmd.StartsWith("qXfer:osdata:read:processes"))
                Send("l<?xml version=\"1.0\"?><osdata type=\"processes\"><item><column name=\"pid\">0x10</column><column name=\"command\">pm</column></item>" +
                     "<item><column name=\"pid\">0x2b</column><column name=\"command\">momiji</column><column name=\"title\">00040000001B5100</column></item></osdata>");
            else if (cmd.StartsWith("vAttach")) Send("T05");
            else if (cmd.StartsWith("vCont")) { }
            else if (cmd.StartsWith("D")) Send("OK");
            else if (cmd.StartsWith("m"))
            {
                var parts = cmd[1..].Split(',');
                uint addr = Convert.ToUInt32(parts[0], 16); int len = Convert.ToInt32(parts[1], 16);
                var outb = new byte[len];
                lock (memory)
                    foreach (var (a, data) in memory)
                        for (int i = 0; i < len; i++)
                        {
                            long k = addr + i - (long)a;
                            if (k >= 0 && k < data.Length) outb[i] = data[k];
                        }
                Send(Convert.ToHexString(outb).ToLowerInvariant());
            }
            else Send("");
        }
    });

    var bot = new ShinyForge.Services.ThreeDS.Bot3DS(new ShinyForge.Services.ThreeDS.Bot3DSOptions
    {
        Host = "127.0.0.1", Mode = ShinyForge.Services.ThreeDS.Hunt3DSMode.Auto, ResetWaitMs = 500,
        LogFile = Path.Combine(work, "3ds_test_encounters.csv"),
    });
    if (File.Exists(Path.Combine(work, "3ds_test_encounters.csv"))) File.Delete(Path.Combine(work, "3ds_test_encounters.csv"));
    var shinySeen = false;
    bot.Log += s => Console.WriteLine("   3DS: " + s.Replace("\n", "\n        "));
    bot.Encounter += (pk, shiny) => shinySeen |= shiny;
    var game = bot.Connect("3ds_umoon");
    Check(game.Name == "Pokémon Ultra Moon", $"game detected from the process list: {game.Name}");
    Check(bot.Trainer.Name == "Dom" && bot.Trainer.TID == 12345, $"trainer read: {bot.Trainer.Name} {bot.Trainer.TID}/{bot.Trainer.SID}");
    var run = Task.Run(() => bot.Run(new CancellationTokenSource(TimeSpan.FromMinutes(3)).Token));
    run.Wait(TimeSpan.FromMinutes(3.5));
    bot.Close();
    cts.Cancel(); udp.Dispose(); listener.Stop();
    var rows = File.ReadAllLines(Path.Combine(work, "3ds_test_encounters.csv"));
    Check(shinySeen && next == 3, $"bot soft-reset through {next} encounters ({resets} resets) and stopped on the shiny");
    Check(rows.Length == 4, $"encounter log has {rows.Length - 1} rows: {string.Join(" | ", rows.Skip(1).Select(r => string.Join(",", r.Split(',').Skip(3).Take(3))))}");
}

if (tests.Contains("sheets"))
{
    // Every game must auto-recognize its tracker (except Gen 7 / Let's Go, whose folders are empty).
    var root = AppSettings.Current.SpreadsheetRoot;
    foreach (var g in GameCatalog.All)
    {
        var wb = SpreadsheetService.FindWorkbook(g, root);
        var expectMissing = g.Gen == 7 || string.IsNullOrWhiteSpace(g.Spreadsheet);
        var nameOk = wb == null || Collection.Normalize(Path.GetFileName(wb)).Contains(Collection.Normalize(g.Spreadsheet));
        Check((wb != null || expectMissing) && nameOk, $"{g.Id,-16} '{g.Spreadsheet}' -> {(wb == null ? "(none)" : wb.Replace(root, "<root>"))}");
    }
    // Bundled fallback: a fake bundled tree is found when the user's folder doesn't exist.
    var fake = Path.Combine(work, "fake_bundle");
    var gr = GameCatalog.Get("gb_red");
    var bundledFolder = Path.Combine(fake, "Gen 1", "(1) Red");
    Directory.CreateDirectory(bundledFolder);
    File.WriteAllText(Path.Combine(bundledFolder, "Red Pokedex Tracker.xlsm"), "x");
    Check(SpreadsheetService.FindWorkbook(gr, Path.Combine(work, "does-not-exist")) is var none && (none == null || none.Contains("Spreadsheets")),
        $"no user folder -> bundled or none ({none ?? "none"})");
    Check(SpreadsheetService.FindWorkbook(gr, fake)?.EndsWith("Red Pokedex Tracker.xlsm") == true, "tracker found in an alternative root");
    Console.WriteLine("   bundled root: " + SpreadsheetService.BundledRoot);
}

if (tests.Contains("dex"))
{
    foreach (var g in GameCatalog.All)
    {
        var withForms = DexData.Build(g, true);
        var plain = DexData.Build(g, false);
        var noSprite = withForms.Where(e => e.Sprite.Length == 0 || !File.Exists(Path.Combine(AppPaths.AssetsDir, e.Sprite))).ToList();
        var generic = withForms.Where(e => !e.GameSprite).ToList();
        var basesGeneric = plain.Count(e => !e.GameSprite);
        // A few Pokémon are missing from some sprite sets (e.g. Poipole..Zeraora in Ultra Sun/Moon) - those use the generic sprite.
        Check(plain.Count > 100 && noSprite.Count == 0 && basesGeneric <= 6,
            $"{g.Id,-16} [{DexData.SpriteSet(g)}] {plain.Count} species, {withForms.Count} entries ({withForms.Count(e => e.Female)} F, {withForms.Count(e => e.Form > 0)} forms), " +
            $"game sprites {withForms.Count - generic.Count}/{withForms.Count}" +
            (generic.Count > 0 ? " generic: " + string.Join(", ", generic.Take(10).Select(e => e.Display)) + (generic.Count > 10 ? "..." : "") : "") +
            (noSprite.Count > 0 ? " NO SPRITE: " + string.Join(", ", noSprite.Take(8).Select(e => e.Display)) : ""));
    }
    var sw = GameCatalog.Get("sw_sword");
    var sword = DexData.Build(sw, true);
    Console.WriteLine("   Sword sample: " + string.Join(" | ", sword.Where(e => e.Species is 52 or 77 or 122 or 554 or 555 or 25 or 849 or 892).Select(e => $"{e.Display}={e.Sprite}")));
    var fr = GameCatalog.Get("gba_firered");
    var frDex = DexData.Build(fr, true);
    Check(frDex.Count(e => e.Species == 201) == 28 && frDex.All(e => e.Species <= 386), $"FireRed: {frDex.Count} entries, Unown forms {frDex.Count(e => e.Species == 201)}, max #{frDex.Max(e => e.Species)}");
    var hg = DexData.Build(GameCatalog.Get("nds_heartgold"), true);
    Check(hg.Any(e => e.Species == 3 && e.Female) && hg.Any(e => e.Species == 25 && e.Female), $"HeartGold has female Venusaur/Pikachu entries ({hg.Count(e => e.Female)} ♀)");
    void Info(Game g, ushort sp, byte form = 0)
    {
        var e = DexData.Build(g, true).First(x => x.Species == sp && x.Form == form && !x.Female);
        var where = DexData.WhereToFind(g, e);
        Console.WriteLine($"   [{g.Title}] {e.Display}\n      where: {string.Join("\n             ", where.Take(6))}\n      evo: {DexData.Evolution(g, e).Replace("\n", " / ")}\n      events: {string.Join("; ", DexData.Events(g, e).Take(4))}");
        Check(where.Count > 0 && !where[0].StartsWith("Encounter data unavailable"), $"info for {e.Display} in {g.Title}");
    }
    Info(fr, 386);                                   // Deoxys - Birth Island static
    Info(fr, 16);                                    // Pidgey - wild
    Info(fr, 133);                                   // Eevee - gift, evolutions by stone
    Info(GameCatalog.Get("3ds_umoon"), 19, 1);       // Alolan Rattata
    Info(sw, 52, 2);                                 // Galarian Meowth
    Info(GameCatalog.Get("sw_violet"), 1007);        // Miraidon
    // Owned data rendered through the real save
    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    var frSave = Path.Combine(desk, @"RoCs-PC-master\RoCs-PC-master\06 - Gen III - FRLG Collection\Save Data\Pokemon Fire Red.sav");
    if (File.Exists(frSave) && SaveUtil.TryGetSaveFile(frSave, out var frs) && frs != null)
    {
        var col = Collection.FromSave(frs, frSave);
        Check(col.Mons.Count == col.Total && col.Mons.All(m => m.Where.Length > 0), $"FireRed save: {col.Mons.Count} owned with locations, {col.DexCaught.Count} dex-caught");
    }
}

if (tests.Contains("dexui"))
{
    ApplicationConfiguration.Initialize();
    var outDir = Path.Combine(work, "screens");
    Directory.CreateDirectory(outDir);
    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    foreach (var (id, save) in new[]
             {
                 ("gba_firered", @"RoCs-PC-master\RoCs-PC-master\06 - Gen III - FRLG Collection\Save Data\Pokemon Fire Red.sav"),
                 ("sw_shield", @"PKHeX (26.10.18)\shield\20210420-120931 Dom\main"),
                 ("nds_heartgold", @"PKHeX (26.10.18)\Pokemon HeartGold.sav"),
             })
    {
        var path = Path.Combine(desk, save);
        var form = new DexForm(GameCatalog.Get(id), _ => SaveUtil.TryGetSaveFile(path, out var s) && s != null ? Collection.FromSave(s, path) : null)
        { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        form.Show();
        for (int i = 0; i < 60; i++) { Application.DoEvents(); Thread.Sleep(100); }
        // click the first caught entry and the first missing entry
        var grid = form.Controls.OfType<DexGrid>().First();
        void Snap(string name)
        {
            for (int i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(100); }
            using var bmp = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
            bmp.Save(Path.Combine(outDir, name + ".png"));
            Console.WriteLine("   rendered " + name);
        }
        grid.Focus();
        grid.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 50, 50, 0) });
        Snap($"dex_{id}_1");
        grid.GetType().GetMethod("OnMouseDown", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, 50 + 3 * 102, 50 + 118, 0) });
        Snap($"dex_{id}_2");
        form.Close();
    }
}

if (tests.Contains("update"))
{
    Console.WriteLine($"   this build: {ShinyForge.Core.AppInfo.Display}, repo {ShinyForge.Core.AppInfo.Repo}");
    Check(ShinyForge.Core.AppInfo.ParseTag("v1.0.12") == new Version(1, 0, 12), "tag v1.0.12 parsed");
    Check(ShinyForge.Core.AppInfo.ParseTag("1.2.3-beta") == new Version(1, 2, 3), "tag 1.2.3-beta parsed");
    Check(ShinyForge.Core.AppInfo.ParseTag("latest") == null, "non-version tag ignored");
    var json = System.Text.Json.Nodes.JsonNode.Parse("""
        {"tag_name":"v9.0.500","name":"ShinyForge 9.0 (build 500)","body":"## What's new\n- **Faster** hunts\n- fixed a bug","html_url":"https://github.com/x/ShinyForge/releases/tag/v9.0.500",
         "published_at":"2026-11-13T12:00:00Z","assets":[
           {"name":"ShinyForge-Setup.exe","browser_download_url":"https://example/setup.exe","size":1500000000},
           {"name":"ShinyForge-Portable.zip","browser_download_url":"https://example/portable.zip","size":157000000}]}
        """)!.AsObject();
    var rel = AppUpdateService.Parse(json)!;
    Check(rel.Build == 500 && rel.IsNewer && rel.SetupUrl!.EndsWith("setup.exe") && rel.ZipUrl!.EndsWith("portable.zip") && rel.SetupSize == 1500000000,
        $"release parsed: build {rel.Build}, newer={rel.IsNewer}, setup+zip found");
    var old = AppUpdateService.Parse(System.Text.Json.Nodes.JsonNode.Parse("""{"tag_name":"v0.9.1","assets":[]}""")!.AsObject())!;
    Check(!old.IsNewer, "an older release is not offered");
    var plain = ShinyForge.UI.UpdateForm.Plain("## What's new\n- **Faster** hunts\nauto - catch");
    Check(plain.Contains("WHAT'S NEW") && plain.Contains("•  Faster hunts") && plain.Contains("auto - catch"), "release notes shown as plain text");
    Check(Browser.Split("\"C:\\Program Files\\Mozilla Firefox\\firefox.exe\" -osint -url \"%1\"") is { Exe: var fx, Args: "-osint -url \"%1\"" } && fx.EndsWith("firefox.exe"),
        "browser command line split");
    Check(Browser.Split("C:\\x\\chrome.exe --single-argument %1") is { Exe: "C:\\x\\chrome.exe" }, "unquoted browser command split");
    var cmd = Browser.DefaultBrowserCommand();
    Console.WriteLine($"   default browser: {cmd}");
    Check(cmd != null && !cmd.Contains("iexplore", StringComparison.OrdinalIgnoreCase), "default web browser found (not the .html file handler)");

    // Portable self-update, for real: an old copy gets the new program files, the user's folders stay as they were.
    var upd = Path.Combine(work, "update_test");
    if (Directory.Exists(upd)) Directory.Delete(upd, true);
    var appDir = Path.Combine(upd, "ShinyForge Portable");
    Directory.CreateDirectory(Path.Combine(appDir, "Data"));
    Directory.CreateDirectory(Path.Combine(appDir, "Backup Tools"));
    File.WriteAllText(Path.Combine(appDir, "ShinyForge.exe"), "OLD");
    File.WriteAllText(Path.Combine(appDir, "portable.flag"), "mine");
    File.WriteAllText(Path.Combine(appDir, "Data", "settings.json"), "my settings");
    File.WriteAllText(Path.Combine(appDir, "Backup Tools", "tool.txt"), "my tool");
    var newDir = Path.Combine(upd, "new", "ShinyForge Portable");
    Directory.CreateDirectory(Path.Combine(newDir, "Data"));
    Directory.CreateDirectory(Path.Combine(newDir, "PythonBot"));
    File.WriteAllText(Path.Combine(newDir, "ShinyForge.exe"), "NEW");
    File.WriteAllText(Path.Combine(newDir, "PythonBot", "start_hunt.bat"), "new bot");
    File.WriteAllText(Path.Combine(newDir, "portable.flag"), "from zip");
    File.WriteAllText(Path.Combine(newDir, "Data", "settings.json"), "SHOULD NOT OVERWRITE");
    var zip = Path.Combine(upd, "ShinyForge-Portable.zip");
    System.IO.Compression.ZipFile.CreateFromDirectory(Path.GetDirectoryName(newDir)!, zip);
    using (var gone = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true })!) gone.WaitForExit();
    var deadPid = 999_999; // no such process: the script must not wait
    var script = AppUpdateService.PreparePortableUpdate(zip, Path.Combine(upd, "work"), appDir, deadPid, restart: false);
    using (var run = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { CreateNoWindow = true, UseShellExecute = false })!)
        Check(run.WaitForExit(60_000) && run.ExitCode == 0, $"update script ran (exit {(run.HasExited ? run.ExitCode : -1)})");
    Check(File.ReadAllText(Path.Combine(appDir, "ShinyForge.exe")) == "NEW", "program files replaced");
    Check(File.Exists(Path.Combine(appDir, "PythonBot", "start_hunt.bat")), "new files added");
    Check(File.ReadAllText(Path.Combine(appDir, "Data", "settings.json")) == "my settings", "Data folder kept");
    Check(File.ReadAllText(Path.Combine(appDir, "portable.flag")) == "mine" && File.Exists(Path.Combine(appDir, "Backup Tools", "tool.txt")), "portable.flag and Backup Tools kept");
}

if (tests.Contains("updatelive"))
{
    var rel = await AppUpdateService.CheckAsync();
    Console.WriteLine($"   GitHub latest: {rel?.Tag} build {rel?.Build}, setup {rel?.SetupSize / 1048576} MB, zip {rel?.ZipSize / 1048576} MB, page {rel?.PageUrl}");
    Check(rel != null && rel.SetupUrl != null && rel.ZipUrl != null, "latest release found on GitHub with both downloads");
    Check(rel != null && rel.IsNewer == (rel.Version > ShinyForge.Core.AppInfo.Comparable), $"this copy ({ShinyForge.Core.AppInfo.Display}) compares correctly");
}

if (tests.Contains("health"))
{
    // built-in checks
    HealthMonitor.RegisterBuiltIns();
    HealthMonitor.RunNow();
    Check(HealthMonitor.Items.All(i => i.State != HealthState.Error), $"built-in checks: {HealthMonitor.Items.Count} problem(s) {string.Join("; ", HealthMonitor.Items.Select(i => i.Message))}");

    // a check that throws must not break the monitor
    HealthMonitor.Register("broken", () => throw new InvalidOperationException("boom"));
    HealthItem? raised = null;
    HealthMonitor.Problem += i => raised ??= i;
    HealthMonitor.RunNow();
    Check(raised?.State == HealthState.Error && raised.Message.Contains("boom"), "a crashing check is reported, not fatal");
    HealthMonitor.Unregister("broken");
    HealthMonitor.RunNow();
    Check(HealthMonitor.Items.All(i => !i.Message.Contains("boom")), "unregistered check's problems are cleared");

    // same-Pokemon detection in the Python bot's log
    var csvPath = Path.Combine(work, "encounters_test.csv");
    var since = DateTime.Now.AddMinutes(-1);
    string Row(int n, string pid) => $"{n},{DateTime.Now:yyyy-MM-dd HH:mm:ss},normal,Deoxys,30,71,71,97,42,95,38,95,5,7,26,1,11,1,51,0,0,0,0,0,0,Bashful,0,Dragon 54,False,57940,{pid},";
    File.WriteAllLines(csvPath, new[] { "header", Row(1, "11111111"), Row(2, "22222222"), Row(3, "33333333") });
    Check(HuntService.FindRepeat(csvPath, since) == null, "different Pokémon -> no repeat");
    File.WriteAllLines(csvPath, new[] { "header", Row(1, "11111111"), Row(2, "50CA8977"), Row(3, "50CA8977"), Row(4, "50CA8977") });
    Check(HuntService.FindRepeat(csvPath, since)?.Pid == "50CA8977", "same Pokémon 3x in a row -> detected");
    Check(HuntService.FindRepeat(csvPath, DateTime.Now.AddMinutes(5)) == null, "old rows (before the hunt started) are ignored");
    var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\Switch 1 Pokemon Bot\encounters.csv");
    if (File.Exists(real))
        Console.WriteLine($"   real log since 09:52: {HuntService.FindRepeat(real, new DateTime(2026, 11, 13, 9, 52, 0))?.Pid ?? "no 3-in-a-row at the end"}");

    // Switch IP is written into the Python bot's config.yaml
    var cfgDir = Path.Combine(work, "botcfg");
    Directory.CreateDirectory(cfgDir);
    File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\Switch 1 Pokemon Bot\config.yaml"), Path.Combine(cfgDir, "config.yaml"), true);
    HuntService.SyncBotConfig(cfgDir, "192.168.1.77", 6000);
    var cfgText = File.ReadAllText(Path.Combine(cfgDir, "config.yaml"));
    Check(cfgText.Contains("host: 192.168.1.77") && cfgText.Contains("# <-- CHANGE") && cfgText.Contains("port: 6000"), "Switch IP synced into config.yaml (comments kept)");

    // reachability
    Check(HealthMonitor.CanReach(AppSettings.Current.SwitchIP, 6000), $"Switch answers at {AppSettings.Current.SwitchIP}:6000");
    Check(!HealthMonitor.CanReach("192.168.0.250", 6000, 1500), "a wrong IP is detected as unreachable");

    // recording: ffmpeg dies -> the health check restarts it
    ApplicationConfiguration.Initialize();
    var win = new Form { Text = "ShinyForgeHealthTest", Size = new Size(320, 240), StartPosition = FormStartPosition.Manual, Location = new Point(50, 50), BackColor = Color.SteelBlue };
    win.Show(); Application.DoEvents();
    var recDir = Path.Combine(work, "health_rec");
    if (Directory.Exists(recDir)) Directory.Delete(recDir, true);
    var rec = new RecordingService();
    rec.Start(RecordingService.WindowPrefix + "ShinyForgeHealthTest", RecordingService.NoAudio, recDir, 1, 60);
    for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(100); }
    Check(rec.CheckHealth() == null, "healthy recording -> nothing to report");
    foreach (var p in System.Diagnostics.Process.GetProcessesByName("ffmpeg")) try { p.Kill(); } catch { }
    Thread.Sleep(1000);
    var res = rec.CheckHealth();
    for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(100); }
    Check(res is { Error: true } && res.Value.Message.Contains("restarted") && rec.IsRecording, $"crashed recording restarted automatically: {res?.Message}");
    rec.Stop();
    Check(rec.CheckHealth() == null, "stopped by the user -> not restarted");
    win.Close();
}

if (tests.Contains("features"))
{
    var s = AppSettings.Current;
    var keep = (s.DiscordWebhookUrl, s.NtfyTopic, s.NtfyServer, s.OverlayEnabled, s.RemoteEnabled, s.WebPort, s.AutoBackups);

    // ---- notifications against a fake Discord + ntfy server on localhost
    var got = new List<string>();
    var fake = new System.Net.HttpListener();
    fake.Prefixes.Add("http://127.0.0.1:8798/");
    fake.Start();
    _ = Task.Run(async () =>
    {
        while (fake.IsListening)
        {
            try
            {
                var ctx = await fake.GetContextAsync();
                using var rd = new StreamReader(ctx.Request.InputStream);
                lock (got) got.Add($"{ctx.Request.Url!.AbsolutePath} title={ctx.Request.Headers["Title"]} body={rd.ReadToEnd()}");
                ctx.Response.StatusCode = 200; ctx.Response.Close();
            }
            catch { break; }
        }
    });
    s.DiscordWebhookUrl = "http://127.0.0.1:8798/discord"; s.NtfyServer = "http://127.0.0.1:8798"; s.NtfyTopic = "test-topic";
    var res = await NotifyService.SendAsync("Test shiny", "Deoxys appeared", important: true);
    Thread.Sleep(500);
    Check(res.Contains("Discord: sent") && res.Contains("ntfy: sent") && got.Any(g => g.StartsWith("/discord") && g.Contains("Deoxys")) && got.Any(g => g.StartsWith("/test-topic") && g.Contains("Test shiny")),
        $"notifications sent to Discord + ntfy ({res})");
    // the Python bot's notifier
    var py = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python",
        "-c \"import sys; sys.path.insert(0, r'" + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\Switch 1 Pokemon Bot") + "'); " +
        "from shinybot.extras import notify; notify({'notify': {'discord_webhook': 'http://127.0.0.1:8798/pydiscord', 'ntfy_topic': 'pytopic', 'ntfy_server': 'http://127.0.0.1:8798'}}, 'Py shiny', 'from python', important=True)\"")
        { UseShellExecute = false, RedirectStandardOutput = true })!;
    Console.WriteLine("   python: " + py.StandardOutput.ReadToEnd().Trim());
    py.WaitForExit();
    Thread.Sleep(300);
    Check(got.Any(g => g.StartsWith("/pydiscord")) && got.Any(g => g.StartsWith("/pytopic") && g.Contains("Py shiny")), "the Python bot sends notifications too");
    fake.Stop();

    // ---- backups
    s.AutoBackups = true;
    var bdir = Path.Combine(work, "backup_test"); Directory.CreateDirectory(bdir);
    var savePath = Path.Combine(bdir, "test.sav");
    File.WriteAllText(savePath, "version 1");
    var b1 = BackupService.Backup(savePath, false);
    var b2 = BackupService.Backup(savePath, false);
    Thread.Sleep(1100);
    File.WriteAllText(savePath, "version 2 - changed");
    var b3 = BackupService.Backup(savePath, false);
    Check(b1 != null && b2 == null && b3 != null, "backup made, skipped when unchanged, made again after a change");
    var entry = BackupService.List().First(e => e.Copy == b1);
    BackupService.Restore(entry);
    Check(File.ReadAllText(savePath) == "version 1", "restore puts the old version back");
    Check(BackupService.List().Where(e => e.Original == Path.GetFullPath(savePath)).Any(e => File.ReadAllText(e.Copy) == "version 2 - changed"),
        "the version that was replaced is still in the backups (restore can be undone)");

    // ---- hunt statistics from the real log
    var src = HuntStats.Sources().FirstOrDefault(x => x.Name.Contains("Python"));
    if (src != null)
    {
        var st = HuntStats.Compute(src, false);
        Console.WriteLine($"   stats: total {st.Total}, phase {st.Phase}, odds 1/{1 / st.Odds:0}, chance {st.ChanceByNow:P1}, {st.PerHour:0}/h, 50% at {st.To50}, best IV {st.BestIv?.IvTotal}, milestone '{st.Milestone}'");
        Check(st.Total > 100 && Math.Abs(1 / st.Odds - 8192) < 1 && st.To50 == 5678 && st.Species.Count > 0, "hunt statistics computed from encounters.csv");
        Check(Math.Abs(HuntStats.BaseOdds(8, true) - (1 - Math.Pow(1 - 1 / 4096.0, 3))) < 1e-12, "Shiny Charm odds (3 rolls, Gen 8)");
    }

    // ---- web server: overlay + phone remote
    s.OverlayEnabled = true; s.RemoteEnabled = true; s.WebPort = 8799;
    WebServer.Apply();
    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    var overlay = await http.GetStringAsync("http://127.0.0.1:8799/overlay");
    var stats = await http.GetStringAsync("http://127.0.0.1:8799/api/stats");
    var badPin = await http.GetAsync("http://127.0.0.1:8799/remote?pin=000");
    var remote = await http.GetStringAsync($"http://127.0.0.1:8799/remote?pin={WebServer.Pin}");
    Check(overlay.Contains("api/stats") && stats.Contains("counter") && badPin.StatusCode == System.Net.HttpStatusCode.Forbidden && remote.Contains("Start"),
        $"overlay page, stats JSON, remote with PIN (wrong PIN refused) - {stats[..Math.Min(140, stats.Length)]}...");
    var lan = WebServer.LanAddresses().FirstOrDefault();
    if (lan != null)
    {
        var fromLan = await http.GetAsync($"http://{lan}:8799/api/stats");
        Check(fromLan.StatusCode == System.Net.HttpStatusCode.Forbidden, "stats from the network need the PIN");
    }
    var shot = await http.GetAsync($"http://127.0.0.1:8799/api/screen?pin={WebServer.Pin}");
    var bytes = await shot.Content.ReadAsByteArrayAsync();
    Check(shot.IsSuccessStatusCode && bytes.Length > 1000 && bytes[0] == 0xFF && bytes[1] == 0xD8, $"live Switch screenshot through the remote ({bytes.Length / 1024} KB JPEG)");
    var act = await http.GetStringAsync($"http://127.0.0.1:8799/api/action?pin={WebServer.Pin}&do=pause");
    var pausedFlag = HuntService.BotDir != null && File.Exists(Path.Combine(HuntService.BotDir, "pause.flag"));
    await http.GetStringAsync($"http://127.0.0.1:8799/api/action?pin={WebServer.Pin}&do=resume");
    Check(pausedFlag && !HuntService.IsPaused, $"remote pause/resume ({act})");
    await Task.Delay(5500);
    Check(File.Exists(Path.Combine(WebServer.OverlayDir, "counter.txt")), "overlay text files written (" + string.Join(", ", Directory.GetFiles(WebServer.OverlayDir).Select(Path.GetFileName)) + ")");
    WebServer.Stop();

    // ---- hunt options -> bot settings (shinyforge.json) -> Python merges them
    var botCopy = Path.Combine(work, "botcopy"); Directory.CreateDirectory(botCopy);
    var h = s.Hunt;
    var keepHunt = System.Text.Json.JsonSerializer.Serialize(h);
    h.StopNatures = new() { "Jolly", "Adamant" }; h.MinIvs = new() { ["Spe"] = 31 }; h.AutoCatch = "non_legendary"; h.RngEnabled = false;
    HuntService.WriteBotOverrides(botCopy);
    var json = File.ReadAllText(Path.Combine(botCopy, "shinyforge.json"));
    Check(json.Contains("\"mode\": null") && json.Contains("Jolly") && json.Contains("\"Spe\": 31") && json.Contains("non_legendary"), "RNG stays OFF unless chosen; options written for the bot");
    h.RngEnabled = true;
    HuntService.WriteBotOverrides(botCopy);
    Check(File.ReadAllText(Path.Combine(botCopy, "shinyforge.json")).Contains("\"mode\": \"rng\""), "RNG mode only when the user turned it on");
    var pyRules = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("python", "-c \"import sys; sys.path.insert(0, r'" +
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), @"Downloads\Switch 1 Pokemon Bot") + "'); " +
        "from types import SimpleNamespace as N; from shinybot.extras import check_rules; " +
        "p=N(species_name='Deoxys', nature='Jolly', iv_total=100, ivs={'Spe': 31}); q=N(species_name='Deoxys', nature='Bold', iv_total=50, ivs={'Spe': 3}); " +
        "r={'natures':['Jolly'],'min_ivs':{'Spe':31}}; print(check_rules(p,r)); print(check_rules(q,r)); print(check_rules(p, dict(r, require_all=True, min_iv_total=150)))\"")
        { UseShellExecute = false, RedirectStandardOutput = true })!;
    var rulesOut = pyRules.StandardOutput.ReadToEnd().Trim().Split('\n').Select(x => x.Trim()).ToArray();
    Check(rulesOut.Length == 3 && rulesOut[0].StartsWith("matches") && rulesOut[1] == "None" && rulesOut[2] == "None", "stop rules: match / no match / all-required (" + string.Join(" | ", rulesOut) + ")");
    s.Hunt = System.Text.Json.JsonSerializer.Deserialize<HuntOptions>(keepHunt)!;

    // ---- shiny clip buffer
    ApplicationConfiguration.Initialize();
    var win = new Form { Text = "ShinyForgeClipTest", Size = new Size(320, 240), StartPosition = FormStartPosition.Manual, Location = new Point(60, 60), BackColor = Color.SeaGreen };
    win.Show(); Application.DoEvents();
    var clipOut = Path.Combine(work, "clips"); if (Directory.Exists(clipOut)) Directory.Delete(clipOut, true);
    var clips = new ShinyClipService();
    s.ShinyClipSeconds = 30;
    clips.Start(RecordingService.WindowPrefix + "ShinyForgeClipTest", RecordingService.NoAudio, "selftest", clipOut);
    for (int i = 0; i < 250; i++) { Application.DoEvents(); Thread.Sleep(100); }
    var saveTask = clips.SaveClipAsync("Deoxys");
    while (!saveTask.IsCompleted) { Application.DoEvents(); Thread.Sleep(100); }
    var clip = saveTask.Result;
    clips.Stop(); win.Close();
    Check(clip != null && new FileInfo(clip).Length > 10_000, $"shiny clip saved from the rolling buffer: {(clip == null ? "none" : Path.GetFileName(clip) + $" ({new FileInfo(clip).Length / 1024} KB)")}");
    if (clip != null)
    {
        var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(RecordingService.FindFfmpeg()!)!, "ffprobe.exe"),
            $"-v error -show_entries format=duration -of csv=p=0 \"{clip}\"") { UseShellExecute = false, RedirectStandardOutput = true });
        var dur = probe?.StandardOutput.ReadToEnd().Trim();
        Console.WriteLine($"   clip duration: {dur} s");
        Check(double.TryParse(dur, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 15, "clip is a playable video of the last seconds");
    }

    // ---- keep awake
    PowerGuard.BusyReason = () => "a test";
    PowerGuard.Start();
    Thread.Sleep(6500);
    Check(PowerGuard.HeldFor == "a test", "keep-awake engaged while busy");

    (s.DiscordWebhookUrl, s.NtfyTopic, s.NtfyServer, s.OverlayEnabled, s.RemoteEnabled, s.WebPort, s.AutoBackups) = keep;
}

if (tests.Contains("featuresui"))
{
    ApplicationConfiguration.Initialize();
    var outDir = Path.Combine(work, "screens"); Directory.CreateDirectory(outDir);
    void Snap(Form f, string name, int waitMs = 600)
    {
        f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000); f.ShowInTaskbar = false;
        f.Show(); for (int i = 0; i < waitMs / 50; i++) { Application.DoEvents(); Thread.Sleep(50); }
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(Path.Combine(outDir, name + ".png"));
        f.Close();
        Console.WriteLine("   rendered " + name);
    }
    Snap(new HuntOptionsForm(true), "hunt_options");
    Snap(new HuntStatsForm("Python"), "hunt_stats", 2000);
    Snap(new SettingsForm(), "settings");
    Snap(new BackupsForm(), "backups");
    Snap(new LivingDexForm(), "living_dex", 40000);
    Snap(new GamePage(GameCatalog.Get("sw_frlg_lg")) is var gp ? new Form { Size = new Size(1320, 1200), Controls = { gp } } : null!, "game_page_frlg", 1500);
}

if (tests.Contains("healthui"))
{
    ApplicationConfiguration.Initialize();
    var outDir = Path.Combine(work, "screens");
    Directory.CreateDirectory(outDir);
    void Snap(Form f, string name)
    {
        f.StartPosition = FormStartPosition.Manual; f.Location = new Point(-4000, -4000); f.ShowInTaskbar = false;
        f.Show(); for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(Path.Combine(outDir, name + ".png"));
        f.Close();
        Console.WriteLine("   rendered " + name);
    }
    var ctor = typeof(IpPrompt).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)[0];
    Snap((Form)ctor.Invoke(new object?[] { "Switch", "192.168.0.16", "The Switch doesn't answer at 192.168.0.16:6000. Its IP address may have changed (or it's asleep / sys-botbase isn't running).",
        "On the Switch: System Settings → Internet → Connection Status shows the IP address.\nThe Switch must be on the same network as this PC, with sys-botbase running." }), "ip_switch");
    HealthMonitor.Report("Python hunter", HealthState.Warning, "Bot: The same Deoxys (PID 50CA8977) came back 2x in a row - the game did not reset. Fixing: checking the reset and retrying it with a longer hold.");
    HealthMonitor.Report("Recording (LeafGreen (Switch))", HealthState.Error, "Recording had stopped unexpectedly (ffmpeg exited) - restarted automatically (attempt 1).");
    Snap(new HealthForm(), "health_form");
    var main = new MainForm();
    main.StartPosition = FormStartPosition.Manual; main.Location = new Point(-4000, -4000);
    main.Show(); for (int i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(50); }
    main.GetType().GetMethod("OnHealthProblem", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
        .Invoke(main, new object[] { HealthMonitor.Items[0] });
    main.GetType().GetMethod("UpdateHealthLabel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(main, null);
    for (int i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(50); }
    using (var bmp = new Bitmap(main.Width, main.Height)) { main.DrawToBitmap(bmp, new Rectangle(Point.Empty, main.Size)); bmp.Save(Path.Combine(outDir, "main_health.png")); }
    main.Close();
    Console.WriteLine("   rendered main_health");
}

if (tests.Contains("render"))
{
    ApplicationConfiguration.Initialize();
    var outDir = Path.Combine(work, "screens");
    Directory.CreateDirectory(outDir);
    void Shot(Control c, string name, Size size)
    {
        var host = new Form { Size = size, StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        c.Dock = DockStyle.Fill;
        host.Controls.Add(c);
        host.Show();
        Application.DoEvents();
        Thread.Sleep(300);
        Application.DoEvents();
        using var bmp = new Bitmap(host.ClientSize.Width, host.ClientSize.Height);
        host.DrawToBitmap(bmp, new Rectangle(Point.Empty, host.ClientSize));
        bmp.Save(Path.Combine(outDir, name + ".png"));
        host.Close();
        Console.WriteLine("   rendered " + name);
    }
    Shot(new PlatformPage(), "1_platform", new Size(1320, 760));
    Shot(new GamesPage(false), "2_games_3ds", new Size(1320, 1500));
    Shot(new GamesPage(true), "3_games_switch", new Size(1320, 900));
    Shot(new GamePage(GameCatalog.Get("sw_frlg_lg")), "4_game_leafgreen_switch", new Size(1320, 1120));
    Shot(new GamePage(GameCatalog.Get("3ds_x")), "5_game_x", new Size(1320, 1120));
    Shot(new GamePage(GameCatalog.Get("3ds_umoon")), "6_game_ultramoon", new Size(1320, 1220));
    Shot(new GamePage(GameCatalog.Get("nds_heartgold")), "7_game_heartgold", new Size(1320, 1220));
    var main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000) };
    main.Show(); Application.DoEvents(); Thread.Sleep(500); Application.DoEvents();
    using (var bmp = new Bitmap(main.Width, main.Height)) { main.DrawToBitmap(bmp, new Rectangle(Point.Empty, main.Size)); bmp.Save(Path.Combine(outDir, "0_mainform.png")); }
    main.Close();
    Console.WriteLine("   rendered 0_mainform");
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures;
