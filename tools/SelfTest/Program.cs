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
var uiReady = false;
void InitUi() { if (uiReady) return; ApplicationConfiguration.Initialize(); uiReady = true; } // WinForms: once per process

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
        var boxed = pk.Clone(); boxed.Species = 25; boxed.SetPIDGender(0); boxed.SetShiny(); boxed.RefreshChecksum(); // gender first: it rerolls the PID
        sav.SetBoxSlotAtIndex(boxed, 0, 3);
        var path = Path.Combine(folder, "nested", $"{id}.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, sav.Write().ToArray());

        var found = SaveService.FindSaves(folder, game);
        // PKHeX doesn't recognise every freshly made BLANK save once it's written to disk (Scarlet, X, Sword): the sub-folder
        // search is then checked with real saves in the "checkpoint" test, and the rest uses the save in memory.
        if (SaveUtil.TryGetSaveFile(path, out _)) Check(found.Count == 1, $"{id}: save found in a sub-folder ({found.Count})");
        else Console.WriteLine($"   {id}: PKHeX doesn't detect a blank save file of this game - sub-folder search covered by the checkpoint test");
        var save = found.Count > 0 ? found[0] : new FoundSave(path, sav);
        var (outDir, count) = SaveService.ExportPokemon(save.Save, game, folder);
        var files = Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories).ToList();
        Check(count == 2 && files.Count == 2, $"{id}: exported {count} Pokémon -> {Path.GetFileName(outDir)} ({string.Join(", ", files.Select(Path.GetFileName))})");
        var reread = EntityFormat.GetFromBytes(File.ReadAllBytes(files[0]));
        Check(reread != null && reread.Species != 0, $"{id}: exported file re-opens with PKHeX ({reread?.GetType().Name} species {reread?.Species})");
        var info = SaveService.WriteGameInfo(save.Save, game, save.Path, folder);
        var text = File.ReadAllText(info);
        Check(text.Contains("Dom") && text.Contains(save.Save.DisplayTID.ToString()), $"{id}: game info.txt written (TID {save.Save.DisplayTID})");
        if (id == "3ds_x") Console.WriteLine(text);
        var col = Collection.FromSave(save.Save, path);
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

if (tests.Contains("sheet7"))
{
    // Gen 7 trackers: a copy of each is updated through Excel (the Let's Go ones with a small hand-made collection).
    foreach (var id in new[] { "sw_lgp", "3ds_usun" })
    {
        var src = SpreadsheetService.FindWorkbook(GameCatalog.Get(id), AppSettings.Current.SpreadsheetRoot)!;
        Check(src != null, $"{id} tracker located: {src}");
        if (src == null) continue;
        var copy = Path.Combine(work, Path.GetFileNameWithoutExtension(src) + " (TEST COPY).xlsm");
        File.Copy(src, copy, true);
        var col = new Collection();
        foreach (var n in new[] { "Bulbasaur", "Pikachu", "Mewtwo" })
            col.BySpecies[Collection.Normalize(n)] = new Collection.Entry { Name = n, Owned = true, OwnOT = true, Shiny = n == "Pikachu" };
        var t7 = new Thread(() =>
        {
            var r = SpreadsheetService.Update(copy, col, s => Console.WriteLine("   " + s));
            Console.WriteLine($"   result: inBox+{r.MarkedInBox} OT+{r.MarkedOT} shiny+{r.MarkedShiny} sheets={r.Sheets}");
            Check(r.MarkedInBox + r.MarkedOT + r.MarkedShiny > 0 || r.Sheets > 0, $"{id} tracker copy updated through Excel");
        });
        t7.SetApartmentState(ApartmentState.STA); t7.Start(); t7.Join();
        try { File.Delete(copy); } catch { }
    }
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

if (tests.Contains("frames"))
{
    // every console frame: the screens are replaced by solid red test inputs; the red must fill each hole exactly
    var ff = RecordingService.FindFfmpeg()!;
    var dir = Path.Combine(work, "frames");
    Directory.CreateDirectory(dir);
    var cases = new (Platform P, string Source, string Overlay)[]
    {
        (Platform.GB, RecordingService.Top3DS, "gb"), (Platform.GBC, RecordingService.Top3DS, "gbc"), (Platform.GBA, RecordingService.Top3DS, "gba"),
        (Platform.NDS, RecordingService.Both3DS, "nds"), (Platform.N3DS, RecordingService.Both3DS, "3ds"),
        (Platform.Switch, RecordingService.DevicePrefix + "Test card", "switch1"), (Platform.Switch, RecordingService.DevicePrefix + "Test card", "switch2"),
        (Platform.N3DS, RecordingService.Both3DS, ""), (Platform.NDS, RecordingService.Both3DS, ""),
    };
    foreach (var (p, source, overlay) in cases)
        foreach (var pattern in new[] { "color=c=red", "testsrc2=rate=30" })
        {
            var cmd = RecordingService.BuildCapture(new RecordingService.CaptureSpec(source, RecordingService.NoAudio, p, overlay), false);
            var args2 = System.Text.RegularExpressions.Regex.Replace(cmd.Args, "-f gdigrab .*?-i title=\"3DS Capture - (Top|Bottom)\"",
                m => $"-f lavfi -i {pattern}:s={(m.Groups[1].Value == "Top" ? "400x240" : "320x240")}:r=30");
            args2 = System.Text.RegularExpressions.Regex.Replace(args2, "-f dshow .*?-i video=\"[^\"]+\"", $"-f lavfi -i {pattern}:s=1920x1080:r=30");
            var name = $"{p}_{(overlay == "" ? "noframe" : overlay)}_{(pattern.StartsWith("color") ? "red" : "pattern")}.png";
            var outPng = Path.Combine(dir, name);
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -loglevel error -y {args2} -frames:v 1 \"{outPng}\"")
            { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true })!;
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (!File.Exists(outPng)) { Check(false, $"{name}: ffmpeg failed: {err.Trim()}"); continue; }
            if (!pattern.StartsWith("color") || overlay == "") { if (overlay == "") Check(true, $"{name} rendered ({cmd.Description})"); continue; }
            var frame = ConsoleFrames.Get(overlay)!;
            using var bmp = new System.Drawing.Bitmap(outPng);
            double sx = (double)bmp.Width / frame.Width, sy = (double)bmp.Height / frame.Height;
            bool Red(int x, int y) { var c = bmp.GetPixel(x, y); return c.R > 180 && c.G < 90 && c.B < 90; }
            int expected = RecordingService.HasTwoScreens(p) ? 2 : 1;
            foreach (var r in frame.Screens.Take(expected))
            {
                // inside (2 px in, away from the rounded corners): all red; 3 px outside the hole: the frame, not red
                int x0 = (int)Math.Ceiling(r.X * sx) + 2, y0 = (int)Math.Ceiling(r.Y * sy) + 2, x1 = (int)Math.Floor((r.X + r.Width) * sx) - 3, y1 = (int)Math.Floor((r.Y + r.Height) * sy) - 3;
                int bad = 0, total = 0;
                for (int x = x0 + 6; x <= x1 - 6; x += 3) { total += 2; if (!Red(x, y0)) bad++; if (!Red(x, y1)) bad++; }
                for (int y = y0 + 6; y <= y1 - 6; y += 3) { total += 2; if (!Red(x0, y)) bad++; if (!Red(x1, y)) bad++; }
                int leak = 0, ring = 0;
                for (int x = x0 + 6; x <= x1 - 6; x += 3) { ring += 2; if (Red(x, y0 - 5)) leak++; if (Red(x, y1 + 5)) leak++; }
                for (int y = y0 + 6; y <= y1 - 6; y += 3) { ring += 2; if (Red(x0 - 5, y)) leak++; if (Red(x1 + 5, y)) leak++; }
                Check(bad == 0 && leak == 0, $"{name} ({bmp.Width}x{bmp.Height}) {r.Name} screen fills its hole: {total - bad}/{total} edge pixels red, {leak}/{ring} outside");
            }
        }
    Console.WriteLine("   pictures: " + dir);
}

if (tests.Contains("streamlabs"))
{
    // the running Streamlabs Desktop: set up the console scenes (existing ones are updated)
    Check(StreamlabsService.IsOpen, "Streamlabs Desktop is open");
    if (StreamlabsService.IsOpen)
    {
        using var sl = StreamlabsService.Connect();
        var before = sl.Call("ScenesService", "getSceneNames")!.AsArray().Count;
        var report = sl.SetUpConsoleScenes(null, null);
        foreach (var r in report) Console.WriteLine("   " + r);
        var after = sl.Call("ScenesService", "getSceneNames")!.AsArray().Select(n => (string)n!).ToList();
        Check(after.Count == before, $"no duplicate scenes ({before} before, {after.Count} after)");
        foreach (var name in new[] { "Game Boy", "Game Boy Color", "Game Boy Advance", "Nintendo DS", "3DS", "Switch 1" })
            Check(after.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)), $"scene {name}");
        Check(after.Any(a => a.Equals("switch 2", StringComparison.OrdinalIgnoreCase)), "scene Switch 2");
    }
}

if (tests.Contains("framesnow"))
{
    // the real 3DS Capture windows, through the real capture command, one picture per console frame
    var ff = RecordingService.FindFfmpeg()!;
    var dir = Path.Combine(work, "framesnow");
    Directory.CreateDirectory(dir);
    foreach (var (p, overlay) in new[] { (Platform.GB, "gb"), (Platform.GBC, "gbc"), (Platform.GBA, "gba"), (Platform.NDS, "nds"), (Platform.N3DS, "3ds"), (Platform.N3DS, "") })
    {
        var src = RecordingService.HasTwoScreens(p) ? RecordingService.Both3DS : RecordingService.Top3DS;
        var cmd = RecordingService.BuildCapture(new RecordingService.CaptureSpec(src, RecordingService.NoAudio, p, overlay), false);
        var outPng = Path.Combine(dir, $"{p}_{(overlay == "" ? "noframe" : overlay)}.png");
        using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -loglevel error -y {cmd.Args} -frames:v 1 \"{outPng}\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true })!;
        var err = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        Check(File.Exists(outPng), $"{Path.GetFileName(outPng)} ({cmd.Description}) {err.Trim()}");
    }
    Console.WriteLine("   pictures: " + dir);
}

if (tests.Contains("appaudio"))
{
    // one program's sound (here: ffplay playing a quiet tone for 4 s) captured through the pipe into FFmpeg
    var ff = RecordingService.FindFfmpeg()!;
    var bin = Path.GetDirectoryName(ff)!;
    var wav = Path.Combine(work, "appaudio.wav");
    using var player = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(bin, "ffplay.exe"),
        "-hide_banner -loglevel error -nodisp -autoexit -volume 15 -f lavfi -i sine=frequency=440:duration=5") { UseShellExecute = false, CreateNoWindow = true })!;
    Thread.Sleep(500);
    using var cap = new ProcessAudioCapture("ffplay");
    cap.Start();
    using var rec = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -loglevel error -y {cap.FfmpegInput} -t 4 \"{wav}\"")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
    rec.WaitForExit(15000);
    cap.Dispose();
    Console.WriteLine("   capture error: " + (cap.Error ?? "none"));
    var vd = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ff, $"-hide_banner -i \"{wav}\" -af volumedetect -f null -")
    { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true })!;
    var text = vd.StandardError.ReadToEnd();
    vd.WaitForExit();
    var max = System.Text.RegularExpressions.Regex.Match(text, @"max_volume: (-?[\d.]+) dB");
    Console.WriteLine("   " + string.Join(" ", text.Split('\n').Where(l => l.Contains("_volume")).Select(l => l.Trim()[(l.Trim().IndexOf(']') + 1)..].Trim())));
    Check(cap.Error == null && max.Success && double.Parse(max.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > -50, "the program's sound was captured");
}

if (tests.Contains("frameslive"))
{
    // real 3DS Capture windows + 3DS Capture's sound, 3DS frame, 8 seconds
    var rec = new RecordingService();
    var dir = Path.Combine(work, "frameslive");
    if (Directory.Exists(dir)) Directory.Delete(dir, true);
    rec.Status += s => Console.WriteLine("   rec: " + s);
    rec.Start(new RecordingService.CaptureSpec(RecordingService.Both3DS, RecordingService.AppAudio3DS, Platform.N3DS, "3ds"), dir, 1, 60);
    Thread.Sleep(8000);
    rec.Stop();
    var file = Directory.EnumerateFiles(dir, "Recording_*.mp4").FirstOrDefault();
    Check(file != null, "3DS recording written: " + file);
    if (file != null)
    {
        var probe = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.Combine(Path.GetDirectoryName(RecordingService.FindFfmpeg()!)!, "ffprobe.exe"),
            $"-v error -show_entries stream=codec_type,width,height,duration -of csv=p=0 \"{file}\"") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!;
        var info = probe.StandardOutput.ReadToEnd().Trim();
        probe.WaitForExit();
        Console.WriteLine("   streams: " + info.Replace("\n", " | "));
        Check(info.Contains("video") && info.Contains("audio"), "recording has the stacked picture and 3DS Capture's sound");
    }
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
    await DependencyService.ReadVersions();
    foreach (var d in DependencyService.All) Console.WriteLine($"   {d.Name,-20} {d.Version ?? "-",-18} {d.State,-16} {d.Detail}");
    Check(DependencyService.All.Where(d => d.Path != null && d.Name != "Microsoft Excel").All(d => d.Version != null),
        "every installed tool's version was read: " + string.Join(", ", DependencyService.All.Where(d => d.Path != null && d.Version == null).Select(d => d.Name)));
    foreach (var u in updates) Console.WriteLine($"   UPDATE {u.Item}: {u.Message}");
    Check(DependencyService.All.First(d => d.Name == "FFmpeg").State != DepState.Missing, "FFmpeg detected");
    // version texts as the tools / GitHub really write them
    Check(DependencyService.ParseVersion("v0.71.6-beta") == new Version(0, 71, 6) && DependencyService.ParseVersion("3.32/3.33") == new Version(3, 33)
          && DependencyService.ParseVersion("esptool v5.6.0") == new Version(5, 6, 0) && DependencyService.ParseVersion("v2.5") == new Version(2, 5),
        "version texts parsed (v0.71.6-beta, 3.32/3.33, esptool v5.6.0, v2.5)");
    Check(DependencyService.IsNewer("v5.6.0", "5.5.1") && !DependencyService.IsNewer("v2.5", "2.5") && !DependencyService.IsNewer("v0.71.6-beta", "0.71.10")
          && !DependencyService.IsNewer("2.7.1", null), "newer / same / older / unknown compared correctly");
    Check(PokemonAutomation.VersionOf(@"C:\x\PA-SerialPrograms-Windows-x64-0.71.6-20261029\SerialPrograms-Windows.cmd") == new Version(0, 71, 6)
          && PokemonAutomation.VersionOf(@"C:\x\PA-SerialPrograms-Windows-x64-0.70.2-20260901\inner\SerialPrograms-Windows.cmd") == new Version(0, 70, 2),
        "Pokémon Automation's version read from its folder name");
    // an outdated copy must be reported: a fake old Pokémon Automation folder, set in memory only (settings aren't saved)
    var fake = Path.Combine(work, "PA-SerialPrograms-Windows-x64-0.60.0-20250101", "SerialPrograms-Windows.cmd");
    Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
    File.WriteAllText(fake, "rem test");
    var keep = AppSettings.Current.PokemonAutomationPath;
    AppSettings.Current.PokemonAutomationPath = fake;
    try
    {
        var old = await DependencyService.CheckUpdates();
        var pa = old.FirstOrDefault(u => u.Item == "Pokémon Automation");
        Check(pa != null && pa.Message.Contains("0.60.0") && pa.Apply != null, $"an outdated Pokémon Automation is reported ({pa?.Message})");
    }
    finally { AppSettings.Current.PokemonAutomationPath = keep; }

    // SysBot must be current before any hunt or tool: the real copy (2.7.1) is current; a fake older self-built copy isn't
    Check(await DependencyService.SysBotOutdated(fresh: true) == null, "the installed SysBot.exe is the latest release");
    var oldBot = Path.Combine(work, "Sys-EncounterBot.NET-2.6.0", "SysBot.Pokemon.WinForms", "bin", "Release", "SysBot.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(oldBot)!);
    File.WriteAllText(oldBot, "test");
    var keepBot = AppSettings.Current.SysBotPath;
    AppSettings.Current.SysBotPath = oldBot;
    try
    {
        var o = await DependencyService.SysBotOutdated(fresh: true);
        Check(o is { Installed: "2.6.0", SelfBuilt: true }, $"an outdated self-built SysBot is caught ({o})");
        Check(await DependencyService.SysBotOutdated() == o, "the answer is reused (no GitHub call per click)");
    }
    finally { AppSettings.Current.SysBotPath = keepBot; DependencyService.ForgetSysBotCheck(); }

    // the equipment checks done before outside programs open, on this PC's real hardware
    Console.WriteLine($"   3DS capture board: {Equipment.ThreeDSCaptureBoard()}, microcontroller: {Equipment.Microcontroller()}, " +
                      $"video sources: {string.Join(" | ", Equipment.VideoSources())}");
    Check(Equipment.VideoSources().Count > 0, "capture devices are listed (the recording / Pokémon Automation check)");
    Check(WinUsbDevice.IsVendorConnected(0x534D) && !WinUsbDevice.IsConnected(0x534D, 0x0001), "USB detection: the MS2109 capture card (534D:2109) is seen, a made-up product isn't");
    if (DependencyService.PathOf("Python 3") is { } py)
    {
        var missing = await DependencyService.MissingPythonPackages(py);
        Check(missing != null, $"the Python hunter's packages were checked (missing: {(missing is { Count: > 0 } ? string.Join(", ", missing) : "none")})");
    }
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

if (tests.Contains("3dsvc"))
{
    // ---- Simulated 3DS running Pokémon Crystal from the Virtual Console: the Game Boy RAM sits at an odd address inside a
    // 2 MB region of an unnamed process; Luma's getmemregions lists the regions. The bot must find the RAM, read the trainer,
    // soft reset (A+B+Start+Select) through encounters and stop on the one that is shiny by the Gen 2 DV rule.
    var map = ShinyForge.Services.ThreeDS.GbMap.Crystal;
    const uint regionStart = 0x08000000, wramAt = 0x08123457; // not aligned on purpose
    var region = new byte[0x200000];
    var decoy = new byte[0x40000];
    void PutAt(uint wram, uint gbAddr, params byte[] b) => b.CopyTo(region, (int)(wram - regionStart) + gbAddr - 0xC000);
    void Put(uint gbAddr, params byte[] b) => PutAt(wramAt, gbAddr, b);
    void Party(uint wram)
    {
        PutAt(wram, map.PartyCount, 2, 155, 158, 0xFF);                                          // Cyndaquil, Totodile
        PutAt(wram, map.PartyMons, 155); PutAt(wram, (ushort)(map.PartyMons + 0x1F), 14);       // species + level 14
        PutAt(wram, (ushort)(map.PartyMons + 0x30), 158); PutAt(wram, (ushort)(map.PartyMons + 0x30 + 0x1F), 9);
    }
    // a decoy found on real hardware: a party that checks out but no trainer name behind it (must be skipped)
    Party(0x08010001);
    PutAt(0x08010001, map.PlayerName, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08);
    // the game starts CLOSED: its RAM appears when the bot presses A on the HOME Menu (then the bot goes through the title screen)
    var vcOpen = false; var vcSettled = false;
    void OpenGame()
    {
        Party(wramAt);
        Put(map.PlayerId, 0x30, 0x39);                              // ID 12345
        Put(map.PlayerName, 0x83, 0x8E, 0x8C, 0x50);                // "DOM"
        vcOpen = true;
    }
    decoy[0x100] = 3; decoy[0x101] = 1; decoy[0x102] = 0xFF;    // a broken party-like pattern (count 3, but 0xFF too early)
    // encounters: Suicune (DVs 0x5A 0x3C), Raikou, Entei with DVs Atk 10 Def 10 / Spe 10 Spc 10 = shiny
    var wild = new (byte Species, byte Level, byte Dv0, byte Dv1)[] { (245, 40, 0x5A, 0x3C), (243, 40, 0x7B, 0x21), (244, 40, 0xAA, 0xAA) };
    int nextVc = 0, aVc = 0, resetsVc = 0;
    var udpVc = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 4950));
    var ctsVc = new CancellationTokenSource();
    uint lastHidVc = 0xFFF;
    _ = Task.Run(() =>
    {
        var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
        while (!ctsVc.IsCancellationRequested)
        {
            byte[] p;
            try { p = udpVc.Receive(ref ep); } catch { break; }
            if (p.Length != 20) continue;
            var hid = BitConverter.ToUInt32(p, 0);
            var pressed = ~hid & lastHidVc & 0xFFF;
            lastHidVc = hid;
            lock (region)
            {
                if (!vcOpen) { if ((pressed & 1) != 0) OpenGame(); continue; }                                    // HOME Menu: A opens the game
                if ((~hid & 0x00F) == 0x00F && (pressed & 0x00F) != 0) { Put(map.BattleFlag, 0); resetsVc++; aVc = 0; } // A+B+Start+Select
                else if ((pressed & 1) != 0 && vcSettled && region[(int)(wramAt - regionStart) + map.BattleFlag - 0xC000] == 0 && ++aVc >= 3 && nextVc < wild.Length)
                {
                    var w = wild[nextVc++];
                    Put(map.Enemy, w.Species);
                    Put(map.EnemyDVs, w.Dv0, w.Dv1);
                    Put(map.EnemyLevel, w.Level);
                    Put(map.BattleFlag, 1);
                }
            }
        }
    });
    var listenerVc = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 4000);
    listenerVc.Start();
    var regionText = $"00100000 - 00300000 R-X Code\n0ff00000 - 10000000 RW- Private\n{regionStart:x8} - {regionStart + region.Length:x8} RW- Private\n14000000 - {0x14000000 + decoy.Length:x8} RW- Continuous\n";
    _ = Task.Run(() =>
    {
        using var client = listenerVc.AcceptTcpClient();
        client.NoDelay = true; // otherwise delayed ACKs make the full-memory search take minutes
        var s = client.GetStream();
        void Send(string payload)
        {
            var sum = (payload.Sum(c => (int)c) & 0xFF).ToString("x2");
            var b = System.Text.Encoding.ASCII.GetBytes($"${payload}#{sum}");
            s.Write(b, 0, b.Length);
        }
        while (!ctsVc.IsCancellationRequested)
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
                Send("l<?xml version=\"1.0\"?><osdata type=\"processes\">" +
                     "<item><column name=\"pid\">0x10</column><column name=\"command\">pm</column></item>" +
                     "<item><column name=\"pid\">0x20</column><column name=\"command\">menu</column></item>" +
                     "<item><column name=\"pid\">0x2c</column><column name=\"command\">QBRA</column></item></osdata>");
            else if (cmd.StartsWith("qRcmd,")) Send(Convert.ToHexString(System.Text.Encoding.ASCII.GetBytes(regionText)).ToLowerInvariant());
            else if (cmd.StartsWith("vAttach")) { lock (region) if (vcOpen && !vcSettled) { vcSettled = true; aVc = 0; } Send("T05"); } // title-screen A presses don't count
            else if (cmd.StartsWith("vCont")) { }
            else if (cmd.StartsWith("D")) Send("OK");
            else if (cmd.StartsWith("m"))
            {
                var parts = cmd[1..].Split(',');
                uint addr = Convert.ToUInt32(parts[0], 16); int len = Convert.ToInt32(parts[1], 16);
                var outb = new byte[len];
                lock (region)
                    for (int i = 0; i < len; i++)
                    {
                        long a = addr + i;
                        if (a >= regionStart && a < regionStart + region.Length) outb[i] = region[a - regionStart];
                        else if (a >= 0x14000000 && a < 0x14000000 + decoy.Length) outb[i] = decoy[a - 0x14000000];
                    }
                Send(Convert.ToHexString(outb).ToLowerInvariant());
            }
            else Send("");
        }
    });

    Check(ShinyForge.Services.ThreeDS.VirtualConsoleGb.Make(1, 0x83, 70, 0xAA, 0xAA) is { Species: 150, IsShiny: true },
        "Gen 1: internal species 0x83 = Mewtwo, DVs 10/10/10/10 = shiny");
    Check(ShinyForge.Services.ThreeDS.VirtualConsoleGb.Make(2, 249, 40, 0x9A, 0xAA) is { IsShiny: false } &&
          ShinyForge.Services.ThreeDS.VirtualConsoleGb.Make(2, 249, 40, 0xFA, 0xAA) is { IsShiny: true },
        "Gen 2 DV rule: Attack 9 not shiny, Attack 15 shiny (Def/Spe/Spc 10)");
    // the transfer rule agrees with PKHeX for every Pokémon in real Gen 1 / Gen 2 saves (copies are only read)
    var bakDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PKHeX (26.10.18)", "bak");
    int checkedMons = 0, disagree = 0;
    foreach (var f in Directory.Exists(bakDir) ? Directory.GetFiles(bakDir) : Array.Empty<string>())
        if (SaveUtil.TryGetSaveFile(f, out var gbSav) && gbSav is { Generation: <= 2 })
            foreach (var pk in gbSav.BoxData.Concat(gbSav.PartyData).Where(p => p.Species != 0))
            {
                checkedMons++;
                if (ShinyForge.Services.TransferShiny.IsShinyDV(pk.IV_ATK, pk.IV_DEF, pk.IV_SPE, pk.IV_SPA) != pk.IsShiny) disagree++;
            }
    Check(checkedMons > 0 && disagree == 0, $"transfer shiny rule = PKHeX's for {checkedMons} Pokémon in real Gen 1/2 saves");
    var pk3 = new PK3 { Species = 25, TID16 = 0, SID16 = 0 };
    pk3.PID = 0x00090000; // shiny value 9
    var pk3b = new PK3 { Species = 25 }; pk3b.PID = 0x00030000; // shiny value 3
    Check(ShinyForge.Services.TransferShiny.Line(pk3)!.Contains("Shiny value 9") && ShinyForge.Services.TransferShiny.Line(pk3b)!.StartsWith("★ Shiny in every later game") &&
          ShinyForge.Services.TransferShiny.Line(VirtualConsoleShiny())!.Contains("shiny when moved to Pokémon Bank"),
        "transfer lines: Gen 3 value 9 (stays normal), value 3 (shiny everywhere), Gen 1 DVs 10/10/10/10 (shiny in Bank)");
    static PKM VirtualConsoleShiny() => ShinyForge.Services.ThreeDS.VirtualConsoleGb.Make(1, 0x83, 70, 0xAA, 0xAA)!;
    var logVc = Path.Combine(work, "3ds_vc_encounters.csv");
    if (File.Exists(logVc)) File.Delete(logVc);
    var botVc = new ShinyForge.Services.ThreeDS.Bot3DS(new ShinyForge.Services.ThreeDS.Bot3DSOptions
    {
        Host = "127.0.0.1", Mode = ShinyForge.Services.ThreeDS.Hunt3DSMode.StaticSoftReset, ResetWaitMs = 500, OpenGameWaitMs = 500, LogFile = logVc,
    });
    var shinyVc = false;
    botVc.Log += m => Console.WriteLine("   3DS: " + m.Replace("\n", "\n        "));
    botVc.Encounter += (pk, stop) => shinyVc |= stop && pk.IsShiny;
    var gameVc = botVc.Connect("gbc_crystal");
    Check(gameVc.ShinyForgeId == "gbc_crystal" && botVc.Trainer is { Name: "DOM", TID: 12345 },
        $"Virtual Console: {gameVc.Name} opened from the HOME Menu, found by its Game Boy RAM (decoy without a name skipped); trainer {botVc.Trainer.Name} {botVc.Trainer.TID}");
    Check(ShinyForge.Services.ThreeDS.VirtualConsoleGb.Candidates(new[] { new ShinyForge.Services.ThreeDS.GdbProcess(43, "trl", ""),
            new ShinyForge.Services.ThreeDS.GdbProcess(45, "ro", "") }).Select(p => p.Name).SequenceEqual(new[] { "trl" }),
        "the on-demand system module 'ro' (newer than the game) is never taken for the game");
    var runVc = Task.Run(() => botVc.Run(new CancellationTokenSource(TimeSpan.FromMinutes(2)).Token));
    runVc.Wait(TimeSpan.FromMinutes(2.5));
    botVc.Close();
    ctsVc.Cancel(); udpVc.Dispose(); listenerVc.Stop();
    var rowsVc = File.Exists(logVc) ? File.ReadAllLines(logVc) : Array.Empty<string>();
    Check(shinyVc && nextVc == 3 && resetsVc >= 2, $"soft resets through {nextVc} encounters ({resetsVc} resets), stopped on the shiny Entei");
    Check(rowsVc.Length == 4 && rowsVc[3].Contains("Entei") && rowsVc[3].Contains("True"),
        $"encounter log: {string.Join(" | ", rowsVc.Skip(1).Select(r => string.Join(",", r.Split(',').Skip(3).Take(2))))}");
}

if (tests.Contains("sheets"))
{
    // Every game must auto-recognize its tracker (Gen 7 / Let's Go included since build 4).
    var root = AppSettings.Current.SpreadsheetRoot;
    foreach (var g in GameCatalog.All)
    {
        var wb = SpreadsheetService.FindWorkbook(g, root);
        var expectMissing = string.IsNullOrWhiteSpace(g.Spreadsheet);
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
    InitUi();
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
    Check(rel.VersionText == "9.0.500" && rel.IsNewer && rel.ZipUrl!.EndsWith("portable.zip") && rel.ZipSize == 157000000,
        $"release parsed: {rel.VersionText}, newer={rel.IsNewer}, program zip found");
    // 1.X.Z: a big update (X) is newer than any patch of the one before
    Check(ShinyForge.Core.AppInfo.ParseTag("v1.1.0") > ShinyForge.Core.AppInfo.ParseTag("v1.0.99"), "1.1.0 is newer than 1.0.99");
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

if (tests.Contains("pokeldn"))
{
    // Serial framing: CRC-32/ISO-HDLC check value, COBS round trips, and byte-for-byte equality with pokeldn's own Python code.
    Check(PokeldnService.Crc32("123456789"u8) == 0xCBF43926, "CRC-32 check value");
    var rnd = new Random(7);
    var okRound = true;
    foreach (var len in new[] { 0, 1, 253, 254, 255, 600 })
    {
        var data = new byte[len];
        rnd.NextBytes(data);
        if (len > 10) { data[3] = 0; data[len - 1] = 0; }
        okRound &= PokeldnService.CobsDecode(PokeldnService.CobsEncode(data))!.SequenceEqual(data);
        okRound &= !PokeldnService.CobsEncode(data).Contains((byte)0);
    }
    Check(okRound, "COBS encode/decode round trip (0-600 bytes, zeros, 254-byte runs)");
    var info = new byte[14].Concat(System.Text.Encoding.ASCII.GetBytes("pokeldn-radio esp32s3 version=1.11.0")).ToArray();
    info[0] = 1;
    var big = Enumerable.Range(0, 600).Select(i => (byte)(i % 7 == 0 ? 0 : i)).ToArray();
    var root = Path.Combine(Environment.CurrentDirectory, "..", "..", "external", "pokeldn");
    var repo = new[] { root, @"C:\Users\Dominic Sanna\Desktop\Updated app\ShinyForge\Source\external\pokeldn" }.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists);
    var py = DependencyService.FindPython();
    if (repo != null && py != null)
    {
        var script = Path.Combine(work, "pokeldn_frames.py");
        File.WriteAllText(script, $$"""
            import sys; sys.path.insert(0, r'{{repo}}')
            from pokeldn.ldn import esp32
            print(esp32.encode_frame(1).hex())
            print(esp32.encode_frame(0x81, bytes.fromhex('{{Convert.ToHexString(info)}}')).hex())
            print(esp32.encode_frame(0x08, bytes.fromhex('{{Convert.ToHexString(big)}}')).hex())
            """);
        var psi = new System.Diagnostics.ProcessStartInfo(py, $"-I \"{script}\"") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        p.WaitForExit();
        Check(lines.Length == 3, $"pokeldn's Python framing ran ({lines.Length} frames)");
        if (lines.Length == 3)
        {
            Check(Convert.ToHexString(PokeldnService.Frame(0x01, Array.Empty<byte>())).Equals(lines[0], StringComparison.OrdinalIgnoreCase), $"HELLO frame identical to pokeldn's ({lines[0]})");
            Check(Convert.ToHexString(PokeldnService.Frame(0x08, big)).Equals(lines[2], StringComparison.OrdinalIgnoreCase), "600-byte frame with zeros identical to pokeldn's");
            var fromPy = Convert.FromHexString(lines[1]);
            Check(PokeldnService.TryDecode(fromPy[..^1], out var type, out var payload) && type == 0x81 && payload.SequenceEqual(info), "INFO frame built by pokeldn decoded");
            var text = System.Text.Encoding.UTF8.GetString(payload, 14, payload.Length - 14);
            var fi = new FirmwareInfo(true, PokeldnService.ParseVersion(text), text, null);
            Check(fi.Version == "1.11.0" && fi.Target == "esp32s3", $"firmware version {fi.Version} and chip {fi.Target} read from INFO");
        }
    }
    else Console.WriteLine("   (pokeldn source or Python not found - Python comparison skipped)");
    var bad = PokeldnService.Frame(0x01, Array.Empty<byte>());
    bad[2] ^= 0x40;
    Check(!PokeldnService.TryDecode(bad[..^1], out _, out _), "a corrupted frame is rejected (CRC)");

    // Checksums file and firmware image version
    var sums = PokeldnService.ParseSums("8ddc0e01c6a66bd5f111d74793c5869207ea2aa7e923ac3735c9e38b7f3c49c1  pokeldn-windows-x64.zip\nc581a4e8f42f34e38e61a9636df56352c7db48ac274297a9c9aa4dfae0cc8769  pokeldn-radio-s3.bin\n");
    Check(sums.Count == 2 && sums["pokeldn-radio-s3.bin"].StartsWith("c581a4e8"), "SHA256SUMS parsed");
    if (PokeldnService.FindFirmware() is { } fw)
        Check(PokeldnService.FirmwareVersionOf(fw) is { Length: > 0 } v && Version.TryParse(v, out _), $"firmware image version: {PokeldnService.FirmwareVersionOf(fw)} ({fw})");
    var fakeImg = Path.Combine(work, "fake.bin");
    var img = new byte[0x10100];
    BitConverter.GetBytes(0xABCD5432u).CopyTo(img, 0x10020);
    System.Text.Encoding.ASCII.GetBytes("1.11.0").CopyTo(img, 0x10030);
    File.WriteAllBytes(fakeImg, img);
    Check(PokeldnService.FirmwareVersionOf(fakeImg) == "1.11.0", "app descriptor version read from a merged image");

    // prod.keys: names checked, values never needed; pokeldn's settings.json gets only the path (other fields kept)
    var keys = Path.Combine(work, "prod.keys");
    File.WriteAllText(keys, "aes_kek_generation_source = 00\naes_key_generation_source = 00\nmaster_key_00 = 00\n");
    Check(PokeldnService.MissingKeys(keys).SequenceEqual(new[] { "master_key_12" }), "missing key names found");
    var pdata = Path.Combine(work, "pokeldn_data");
    Directory.CreateDirectory(pdata);
    File.WriteAllText(Path.Combine(pdata, "settings.json"), """{"ot": "Ash", "keys": "old"}""");
    PokeldnService.SetKeysPath(keys, pdata);
    var js = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(pdata, "settings.json")))!;
    Check(js["keys"]!.ToString() == keys && js["ot"]!.ToString() == "Ash", "prod.keys path written into pokeldn's settings.json, other settings kept");

    var boards = PokeldnService.FindBoards();
    Console.WriteLine($"   ESP32 boards plugged in: {(boards.Count == 0 ? "none" : string.Join(", ", boards.Select(b => $"{b.Kind} {b.Port}")))}");
    foreach (var b in boards.Where(b => b.NativeUsb))
        Console.WriteLine($"   {b.Port}: {PokeldnService.QueryFirmware(b.Port)}");
}

if (tests.Contains("pokeldnlive"))
{
    // The real install path (downloads ~140 MB from GitHub, verifies checksums), then starts pokeldn once. Cleaned up afterwards.
    var hadApp = Directory.Exists(PokeldnService.AppDir);
    Console.WriteLine("   " + await PokeldnService.InstallAppAndFirmware(_ => { }));
    Console.WriteLine("   " + await PokeldnService.InstallEsptool(_ => { }));
    var app = PokeldnService.FindApp();
    Check(app != null && app.StartsWith(PokeldnService.AppDir) && PokeldnService.InstalledAppTag(app) is { Length: > 0 }, $"pokeldn installed: {PokeldnService.InstalledAppTag(app)} {app}");
    var fw = Path.Combine(PokeldnService.FirmwareDir, PokeldnService.FirmwareAsset);
    Check(PokeldnService.FirmwareVersionOf(fw) is { } fv && Version.TryParse(fv, out _), $"S3 firmware downloaded and checksum-verified: version {PokeldnService.FirmwareVersionOf(fw)}");
    var esptool = PokeldnService.FindEsptool();
    Check(esptool != null && esptool.StartsWith(PokeldnService.EsptoolDir), $"esptool installed: {esptool}");
    if (esptool != null)
    {
        using var v = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(esptool, "version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
        var outp = v.StandardOutput.ReadToEnd();
        v.WaitForExit(30000);
        Check(outp.Contains("esptool", StringComparison.OrdinalIgnoreCase), "esptool runs: " + outp.Split('\n').FirstOrDefault()?.Trim());
    }
    if (app != null)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(app) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(app)! })!;
        Thread.Sleep(15000);
        p.Refresh();
        var alive = !p.HasExited;
        var windowed = System.Diagnostics.Process.GetProcessesByName("pokeldn").Any(x => x.MainWindowHandle != IntPtr.Zero);
        Check(alive || windowed, $"pokeldn starts (running: {alive}, window: {windowed})");
        foreach (var x in System.Diagnostics.Process.GetProcessesByName("pokeldn")) try { x.Kill(true); } catch { }
        Thread.Sleep(2000);
    }
    if (!hadApp)
        foreach (var d in new[] { PokeldnService.AppDir, PokeldnService.FirmwareDir, PokeldnService.EsptoolDir })
            try { Directory.Delete(d, true); } catch (Exception e) { Console.WriteLine($"   (couldn't remove {d}: {e.Message})"); }
}

if (tests.Contains("checkpoint"))
{
    // A fake SD card with Checkpoint backups for several games (and look-alikes that must not match).
    var sd = Path.Combine(work, "fake_sd");
    if (Directory.Exists(sd)) Directory.Delete(sd, true);
    // Copies of real saves (blank saves made by PKHeX aren't recognised for every game); the originals are only read.
    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    var roc = Path.Combine(desk, @"RoCs-PC-master\RoCs-PC-master");
    var usSave = Path.Combine(roc, @"25 - Gen VII - USUM Collection\Save Data\20180228134249 - Ultra Sun\00001b50\main");
    var xSave = Path.Combine(roc, @"19 - Gen VI - XY Collection\Save Data\20180219163039 - X\0000055d\main");
    var shSave = Path.Combine(desk, @"PKHeX (26.10.18)\shield\20210420-120931 Dom\main");
    void Put(string folder, string source, DateTime when)
    {
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "main");
        File.Copy(source, file, true);
        File.SetLastWriteTime(file, when);
    }
    var t0 = new DateTime(2026, 11, 1, 12, 0, 0);
    Put(Path.Combine(sd, @"3ds\Checkpoint\saves\0x01B50 Pokémon Ultra Sun\20261101-120000"), usSave, t0);
    Put(Path.Combine(sd, @"3ds\Checkpoint\saves\0x01B50 Pokémon Ultra Sun\20261110-090000"), usSave, t0.AddDays(9));
    Put(Path.Combine(sd, @"3ds\Checkpoint\saves\0x01B51 Pokémon Ultra Moon\20261105-120000"), usSave, t0.AddDays(4)); // other title ID: ignored
    Put(Path.Combine(sd, @"3ds\Checkpoint\saves\0x0055D Pokémon X\backup"), xSave, t0);
    Put(Path.Combine(sd, @"switch\Checkpoint\saves\01008DB008C2C000 Pokémon Shield\20261102-101010"), shSave, t0.AddDays(1));
    Put(Path.Combine(sd, @"switch\Checkpoint\saves\0100ABF008968000 Pokémon Sword\20261102-101010"), shSave, t0.AddDays(1)); // a Shield save in Sword's folder
    Directory.CreateDirectory(Path.Combine(sd, @"switch\Checkpoint\saves\01008DB008C2C000 Pokémon Shield\empty backup"));

    var old = AppSettings.Current.CheckpointFolder;
    AppSettings.Current.CheckpointFolder = sd; // not saved: test only
    try
    {
        Check(CheckpointService.SplitFolder("0x01B51 Pokémon Ultra Moon") == ("01B51", "Pokémon Ultra Moon"), "3DS folder name split");
        Check(CheckpointService.SplitFolder("0100ABF008968000 Pokémon Sword") == ("0100ABF008968000", "Pokémon Sword"), "Switch folder name split");
        Check(CheckpointService.SplitFolder("IRBO Pokémon Black") == ("IRBO", "Pokémon Black"), "DS cartridge folder name split");
        var g = (string id) => GameCatalog.All.First(x => x.Id == id);
        Check(CheckpointService.Matches(g("3ds_umoon"), "0x01B51 Pokémon Ultra Moon") && !CheckpointService.Matches(g("3ds_moon"), "0x01B51 Pokémon Ultra Moon"),
            "Ultra Moon matched by ID, Moon not");
        Check(CheckpointService.Matches(g("nds_black"), "IRBO Pokémon Black") && !CheckpointService.Matches(g("nds_black2"), "IRBO Pokémon Black"), "DS game matched by name");
        Check(CheckpointService.Matches(g("sw_za"), "0100F43008C44000 Pokémon Legends: Z-A"), "Legends: Z-A matched (by title ID)");
        Check(CheckpointService.Matches(g("sw_frlg_lg"), "010034D02340E000 Pokemon LeafGreen") && !CheckpointService.Matches(g("sw_frlg_fr"), "010034D02340E000 Pokemon LeafGreen"),
            "Switch LeafGreen matched by ID, FireRed not");
        Check(CheckpointService.Matches(g("sw_lgp"), "010003F003A34000 Pokémon: Let's Go, Pikachu!"), "Let's Go Pikachu matched");

        var roots3 = CheckpointService.FindRoots(false);
        var rootsS = CheckpointService.FindRoots(true);
        Check(roots3.Any(r => r.StartsWith(sd)) && rootsS.Any(r => r.StartsWith(sd)), "Checkpoint roots found from the chosen folder");
        var um = CheckpointService.FindBackups(g("3ds_usun"), roots3.Where(r => r.StartsWith(sd)));
        Check(um.Count == 2 && um[0].BackupName == "20261110-090000", $"Ultra Sun: {um.Count} backups, newest first ({um.FirstOrDefault()?.BackupName}); Ultra Moon's folder ignored");
        var sh = CheckpointService.FindBackups(g("sw_shield"), rootsS.Where(r => r.StartsWith(sd)));
        Check(sh.Count == 1 && sh[0].Save.Save.Version == GameVersion.SH, $"Shield: {sh.Count} backup (empty backup folder and Sword's folder ignored)");
        Check(CheckpointService.FindBackups(g("sw_sword"), rootsS.Where(r => r.StartsWith(sd))).Count == 0, "a Shield save in Sword's folder isn't taken as Sword");
        Check(CheckpointService.FindBackups(g("3ds_x"), roots3.Where(r => r.StartsWith(sd))).Count == 1, "X backup found");
        var savesRoot = Path.Combine(work, "saves_copy");
        if (Directory.Exists(savesRoot)) Directory.Delete(savesRoot, true);
        var copied = CheckpointService.CopyToPc(um[1], g("3ds_usun"), savesRoot);
        Check(File.Exists(copied.Path) && copied.Path.StartsWith(savesRoot) && File.GetLastWriteTime(copied.Path) == t0, $"backup copied to the PC with its time kept: {Path.GetRelativePath(work, copied.Path)}");
        Check(SaveService.FindSaves(CheckpointService.GameFolder(g("3ds_usun"), savesRoot), g("3ds_usun")).Count == 1, "the copy is found by the normal save search");
        Check(Directory.GetLastWriteTime(um[0].Folder) <= DateTime.Now && File.Exists(um[1].Save.Path), "the SD card copy was only read");
    }
    finally { AppSettings.Current.CheckpointFolder = old; }

    // ---- The exact folder/file names Checkpoint writes (from its source: 3ds/source/paths.cpp + titleprobe.cpp,
    // switch/source/titleprobe.cpp + BackupList.cpp), filled with copies of real saves - one SD card with both systems.
    {
        var real = Path.Combine(work, "checkpoint_sd");
        if (Directory.Exists(real)) Directory.Delete(real, true);
        var pk = Path.Combine(desk, "PKHeX (26.10.18)");
        var bak = Path.Combine(pk, "bak");
        string Bak(string start) => Directory.EnumerateFiles(bak).First(f => Path.GetFileName(f).StartsWith(start));
        void CopyDir(string from, string to)
        {
            foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var t = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t, true);
            }
        }
        void PutAs(string folder, string source, string fileName)
        {
            Directory.CreateDirectory(folder);
            File.Copy(source, Path.Combine(folder, fileName), true);
        }
        var c3 = Path.Combine(real, @"3ds\Checkpoint\saves");
        var cs = Path.Combine(real, @"switch\Checkpoint\saves");
        // 3DS: "0x%05X " + English short title (Unicode kept, . , ! \ / : ? * " < > | -> space, trailing spaces cut)
        var bdsp = SaveUtil.TryGetSaveFile(Path.Combine(pk, "SaveData.bin"), out var bdspSav) ? bdspSav!.Version : GameVersion.Invalid;
        var cases = new (string Game, string Folder, string Source, string? FileName)[]
        {
            ("3ds_umoon", Path.Combine(c3, @"0x01B51 Pokémon Ultra Moon\20261112-221200"), Path.Combine(pk, "main"), "main"),
            ("3ds_usun", Path.Combine(c3, @"0x01B50 Pokémon Ultra Sun\20180228-134249"), usSave, "main"),
            ("3ds_x", Path.Combine(c3, @"0x0055D Pokémon X\20241119-133800"), xSave, "main"),
            // 3DS Virtual Console (GB/GBC): sav.dat; names as on the 3DS HOME menu
            ("gb_red", Path.Combine(c3, @"0x01710 Pokémon Red Version\20261101-120000"), Bak("Pokemon Red.sav"), "sav.dat"),
            ("gb_yellow", Path.Combine(c3, @"0x01712 Pokémon Yellow Version  Special Pikachu Edition\20261101-120000"), Bak("Pokemon Yellow.sav"), "sav.dat"),
            ("gbc_crystal", Path.Combine(c3, @"0x0172F Pokémon Crystal Version\20261101-120000"), Bak("sav.dat [DOM (C)"), "sav.dat"),
            ("gbc_gold", Path.Combine(c3, @"0x0172D Pokémon Gold Version\20261101-120000"), Bak("sav.dat [DOM (GS)"), "sav.dat"),
            // DS cartridge in a 3DS: "<game code> <12-char ROM header title>/<backup>/<ROM title>.sav"
            ("nds_black", Path.Combine(c3, @"IRBO POKEMON B\20261101-120000"), Path.Combine(pk, "Pokemon Black.sav"), "POKEMON B.sav"),
            ("nds_heartgold", Path.Combine(c3, @"IPKE POKEMON HG\20261101-120000"), Path.Combine(pk, "Pokemon HeartGold.sav"), "POKEMON HG.sav"),
            // Switch: "0x%016llX " + name, but the ID again when the name isn't ASCII (every "Pokémon" title); backup = "<date> <user>"
            ("sw_shield", Path.Combine(cs, @"0x01008DB008C2C000 0x01008DB008C2C000\20210420-120931 Dom"), Path.Combine(pk, @"shield\20210420-120931 Dom"), null),
            ("sw_pla", Path.Combine(cs, @"0x01001F5010DFA000 0x01001F5010DFA000\20220123-152959 Dom"), Path.Combine(pk, @"arceus\20220123-152959 Dom"), null),
            ("sw_violet", Path.Combine(cs, @"0x01008F6008C5E000 0x01008F6008C5E000\20261101-120000 Dom"), Path.Combine(pk, @"violet\OG Playthrough"), null),
            ("sw_za", Path.Combine(cs, @"0x0100F43008C44000 0x0100F43008C44000\20261101-120000 Dom"), Path.Combine(pk, @"legends za\main"), "main"),
            (bdsp == GameVersion.SP ? "sw_sp" : "sw_bd", Path.Combine(cs, (bdsp == GameVersion.SP ? "0x010018E011D92000 0x010018E011D92000" : "0x0100000011D90000 0x0100000011D90000") + @"\20261101-120000 Dom"),
                Path.Combine(pk, "SaveData.bin"), "SaveData.bin"),
            ("sw_frlg_lg", Path.Combine(cs, @"0x010034D02340E000 0x010034D02340E000\20261101-120000 Dom"), Path.Combine(pk, "LeafGreen_e.sav"), "LeafGreen_e.sav"),
        };
        foreach (var c in cases)
            if (c.FileName == null) CopyDir(c.Source, c.Folder); else PutAs(c.Folder, c.Source, c.FileName);

        var g = (string id) => GameCatalog.All.First(x => x.Id == id);
        foreach (var c in cases)
        {
            var game = g(c.Game);
            var root = game.IsSwitch ? cs : c3;
            var found = CheckpointService.FindBackups(game, new[] { root });
            var s = found.FirstOrDefault()?.Save.Save;
            Check(found.Count == 1 && s != null, $"{(game.IsSwitch ? "Switch" : "3DS")} {game.Title}: " +
                (s == null ? $"NOT found ({Path.GetFileName(Path.GetDirectoryName(c.Folder))})"
                           : $"found + read: {s.Version}, OT {s.OT}, TID {s.DisplayTID}, {s.BoxCount} boxes  [{Path.GetRelativePath(real, found[0].Save.Path)}]"));
            // no other game on that system claims this folder
            var title = Path.GetFileName(Path.GetDirectoryName(c.Folder))!;
            var others = GameCatalog.All.Where(x => x.IsSwitch == game.IsSwitch && x.Id != game.Id && CheckpointService.Matches(x, title)).Select(x => x.Id).ToList();
            Check(others.Count == 0, $"   folder \"{title}\" belongs to {game.Id} only" + (others.Count > 0 ? ": also " + string.Join(", ", others) : ""));
        }
        Check(!CheckpointService.Matches(g("gb_red"), "0x01234 Red Steel") && !CheckpointService.Matches(g("nds_black"), "IREO POKEMON B2")
              && CheckpointService.Matches(g("nds_black2"), "IREO POKEMON B2") && CheckpointService.Matches(g("nds_white"), "IRAD POKEMON W"),
            "look-alikes rejected (Red Steel, Black 2 for Black); other regions' DS carts accepted");

        // ---- The same SD card read over WiFi, from a mock console that answers exactly like ftpd (Checkpoint's FTP server).
        using (var ftpd = new MockFtpd(real))
        {
            var mirror = Path.Combine(work, "console_mirror");
            if (Directory.Exists(mirror)) Directory.Delete(mirror, true);
            Check(ConsoleSdService.FindPort("127.0.0.1", new[] { 50001, ftpd.Port }) == ftpd.Port, $"console FTP server found on port {ftpd.Port}");
            foreach (var id in new[] { "sw_shield", "sw_za", "sw_pla", "3ds_umoon", "nds_black", "gbc_crystal" })
            {
                var game = g(id);
                var local = Path.Combine(mirror, game.IsSwitch ? "Switch" : "3DS");
                var m = ConsoleSdService.Mirror(game, "127.0.0.1", ftpd.Port, null, local);
                var found = CheckpointService.FindBackups(game, new[] { m.Root });
                var s = found.FirstOrDefault()?.Save.Save;
                Check(m.TitleFolders == 1 && found.Count == 1 && s != null,
                    $"over WiFi: {game.Title}: {m.Downloaded} file(s) downloaded" + (s == null ? " - NOT read" : $", read {s.Version} OT {s.OT} TID {s.DisplayTID}"));
            }
            var sw = Path.Combine(mirror, "Switch");
            var shTitle = Path.Combine(real, @"switch\Checkpoint\saves\0x01008DB008C2C000 0x01008DB008C2C000");
            var again = ConsoleSdService.Mirror(g("sw_shield"), "127.0.0.1", ftpd.Port, null, sw);
            Check(again.Downloaded == 0 && again.Files > 0, $"unchanged backups aren't downloaded again ({again.Files} files checked)");
            var src = Path.Combine(shTitle, "20210420-120931 Dom");
            var second = Path.Combine(shTitle, "20261115-100000 Dom");
            Directory.CreateDirectory(second);
            File.Copy(Path.Combine(src, "main"), Path.Combine(second, "main"));
            File.SetLastWriteTime(Path.Combine(second, "main"), DateTime.Now);
            ConsoleSdService.Mirror(g("sw_shield"), "127.0.0.1", ftpd.Port, null, sw);
            var two = CheckpointService.FindBackups(g("sw_shield"), new[] { sw });
            Check(two.Count == 2 && two[0].BackupName == "20261115-100000 Dom", $"a new backup on the console shows up first ({two.FirstOrDefault()?.BackupName})");
            Directory.Delete(second, true);
            ConsoleSdService.Mirror(g("sw_shield"), "127.0.0.1", ftpd.Port, null, sw);
            Check(CheckpointService.FindBackups(g("sw_shield"), new[] { sw }).Count == 1, "a backup deleted on the console disappears from the mirror");
            var copied = CheckpointService.CopyToPc(CheckpointService.FindBackups(g("sw_za"), new[] { sw })[0], g("sw_za"), Path.Combine(work, "saves_copy"));
            Check(File.Exists(copied.Path) && copied.Save.Version == GameVersion.ZA, "a backup read over WiFi is copied to the PC like one from a card");
            var writes = ftpd.Commands.Where(c => c is not ("USER" or "PASS" or "OPTS" or "TYPE" or "PASV" or "MLSD" or "LIST" or "RETR" or "QUIT")).Distinct().ToList();
            Check(writes.Count == 0, "only read commands were sent to the console" + (writes.Count > 0 ? ": " + string.Join(", ", writes) : $" ({string.Join(" ", ftpd.Commands.Distinct())})"));
            Check(ConsoleSdService.FindPort("127.0.0.1", new[] { 50001 }) == null, "no FTP server -> nothing found (the user is told how to turn it on)");
        }
        Directory.Delete(real, true);
    }

    // every Switch game ShinyForge can open on the Switch has its HOME-menu icon (all languages)
    var noIcon = GameCatalog.ForSwitch.SelectMany(SwitchGameLauncher.TitleIds).Where(t => !File.Exists(Path.Combine(AppPaths.AssetsDir, "SwitchIcons", t + ".jpg"))).ToList();
    Check(noIcon.Count == 0, "Switch game icons for every title ID" + (noIcon.Count > 0 ? ": missing " + string.Join(", ", noIcon) : ""));

    // Gen 7 trackers are recognised
    foreach (var id in new[] { "3ds_sun", "3ds_moon", "3ds_usun", "3ds_umoon", "sw_lgp", "sw_lge" })
    {
        var wb = SpreadsheetService.FindWorkbook(GameCatalog.All.First(x => x.Id == id));
        Check(wb != null, $"{id}: tracker {(wb == null ? "NOT found" : Path.GetFileName(wb))}");
    }
}

if (tests.Contains("switchftp"))
{
    // REAL HARDWARE: opens the Homebrew Menu on the Switch (game must be closed), starts the FTP app, reads every Pokémon backup.
    ConsoleFtpLauncher.Decode = ShinyForge.UI.GdiImage.Decode;
    ConsoleFtpLauncher.ScreenshotDir = Path.Combine(work, "hbmenu_scan");
    if (Directory.Exists(ConsoleFtpLauncher.ScreenshotDir)) Directory.Delete(ConsoleFtpLauncher.ScreenshotDir, true);
    var s = AppSettings.Current;
    Console.WriteLine($"   Switch {s.SwitchIP}, sys-botbase port {s.SwitchPort}");
    var port = ConsoleSdService.FindPort(s.SwitchIP);
    if (port == null)
    {
        var game = ConsoleFtpLauncher.RunningGame(s.SwitchIP, s.SwitchPort);
        Check(game == null, game == null ? "no game open on the Switch" : $"a game is open ({game}) - close it first");
        if (game == null)
            try { port = ConsoleFtpLauncher.StartFtpApp(s.SwitchIP, s.SwitchPort, m => Console.WriteLine("   " + m)); }
            catch (Exception e) { Check(false, "starting the FTP app: " + e.Message); }
    }
    Check(port != null, $"FTP server on the Switch: port {port}");
    if (port != null)
    {
        using (var ftp = ConsoleFtp.Connect(s.SwitchIP, port.Value))
        {
            var titles = ftp.List(ConsoleSdService.RemoteSaves(true));
            Console.WriteLine($"   {titles.Count} Checkpoint title folders on the SD card:");
            foreach (var t in titles) Console.WriteLine($"      {t.Name}");
        }
        var mirror = Path.Combine(work, "switch_sd");
        foreach (var game in GameCatalog.ForSwitch)
        {
            var m = ConsoleSdService.Mirror(game, s.SwitchIP, port.Value, null, mirror);
            if (m.TitleFolders == 0) continue;
            if (m.Files == 0) { Console.WriteLine($"   {game.Title}: Checkpoint folder exists, no backups yet"); continue; }
            var found = CheckpointService.FindBackups(game, new[] { m.Root });
            var sv = found.FirstOrDefault()?.Save.Save;
            Check(found.Count > 0, $"{game.Title}: {m.Files} file(s), {found.Count} readable backup(s)" +
                (sv == null ? " - files: " + string.Join(", ", Directory.EnumerateFiles(m.Root, "*", SearchOption.AllDirectories)
                                   .Where(f => CheckpointService.Matches(game, Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))!)))
                                   .Select(f => $"{Path.GetFileName(f)} ({new FileInfo(f).Length} B)"))
                            : $"; newest: {sv.Version}, OT {sv.OT}, TID {sv.DisplayTID}, {sv.PlayedHours}h"));
        }
    }
}

if (tests.Contains("homedex"))
{
    // BUILD STEP for the Living Dex: SelfTest <work> homedex <serebii depositablepokemon.shtml> <"...\Detailed" HOME renders folder>
    // Every boxable slot from Serebii's Pokémon HOME list → PKHeX species/form/gender/sweet → the HOME render files.
    // Writes <work>\home_dex.json (then tools\make_home_sprites.py makes Assets\Home from it).
    var a = args.Skip(1).Where(x => x != "homedex").ToList();
    var html = File.ReadAllText(a[0], System.Text.Encoding.Latin1);
    var renders = a[1];
    var normal = Directory.GetFiles(Path.Combine(renders, "Normal"), "poke_capture_*.png").Select(Path.GetFileName).ToHashSet()!;
    var shiny = Directory.GetFiles(Path.Combine(renders, "Shiny"), "poke_capture_*.png").Select(Path.GetFileName).ToHashSet()!;
    // Some shiny renders are named by hand: "(19) Rattata (F).png", "(19.1) Alolan Rattata.png", "(1) Bulbasaur.png".
    var named = Directory.GetFiles(Path.Combine(renders, "Shiny"), "(*.png").Select(Path.GetFileName).ToList();
    string? NamedShiny(int sp, string alt, bool female)
    {
        var mine = named.Where(f => System.Text.RegularExpressions.Regex.IsMatch(f!, $@"^\({sp}(\.\d+)?\) ")).ToList();
        string Label(string f) => System.Text.RegularExpressions.Regex.Replace(f[(f.IndexOf(')') + 2)..^4], @"\s*\((F|M)\)$", "");
        var want = N(alt.Replace("(Female)", ""));
        var cands = mine.Where(f => N(Label(f!)) == want && !f!.Contains("G-") && !f.Contains("Mega")).ToList();
        // cap Pikachu: "(25) Ash Hat Pikachu (Orginal).png" (sic)
        if (cands.Count == 0 && sp == 25 && alt.EndsWith(" Cap"))
        {
            var region = N(alt[8..^4]).Replace("original", "orginal");
            cands = mine.Where(f => f!.Contains("Ash Hat") && N(f).Contains(region)).ToList();
        }
        // typos in the hand-made names ("Squritle", "Cartipie"): the species' only plain file
        if (cands.Count == 0 && N(alt) == N(SpeciesName.GetSpeciesName((ushort)sp, 2)))
            cands = mine.Where(f => f!.StartsWith($"({sp}) ") && !f.Contains("G-") && !f.Contains("Mega")).ToList();
        if (cands.Count == 0) return null;
        return cands.FirstOrDefault(f => f!.EndsWith(female ? "(F).png" : "(M).png")) ?? cands.FirstOrDefault(f => !f!.EndsWith("(F).png")) ?? cands[0];
    }
    var std = html[html.IndexOf("<div id=\"normal\"")..html.IndexOf("<div id=\"shiny\"")];
    var slots = System.Text.RegularExpressions.Regex.Matches(std, "<img src=\"/pokemonhome/pokemon/80/([^\"]+)\\.png\"[^>]*alt=\"([^\"]*)\"")
        .Select(m => (Code: m.Groups[1].Value, Alt: System.Net.WebUtility.HtmlDecode(m.Groups[2].Value))).ToList();
    string N(string s) => new string(s.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD).Where(char.IsLetterOrDigit).ToArray());
    string[] creams = { "vanillacream", "rubycream", "matchacream", "mintcream", "lemoncream", "saltedcream", "rubyswirl", "caramelswirl", "rainbowswirl" };
    string[] sweets = { "", "berry", "love", "star", "clover", "flower", "ribbon" };
    var outList = new List<object>();
    var problems = new List<string>();
    foreach (var (code, alt) in slots)
    {
        var parts = code.Split('-', 2);
        var sp = ushort.Parse(parts[0]);
        var suffix = parts.Length > 1 ? parts[1] : "";
        // "-f" is not always female on Serebii (Maushold Family of Four, Polteageist Authentic): use the label
        var female = alt.Contains("(Female)") || alt.EndsWith(" Female");
        var species = SpeciesName.GetSpeciesName(sp, 2).Replace('’', '\'');
        var label = alt.Replace("(Female)", "").Replace('’', '\'').Trim();
        if (label.EndsWith(" Female")) label = label[..^7].Trim();
        if (label.StartsWith(species + " ")) label = label[(species.Length + 1)..];
        else if (label == species) label = "";
        else if (label.EndsWith(" " + species)) label = label[..^(species.Length + 1)];
        label = label.Trim();
        // "215-h-f" = Hisuian Sneasel (female), labelled just "Sneasel (Female)": the regional letter is in the code
        if (label.Length == 0 && female && suffix.Length == 3 && suffix.EndsWith("-f"))
            label = suffix[0] switch { 'a' => "Alolan", 'g' => "Galarian", 'h' => "Hisuian", 'p' => "Paldean", _ => "" };
        byte form = 0; int sweet = 0; byte? renderForm = null;
        var names = FormConverter.GetFormList(sp, GameInfo.Strings.types, GameInfo.Strings.forms, new[] { "♂", "♀", "-" }, EntityContext.Gen9);
        var nl = N(label);
        string[] minior = { "red", "orange", "yellow", "green", "blue", "indigo", "violet" };
        // Forms whose Serebii label doesn't match PKHeX's form name (checked one by one).
        int? fixedForm = sp switch
        {
            128 => nl.Contains("blaze") ? 2 : nl.Contains("aqua") ? 3 : nl.Length > 0 ? 1 : 0,       // Paldean Tauros: Combat, Blaze, Aqua
            201 when label is "!" or "?" => label == "!" ? 26 : 27,
            658 => nl.Contains("battlebond") ? 1 : 0,                                            // Battle Bond Greninja is form 1
            666 when nl.Length == 0 => 6,                                                        // plain Vivillon = Meadow
            670 when nl.Contains("eternal") => 5,
            676 when nl.Contains("deputante") => 4,                                              // Serebii's spelling of Debutante
            710 or 711 => nl.Contains("super") ? 3 : nl.Contains("large") ? 2 : nl.Contains("small") ? 1 : 0,
            718 => nl.Contains("powerconstruct") ? (code.EndsWith("-10") ? 2 : 3) : nl.Contains("10") ? 1 : 0,
            744 => nl.Contains("owntempo") ? 1 : 0,                                              // Own Tempo Rockruff
            774 => Math.Max(0, Array.FindIndex(minior, c => nl.StartsWith(c))),                  // boxed Minior: the core colour
            _ => null,
        };
        if (sp == 774) renderForm = (byte)(form + 7); // shown with its coloured core (renders 7-13), not the brown shell
        if (fixedForm != null) { form = (byte)fixedForm; if (sp == 774) renderForm = (byte)(form + 7); }
        else if (sp == 869)
        {
            var cream = label.Length == 0 ? 0 : Array.IndexOf(creams, N(label));
            if (cream < 0) { problems.Add($"unknown Alcremie cream {code} \"{alt}\""); continue; }
            form = (byte)cream;
            var s = suffix;
            foreach (var c in new[] { "rc", "mac", "mic", "lc", "sc", "rs", "cs", "ras" }) if (s.StartsWith(c)) { s = s[c.Length..]; break; }
            sweet = Math.Max(0, Array.IndexOf(sweets, s));
        }
        else if (female && names.Contains("♀"))
            form = (byte)Array.IndexOf(names, "♀");    // Meowstic, Indeedee, Basculegion, Oinkologne: the female is a form
        else if (label.Length > 0)
        {
            var want = N(label).Replace("authentic", "antique").Replace("alolan", "alola").Replace("galarian", "galar").Replace("hisuian", "hisui").Replace("paldean", "paldea")
                               .Replace("pattern", "").Replace("cap", "").Replace("form", "").Replace("rotom", "");
            var best = -1;
            for (var i = names.Length > 0 && names[0] is not ("" or "Normal") ? 0 : 1; i < names.Length; i++)
            {
                var n = N(names[i]).Replace("alolan", "alola").Replace("galarian", "galar").Replace("hisuian", "hisui").Replace("paldean", "paldea").Replace("form", "");
                if (n.Length > 0 && (n == want || (want.Length > 2 && n.Contains(want)) || (n.Length > 2 && want.Contains(n)))) { best = i; if (n == want) break; }
            }
            if (best < 0) { problems.Add($"no form for {code} \"{alt}\" (forms: {string.Join(", ", names)})"); continue; }
            form = (byte)best;
        }
        string? Find(HashSet<string> set, string end, byte fm, int sw)
        {
            // female look: fd; otherwise the male/neutral look - female-only species (Enamorus, Tinkatink) only have fd/fo
            // (genderless "uk" and female-only "fo" before the stray "mf" copies some of those species also have)
            foreach (var g in female ? new[] { "fd", "fo", "uk", "mf" } : new[] { "md", "mo", "uk", "fo", "mf", "fd" })
            {
                var f = $"poke_capture_{sp:0000}_{fm:000}_{g}_n_{sw:00000000}_f_{end}.png";
                if (set.Contains(f)) return f;
            }
            return null;
        }
        // forms that look the same as form 0 have no render of their own (Artisan Poltchageist, Masterpiece Sinistcha);
        // every shiny Alcremie looks the same whatever the cream (only the sweet differs)
        var rf = renderForm ?? form;
        var nf = Find(normal, "n", rf, sweet) ?? Find(normal, "n", 0, sweet);
        var sf = Find(shiny, "r", rf, sweet) ?? NamedShiny(sp, alt, female) ?? Find(shiny, "r", 0, sweet)
                 // look-alike forms: shiny Greninja = Battle Bond's render; Zygarde 50% / 10% = their Power Construct renders
                 ?? (sp == 658 && rf == 0 ? Find(shiny, "r", 1, 0) : null)
                 ?? (sp == 718 && rf <= 1 ? Find(shiny, "r", (byte)(3 - rf), 0) : null);
        if (nf == null) problems.Add($"no normal render for {code} \"{alt}\" (form {form}, sweet {sweet})");
        if (sf == null) problems.Add($"no shiny render for {code} \"{alt}\" (form {form}, sweet {sweet})");
        var formName = sp == 869 ? $"{names[Math.Max(0, (int)form)]}, {(sweet == 0 ? "Strawberry" : char.ToUpper(sweets[sweet][0]) + sweets[sweet][1..])} Sweet" : label;
        var pkhexForm = form < names.Length ? names[form] : "?";
        if (formName.Length == 0 && form > 0 && pkhexForm is not ("♀" or "♂" or "?")) formName = pkhexForm; // plain Vivillon = Meadow
        outList.Add(new { species = sp, form, female, sweet, name = DexEntry.Plain(species), formName = DexEntry.Plain(formName), pkhexForm, serebii = code, normal = nf ?? "", shiny = sf ?? "" });
    }
    // renders of boxable-looking forms Serebii doesn't list (should only be battle-only forms, Gigantamax is skipped already)
    var used = outList.Select(o => (string)o.GetType().GetProperty("normal")!.GetValue(o)!).ToHashSet();
    var extra = normal.Where(f => f!.Contains("_n_") && f.EndsWith("_f_n.png") && !used.Contains(f) && !f.Contains("_mf1_") && !f.Contains("_mfx_") && !f.Contains("_mfy_")).OrderBy(f => f).ToList();
    File.WriteAllText(Path.Combine(work, "home_dex.json"), System.Text.Json.JsonSerializer.Serialize(outList, new System.Text.Json.JsonSerializerOptions { WriteIndented = false }));
    File.WriteAllLines(Path.Combine(work, "home_dex_not_listed.txt"), extra!);
    Console.WriteLine($"   {slots.Count} Serebii slots, {outList.Count} mapped, {problems.Count} problem(s), {extra.Count} render(s) not on Serebii's list");
    foreach (var p in problems) Console.WriteLine("   ! " + p);
    Check(problems.Count == 0 && outList.Count == slots.Count, "every boxable slot has a form and both renders");
}

if (tests.Contains("dexinfo"))
{
    // Evolution text and Serebii links for a set of tricky families in several games, plus a check that EVERY species in
    // EVERY game gets readable evolution text (no raw method names, no errors).
    var g = (string id) => GameCatalog.All.First(x => x.Id == id);
    foreach (var (game, sp, form) in new (string, ushort, byte)[] {
        ("gba_leafgreen", 18, 0), ("sw_frlg_lg", 133, 0), ("nds_platinum", 82, 0), ("nds_heartgold", 133, 0), ("nds_black", 290, 0),
        ("3ds_usun", 25, 0), ("3ds_umoon", 26, 1), ("sw_sword", 133, 0), ("sw_shield", 349, 0), ("sw_pla", 215, 1), ("sw_scarlet", 133, 0),
        ("sw_violet", 924, 0), ("sw_za", 133, 0), ("gb_red", 64, 0), ("gbc_crystal", 236, 0), ("3ds_x", 686, 0), ("sw_bd", 133, 0),
        ("sw_pla", 215, 0), ("sw_pla", 234, 0), ("sw_pla", 550, 2), ("sw_pla", 217, 0), ("sw_scarlet", 206, 0), ("sw_sword", 891, 0) })
    {
        var e = DexData.Build(g(game), true).First(x => x.Species == sp && x.Form == form);
        Console.WriteLine($"--- {g(game).Title}: {e.Display}   {DexData.SerebiiUrl(g(game), e)}");
        foreach (var l in DexData.Evolution(g(game), e).Split(Environment.NewLine)) Console.WriteLine("   " + l);
    }
    var bad = new List<string>();
    foreach (var game in GameCatalog.All)
        foreach (var e in DexData.Build(game, true))
        {
            var t = DexData.Evolution(game, e);
            if (t.Contains("unavailable") || t.Contains("when it levels up  ") || System.Text.RegularExpressions.Regex.IsMatch(t, "LevelUp|UseItem|Trade[A-Z]"))
                bad.Add($"{game.Id} {e.Display}: {t}");
        }
    Check(bad.Count == 0, $"evolution text for every species in every game ({bad.Count} problem(s))" + (bad.Count > 0 ? ": " + string.Join(" | ", bad.Take(5)) : ""));
    var links = GameCatalog.All.Select(x => (x.Title, Url: DexData.SerebiiUrl(x, new DexEntry { Species = 18 }))).ToList();
    Check(links.First(l => l.Title == "LeafGreen").Url.Contains("/pokedex-rs/018") && links.First(l => l.Title == "LeafGreen (Switch)").Url.Contains("/pokedex-rs/")
          && links.First(l => l.Title == "Let's Go, Pikachu!").Url.Contains("/pokedex-sm/") && links.First(l => l.Title == "Legends: Arceus").Url.Contains("/pokedex-swsh/")
          && links.First(l => l.Title == "Red").Url.Contains("/pokedex/018"), "Serebii links go to each game's own Pokédex");
    foreach (var l in links) Console.WriteLine($"   {l.Title,-22} {l.Url}");
}

if (tests.Contains("homemenu"))
{
    // Offline: SelfTest <work> homemenu <folder with Switch HOME screenshots> - frame, icon, user-count and All Software checks
    // on saved screenshots (row*.jpg = HOME row, all_*.jpg = All Software, as_open.jpg = "Who is using the software?").
    ConsoleFtpLauncher.Decode = ShinyForge.UI.GdiImage.Decode;
    var dir = args.Skip(1).First(a => a != "homemenu" && Directory.Exists(a));
    RgbImage Load(string f) => ConsoleFtpLauncher.Decode!(File.ReadAllBytes(Path.Combine(dir, f)))!;
    List<double[]> Icons(string id) => SwitchGameLauncher.IconPrints(SwitchGameLauncher.TitleIds(GameCatalog.Get(id)));
    bool IconAt(RgbImage img, int x, int y, int w, string id)
    {
        var p = ConsoleFtpLauncher.Fingerprint(img, (x + 10, y + 10, w - 20, w - 20));
        return Icons(id).Min(i => ConsoleFtpLauncher.Difference(p, i)) <= 22;
    }
    var row10 = Load("row10.jpg");
    Check(SwitchGameLauncher.SelectedBox(row10) is { X: >= 900 and <= 915, W: > 260 }, $"HOME row: the selected tile's frame ({SwitchGameLauncher.SelectedBox(row10)})");
    Check(SwitchGameLauncher.SelectedBox(Load("wrap_left.jpg")) is { X: < 160 }, "HOME row: the cursor on the first game is recognised");
    Check(SwitchGameLauncher.SelectedBox(Load("as_sel2.jpg")) == null, "HOME row: \"All Software\" (no tile frame) is recognised");
    Check(IconAt(row10, 647, 194, 256, "sw_frlg_lg") && !IconAt(row10, 647, 194, 256, "sw_frlg_fr") && IconAt(row10, 377, 194, 256, "sw_frlg_fr"),
        "HOME row: LeafGreen and FireRed told apart by their icons");
    var all0 = Load("all_0.jpg");
    Check(SwitchGameLauncher.SelectedBox(all0) is { W: > 170 and < 200 }, "All Software: the selected icon's frame");
    Check(IconAt(all0, 829, 351, 174, "sw_frlg_lg") && !IconAt(all0, 645, 351, 174, "sw_frlg_lg") && !IconAt(all0, 93, 167, 174, "sw_frlg_lg"),
        "All Software: LeafGreen found among the other games");
    Check(SwitchGameLauncher.SelectedBox(Load("all_d5.jpg")) is { Y: > 300 and < 360 }, "All Software: frame found under the name tooltip");
    var picker = Load("as_open.jpg");
    Check(ConsoleFtpLauncher.IsUserPicker(picker) && SwitchGameLauncher.CountUsers(picker) == 3, $"user picker: {SwitchGameLauncher.CountUsers(picker)} users");
}

if (tests.Contains("switchfind"))
{
    // REAL HARDWARE, starts nothing: SelfTest <work> switchfind <game id> - HOME menu, cursor on the first game, then the game is
    // looked for along the HOME row and in All Software; the cursor is left on it (no A), then back to HOME.
    ConsoleFtpLauncher.Decode = ShinyForge.UI.GdiImage.Decode;
    var s = AppSettings.Current;
    var game = GameCatalog.Get(args.Skip(1).First(a => a != "switchfind" && GameCatalog.All.Any(g => g.Id == a)));
    using var bot = new SysBotSession(s.SwitchIP, s.SwitchPort);
    Check(bot.TitleId() == null, "nothing is running on the Switch (nothing will be started)");
    bot.Tap("HOME", 1500);
    // the starting point is the first game wherever the cursor was: on a user icon (top), the round buttons (bottom), All Software
    foreach (var (name, button, times) in new[] { ("a user icon", "DUP", 2), ("the bottom buttons", "DDOWN", 2), ("the end of the row", "DRIGHT", 15) })
    {
        for (var i = 0; i < times; i++) bot.Tap(button, 250);
        SwitchGameLauncher.Box? b = null;
        try { b = SwitchGameLauncher.StartAtFirstGame(bot).Box; } catch (IOException e) { Console.WriteLine("   " + e.Message); }
        if (bot.Screenshot() is { } at) File.WriteAllBytes(Path.Combine(work, $"start_from_{button}.jpg"), at);
        Check(b is { X: < 160, W: > 220 }, $"from {name}: cursor on the first game, nothing left of it ({b}, start_from_{button}.jpg)");
    }
    var (img, box) = SwitchGameLauncher.StartAtFirstGame(bot);
    Check(box.X < 160, $"cursor on the first game of the HOME menu ({box})");
    var icons = SwitchGameLauncher.IconPrints(SwitchGameLauncher.TitleIds(game));
    var onRow = SwitchGameLauncher.FindOnHomeRow(bot, icons, ref img, ref box, CancellationToken.None);
    var where = onRow ? "on the HOME row" : SwitchGameLauncher.FindInAllSoftware(bot, icons, m => Console.WriteLine("   " + m), CancellationToken.None) ? "in All Software" : null;
    if (bot.Screenshot() is { } shot) File.WriteAllBytes(Path.Combine(work, $"found_{game.Id}.jpg"), shot);
    Check(where != null, $"Pokémon {game.Title} found {where ?? "nowhere"} (screenshot found_{game.Id}.jpg)");
    if (!onRow) bot.Tap("B", 1000);
    bot.Tap("HOME", 1000);
    Check(bot.TitleId() == null, "still nothing running");
}

if (tests.Contains("updatesall"))
{
    // Every tool: a newer version is found, shown in the tool's OWN row, kept after the window's re-check, with the right
    // kind of update. Tools whose path can be pointed elsewhere get a fake OLDER copy (in memory only - nothing is saved);
    // the others are checked as they are on this PC.
    var s = AppSettings.Current;
    var real = await DependencyService.CheckUpdates();
    await DependencyService.ReadVersions();
    foreach (var d in DependencyService.All) Console.WriteLine($"   {d.Name,-20} {d.Version ?? "-",-18} {d.State}");
    foreach (var u in real) Console.WriteLine($"   found: {u.Item}: {u.Message}");
    var extras = new[] { "sys-botbase (Switch)", "PKHeX library", "Game artwork" };
    Check(real.All(u => DependencyService.All.Any(d => d.Name == u.Item) || extras.Contains(u.Item)),
        "every update belongs to a tool row (or is one of: sys-botbase on the Switch, PKHeX library, artwork)");
    Check(real.All(u => DependencyService.All.FirstOrDefault(d => d.Name == u.Item) is not { } d || d.State == DepState.UpdateAvailable),
        "every tool with an update shows 'Update available'");

    // fake older copies: a program whose version is 1.0.0.0 (SysBot.exe says that) / older folder names
    var oldExe = Path.Combine(work, "old-tool", "Tool.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(oldExe)!);
    File.Copy(s.SysBotPath, oldExe, true); // SysBot.exe's own version is 1.0.0.0
    var oldPa = Path.Combine(work, "PA-SerialPrograms-Windows-x64-0.60.0-20250101", "SerialPrograms-Windows.cmd");
    Directory.CreateDirectory(Path.GetDirectoryName(oldPa)!); File.WriteAllText(oldPa, "rem test");
    var oldBot = Path.Combine(work, "Sys-EncounterBot.NET-2.6.0", "SysBot.Pokemon.WinForms", "bin", "Release", "SysBot.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(oldBot)!); File.WriteAllText(oldBot, "test");
    var keep = (s.PKHeXPath, s.StreamlabsPath, s.PokemonAutomationPath, s.SysBotPath);
    try
    {
        s.PKHeXPath = oldExe; s.StreamlabsPath = oldExe; s.PokemonAutomationPath = oldPa; s.SysBotPath = oldBot;
        foreach (var d in DependencyService.All) d.Version = null; // read again from the fake copies
        var found = await DependencyService.CheckUpdates();
        foreach (var (name, apply) in new[] { ("PKHeX", true), ("Streamlabs Desktop", true), ("Pokémon Automation", true), ("SysBot.exe", false) })
        {
            var dep = DependencyService.All.First(d => d.Name == name);
            var u = DependencyService.UpdateFor(dep);
            Check(u != null && dep.State == DepState.UpdateAvailable, $"{name} (fake older copy): 'Update available' in its own row - {u?.Message}");
            DependencyService.Refresh(dep);
            Check(dep.State == DepState.UpdateAvailable, $"{name}: still 'Update available' after the window re-checks it");
            Check((u?.Apply != null) == apply, $"{name}: {(apply ? "has an Update button" : "report only (built from source)")}");
        }
    }
    finally
    {
        (s.PKHeXPath, s.StreamlabsPath, s.PokemonAutomationPath, s.SysBotPath) = keep;
        DependencyService.ForgetSysBotCheck();
        foreach (var d in DependencyService.All) d.Version = null;
        var back = await DependencyService.CheckUpdates();
        Check(back.Select(u => u.Item).OrderBy(x => x).SequenceEqual(real.Select(u => u.Item).OrderBy(x => x)), "real tools restored: the same updates as before");
    }
}

if (tests.Contains("toolupdate"))
{
    // REAL download: every tool with a newer release is marked in its own row, stays marked after a re-check, and the
    // update really installs it: SelfTest <work> toolupdate <tool name>
    var name = args.Skip(1).FirstOrDefault(a => a != "toolupdate") ?? "pokeldn";
    var found = await DependencyService.CheckUpdates();
    foreach (var u in found) Console.WriteLine($"   update: {u.Item}: {u.Message}");
    var dep = DependencyService.All.First(d => d.Name == name);
    var upd = DependencyService.UpdateFor(dep);
    Check(upd != null && dep.State == DepState.UpdateAvailable, $"{name}: shown as 'Update available' ({upd?.Message})");
    DependencyService.Refresh(dep);
    Check(dep.State == DepState.UpdateAvailable, $"{name}: still 'Update available' after the window re-checks it");
    if (upd != null)
    {
        var msg = await DependencyService.ApplyUpdate(upd, m => Console.WriteLine("   " + m));
        Console.WriteLine("   " + msg);
        Check(dep.State == DepState.Ok && DependencyService.UpdateFor(dep) == null, $"{name}: up to date after updating (now {dep.Version})");
        var again = await DependencyService.CheckUpdates();
        Check(again.All(u => u.Item != name), $"{name}: a fresh check finds nothing newer");
    }
}

if (tests.Contains("stoptargets"))
{
    // Offline: which Pokémon each hunt game offers, and the impossible picks refused with the user's message.
    foreach (var id in new[] { "sw_frlg_lg", "sw_sword", "sw_bd", "sw_lgp", "3ds_x", "3ds_usun", "3ds_or", "gb_red", "gbc_crystal" })
    {
        if (GameCatalog.All.FirstOrDefault(g => g.Id == id) is not { } g) { Console.WriteLine($"   ({id} not in the catalog)"); continue; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var list = StopTargets.Available(g);
        Console.WriteLine($"   {g.Title,-22} {list.Count,4} Pokémon, {list.Count(o => !o.ShinyPossible),3} shiny-locked  ({sw.Elapsed.TotalSeconds:F1} s)  " +
                          string.Join(", ", list.Where(o => !o.ShinyPossible).Select(o => o.Name)));
    }
    var lg = GameCatalog.Get("sw_frlg_lg");
    var sword = GameCatalog.Get("sw_sword");
    string? P(Game g, ushort sp, string gender = "any", string shiny = "any", int form = -1) =>
        StopTargets.Problem(g, new StopTarget { Species = sp, Gender = gender, Shiny = shiny, Form = form });
    Check(P(lg, 113, "female") == null && P(lg, 113, "male") is { } male && male.Contains("does not exist"), "LeafGreen: female Chansey ok, male Chansey refused with the warning");
    Check(P(lg, 81, "female") != null && P(lg, 81) == null, "genderless Magnemite: 'female' refused, 'any' ok");
    Check(P(lg, 386, shiny: "shiny") == null, "LeafGreen: shiny Deoxys possible");
    Check(P(lg, 25, shiny: "square") != null, "LeafGreen has no square shinies");
    Check(StopTargets.Available(lg).First(o => o.Species == 201).Forms.Count(f => f.Length > 0) == 28, "LeafGreen: Unown has 28 forms");
    Check(P(sword, 888, shiny: "shiny") != null && P(sword, 888) == null, "Sword: Zacian is shiny-locked");
    Check(P(sword, 52, form: 2) == null && P(sword, 52, form: 3) != null, "Sword: Galarian Meowth ok, a Meowth form that doesn't exist refused");
    Check(P(lg, 52, form: 2) != null && P(lg, 52) == null, "LeafGreen: no Galarian Meowth (Gen 3), Meowth itself ok");
    Check(P(lg, 133) == null && P(lg, 810) != null, "LeafGreen: Eevee yes, Grookey (Gen 8) no");
    // what the bots get (in memory only - settings aren't saved): SysBot's stop conditions and the Python hunter's targets
    var keepSword = AppSettings.Current.For("sw_sword").StopTargets;
    var keepLg = AppSettings.Current.For("sw_frlg_lg").StopTargets;
    try
    {
        AppSettings.Current.For("sw_sword").StopTargets = new() { new StopTarget { Species = 133, Gender = "female", Shiny = "square" } };
        var cfg = Path.Combine(work, "sysbot-config.json");
        File.WriteAllText(cfg, "{\"StopConditions\": {\"TargetNatures\": \"Jolly\"}, \"Bots\": []}");
        HuntService.PrepareSysBotConfig(cfg, "192.0.2.1", 6000, 1, sword);
        var stop = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfg))!["StopConditions"]!;
        Check((int)stop["StopOnSpecies"]! == 133 && (int)stop["TargetGender"]! == 2 && (int)stop["ShinyTarget"]! == 4 && stop["StopOnForm"] == null
              && (string)stop["TargetNatures"]! == "Jolly", $"SysBot gets the first target (square-shiny female Eevee), other settings kept: {stop.ToJsonString()}");
        AppSettings.Current.For("sw_sword").StopTargets = new();
        HuntService.PrepareSysBotConfig(cfg, "192.0.2.1", 6000, 1, sword);
        stop = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(cfg))!["StopConditions"]!;
        Check((int)stop["StopOnSpecies"]! == 0 && (int)stop["ShinyTarget"]! == 2, "no targets: SysBot stops on any shiny");

        AppSettings.Current.For("sw_frlg_lg").StopTargets = new() { new StopTarget { Species = 386, Shiny = "shiny" }, new StopTarget { Species = 201, Form = 3 } };
        var bot = Path.Combine(work, "pybot"); Directory.CreateDirectory(bot);
        HuntService.WriteBotOverrides(bot, lg);
        var py = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(bot, "shinyforge.json")))!["stop_rules"]!["targets"]!.AsArray();
        Check(py.Count == 2 && (int)py[0]!["species_index"]! == 410 && (string)py[0]!["shiny"]! == "shiny" && (int)py[1]!["species_index"]! == 201 && (int)py[1]!["form"]! == 3,
            $"Python hunter gets the targets with Gen 3 internal numbers (Deoxys = 410): {py.ToJsonString()}");
    }
    finally { AppSettings.Current.For("sw_sword").StopTargets = keepSword; AppSettings.Current.For("sw_frlg_lg").StopTargets = keepLg; }

    if (tests.Contains("debug"))
        foreach (var (g, sp, form) in new[] { (sword, (ushort)888, (byte)0), (sword, (ushort)52, (byte)1), (sword, (ushort)52, (byte)2), (lg, (ushort)201, (byte)5) })
        {
            var pk = PKHeX.Core.EntityBlank.GetBlank(DexData.Context(g), PKHeX.Core.LanguageID.English);
            pk.Species = sp; pk.Form = form;
            var encs = PKHeX.Core.EncounterMovesetGenerator.GenerateEncounters(pk, ReadOnlyMemory<ushort>.Empty, g.Versions).Take(12).ToList();
            Console.WriteLine($"   {g.Title} #{sp} form {form}: {encs.Count} encounters: " + string.Join("; ", encs.Select(e => $"{e.GetType().Name} shiny={e.Shiny} form={e.Form}")));
            var o = StopTargets.Available(g).FirstOrDefault(x => x.Species == sp);
            Console.WriteLine($"      option: forms=[{string.Join(", ", o?.Forms ?? Array.Empty<string>())}] shinyPossible={o?.ShinyPossible}");
        }
}

if (tests.Contains("sysbotproto"))
{
    // Offline: a fake sys-botbase on localhost answers like 2.x (error lines + blank line) and 3.x ("0"); the session must
    // stay in step and never wait for a blank line that doesn't come.
    var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
    listener.Start();
    var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var server = Task.Run(() =>
    {
        using var c = listener.AcceptTcpClient();
        using var st = c.GetStream();
        using var r = new StreamReader(st);
        void W(string s) { var b = System.Text.Encoding.ASCII.GetBytes(s); st.Write(b, 0, b.Length); }
        var peeks = 0;
        string? line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.StartsWith("peek")) W(++peeks switch
            {
                1 => "svcDebugActiveProcess: 264705\n\n",       // 2.x failure
                2 => "0\n",                                     // 3.x failure
                _ => "0A0B0C0D\n",
            });
            else if (line == "getTitleID") W("pmdmntGetApplicationProcessId: 527\nsvcDebugActiveProcess: 264705\npmdmntGetApplicationProcessId: 527\n\n");
            else if (line == "getVersion") { } // a sys-botbase too old to know it: no answer at all
        }
    });
    var sw = System.Diagnostics.Stopwatch.StartNew();
    AppSettings.Current.SwitchConnection = "WiFi";
    using (var bot = new SysBotSession(new WifiLink("127.0.0.1", port), controller: false))
    {
        Check(bot.Peek(0x100, 4) == null, "2.x failed read → null");
        Check(bot.Peek(0x100, 4) == null, "3.x failed read (\"0\") → null");
        Check(bot.Peek(0x100, 4) is { } ok && ok[0] == 0x0A && ok[3] == 0x0D, "next read is in step (0A0B0C0D)");
        Check(bot.TitleId() == null, "2.x \"nothing running\" (3 error lines + blank) → null");
        Check(bot.Version(1500) == null, "getVersion with no answer gives up instead of hanging");
    }
    Check(sw.Elapsed.TotalSeconds < 8, $"no 15 s waits ({sw.Elapsed.TotalSeconds:F1} s)");
    listener.Stop();
}

if (tests.Contains("sbbrestore"))
{
    // REAL HARDWARE, writes the SD card: SelfTest <work> sbbrestore - the FTP app must run; puts the newest backed-up
    // sys-botbase back (and saves sys-botbase-cpp's log.txt next to it).
    var s = AppSettings.Current;
    var backup = SysBotBaseInstaller.LatestBackup();
    Check(backup != null, $"a backup to restore ({backup})");
    if (backup != null)
    {
        SysBotBaseInstaller.Restore(s.SwitchIP, backup, m => Console.WriteLine("   " + m));
        using var ftp = ConsoleFtp.Connect(s.SwitchIP, ConsoleSdService.FindPort(s.SwitchIP)!.Value);
        var size = ftp.Size("/atmosphere/contents/430000000000000B/exefs.nsp");
        Check(size == new FileInfo(Path.Combine(backup, "exefs.nsp")).Length, $"the old exefs.nsp is back ({size} bytes)");
        Console.WriteLine($"   log: {(File.Exists(Path.Combine(backup, "sys-botbase-cpp log.txt")) ? Path.Combine(backup, "sys-botbase-cpp log.txt") : "none")}");
    }
}

if (tests.Contains("sbbinstall"))
{
    // REAL HARDWARE, writes the SD card: SelfTest <work> sbbinstall - installs sys-botbase-cpp (WiFi mode) over the FTP app,
    // backing up the old files, and copies Atmosphère's crash / fatal reports into <work>\crash for reading.
    ConsoleFtpLauncher.Decode = ShinyForge.UI.GdiImage.Decode;
    var s = AppSettings.Current;
    Console.WriteLine($"   before: sys-botbase {SysBotBaseInstaller.InstalledVersion(s.SwitchIP, s.SwitchPort) ?? "?"}");
    var backup = SysBotBaseInstaller.Install(s.SwitchIP, s.SwitchPort, "wifi", m => Console.WriteLine("   " + m)).GetAwaiter().GetResult();
    Check(File.Exists(Path.Combine(backup, "exefs.nsp")), $"old sys-botbase backed up ({backup})");
    var port = ConsoleSdService.FindPort(s.SwitchIP);
    if (port != null)
    {
        using var ftp = ConsoleFtp.Connect(s.SwitchIP, port.Value);
        var nsp = ftp.Size("/atmosphere/contents/430000000000000B/exefs.nsp");
        Check(nsp == new FileInfo(Directory.GetFiles(Path.Combine(AppPaths.ToolsDir, "sys-botbase-cpp"), "exefs.nsp", SearchOption.AllDirectories)[0]).Length,
            $"new exefs.nsp on the SD card ({nsp} bytes)");
        foreach (var dir in new[] { "/atmosphere/crash_reports", "/atmosphere/fatal_errors", "/atmosphere/erpt_reports" })
        {
            List<FtpEntry> list;
            try { list = ftp.List(dir); } catch (FtpException) { continue; }
            foreach (var e in list.Where(e => !e.IsDir).OrderByDescending(e => e.ModifiedUtc ?? DateTime.MinValue).ThenByDescending(e => e.Name).Take(6))
            {
                ftp.Download($"{dir}/{e.Name}", Path.Combine(work, "crash", dir.Trim('/').Replace('/', '_'), e.Name));
                Console.WriteLine($"   {dir}/{e.Name}  {e.Size} bytes  {e.ModifiedUtc}");
            }
        }
    }
}

if (tests.Contains("switchopen"))
{
    // REAL HARDWARE: SelfTest <work> switchopen <game id> - opens the game the way ShinyForge does (FRLG: save loaded).
    ConsoleFtpLauncher.Decode = ShinyForge.UI.GdiImage.Decode;
    var s = AppSettings.Current;
    var game = GameCatalog.Get(args.Skip(1).First(a => a != "switchopen" && GameCatalog.All.Any(g => g.Id == a)));
    var t = SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, game, m => Console.WriteLine("   " + m));
    Check(SwitchGameLauncher.TitleIds(game).Contains(t), $"{game.Title} open ({t})");
}

if (tests.Contains("switchswap"))
{
    // REAL HARDWARE, closes and starts games: SelfTest <work> switchswap <game A> <game B> - what Record Gameplay does before
    // recording B while A runs: A is opened, then closed and B started; then B is suspended (HOME) and brought back on screen.
    ConsoleFtpLauncher.Decode = ShinyForge.UI.GdiImage.Decode;
    var s = AppSettings.Current;
    var pick = args.Skip(1).Where(a => GameCatalog.All.Any(g => g.Id == a)).Select(GameCatalog.Get).ToList();
    var (a, b) = (pick[0], pick[1]);
    RgbImage Screen(SysBotSession bot, string name)
    {
        var jpg = bot.Screenshot() ?? throw new IOException("no screenshot");
        File.WriteAllBytes(Path.Combine(work, name + ".jpg"), jpg);
        return ConsoleFtpLauncher.Decode!(jpg)!;
    }
    string? Try(Func<string> f) { try { return f(); } catch (Exception e) { Console.WriteLine("   " + e.Message); return null; } }
    void Say(string m) => Console.WriteLine("   " + m);

    var ta = Try(() => SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, a, Say));
    Check(ta != null && SwitchGameLauncher.TitleIds(a).Contains(ta), $"{a.Title} open ({ta})");
    using (var bot = new SysBotSession(s.SwitchIP, s.SwitchPort))
        Check(!SwitchGameLauncher.IsHomeMenu(Screen(bot, "swap_a")), $"{a.Title} on screen, not the HOME menu (swap_a.jpg)");
    Check(Try(() => SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, b, Say)) == null, $"without closeOtherGame, {a.Title} is left alone");
    var save = tests.Contains("save") ? SwitchGameLauncher.OtherGame.SaveAndClose : SwitchGameLauncher.OtherGame.Close;
    var tb = Try(() => SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, b, Say, default, save));
    Check(tb != null && SwitchGameLauncher.TitleIds(b).Contains(tb), $"{a.Title} {(save == SwitchGameLauncher.OtherGame.SaveAndClose ? "saved and " : "")}closed, {b.Title} started ({tb})");
    using (var bot = new SysBotSession(s.SwitchIP, s.SwitchPort))
    {
        Thread.Sleep(3000);
        Check(!SwitchGameLauncher.IsHomeMenu(Screen(bot, "swap_b")), $"{b.Title} on screen (swap_b.jpg)");
        bot.Tap("HOME", 2000);
        Check(SwitchGameLauncher.IsHomeMenu(Screen(bot, "swap_home")), "HOME menu recognised (swap_home.jpg)");
    }
    var again = Try(() => SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, b, Say));
    using (var bot = new SysBotSession(s.SwitchIP, s.SwitchPort))
        Check(again == tb && !SwitchGameLauncher.IsHomeMenu(Screen(bot, "swap_front")), $"suspended {b.Title} brought back on screen (swap_front.jpg)");
}

if (tests.Contains("switchgame"))
{
    // REAL HARDWARE: FTP app → read the LeafGreen backups → close the FTP app → open LeafGreen → save loaded → (hunt: the Python
    // hunter logs an encounter, then it's stopped). The game must be closed (or the FTP app already running) at the start.
    ConsoleFtpLauncher.Decode = ShinyForge.UI.GdiImage.Decode;
    var s = AppSettings.Current;
    var lg = GameCatalog.Get("sw_frlg_lg");
    var port = ConsoleSdService.FindPort(s.SwitchIP);
    if (port == null)
        try { port = ConsoleFtpLauncher.StartFtpApp(s.SwitchIP, s.SwitchPort, m => Console.WriteLine("   " + m)); }
        catch (Exception e) { Console.WriteLine("   " + e.Message); }
    Check(port != null, $"FTP app running on the Switch (port {port})");
    if (port != null)
    {
        var m = ConsoleSdService.Mirror(lg, s.SwitchIP, port.Value, null, Path.Combine(work, "switch_sd"));
        Check(CheckpointService.FindBackups(lg, new[] { m.Root }).Count > 0, $"LeafGreen backups read over FTP ({m.Files} files)");
    }
    string? title = null;
    try { title = SwitchGameLauncher.OpenGame(s.SwitchIP, s.SwitchPort, lg, m => Console.WriteLine("   " + m)); }
    catch (Exception e) { Console.WriteLine("   " + e.Message); }
    Check(title != null && SwitchGameLauncher.TitleIds(lg).Contains(title), $"LeafGreen opened from the Homebrew Menu / FTP app ({title})");
    Check(ConsoleSdService.FindPort(s.SwitchIP) == null, "the FTP app is closed");
    if (title != null)
    {
        using var live = new LiveFrlg(s.SwitchIP, s.SwitchPort);
        var r = live.Read();
        Check(r != null, $"the save is loaded (trainer {r?.Trainer}, {r?.Party.Count} in the party)");
    }
    if (tests.Contains("hunt") && title != null)
    {
        var csv = Path.Combine(HuntService.BotDir!, "encounters.csv");
        int Lines() => File.Exists(csv) ? File.ReadAllLines(csv).Length : 0;
        var before = Lines();
        HuntService.Start(lg, "python");
        var until = DateTime.Now.AddSeconds(150);
        while (DateTime.Now < until && Lines() <= before) Thread.Sleep(2000);
        var after = Lines();
        Check(after > before, $"the hunter started from there and logged an encounter ({after - before} new)" +
              (after > before ? ": " + File.ReadAllLines(csv).Last().Split(',').Skip(3).FirstOrDefault() : ""));
        HuntService.StopPythonHunt();
    }
}

if (tests.Contains("sheetsdl"))
{
    // The published tracker list and a real download (the smallest tracker) into a test folder.
    var entries = await SheetsDownloadService.ManifestAsync();
    Check(entries.Count >= 38, $"tracker list on GitHub: {entries.Count} trackers, {entries.Sum(e => e.Size) / 1048576} MB");
    var missingGames = GameCatalog.All.Where(g => !string.IsNullOrWhiteSpace(g.Spreadsheet) && SheetsDownloadService.EntryFor(g, entries) == null).Select(g => g.Id).ToList();
    Check(missingGames.Count == 0, "every game with a tracker name has a published tracker" + (missingGames.Count > 0 ? ": missing " + string.Join(", ", missingGames) : ""));
    var smallest = entries.OrderBy(e => e.Size).First();
    var root = Path.Combine(work, "sheets_dl");
    if (Directory.Exists(root)) Directory.Delete(root, true);
    var path = await SheetsDownloadService.DownloadAsync(smallest, _ => { }, root);
    Check(File.Exists(path) && new FileInfo(path).Length == smallest.Size, $"downloaded and checksum-verified: {Path.GetRelativePath(root, path)} ({smallest.Size / 1024} KB)");
    File.WriteAllText(path, "my progress");
    var again = await SheetsDownloadService.DownloadAsync(smallest, _ => { }, root);
    Check(File.ReadAllText(again) == "my progress", "an existing tracker is never overwritten");
}

if (tests.Contains("updatelive"))
{
    var rel = await AppUpdateService.CheckAsync();
    Console.WriteLine($"   GitHub latest: {rel?.Tag} ({rel?.VersionText}), program zip {rel?.ZipSize / 1048576} MB, page {rel?.PageUrl}");
    Check(rel != null && rel.ZipUrl != null, "latest release found on GitHub with the program download");
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

    // ---- self-diagnosis, minor (non-gameplay) problems, GitHub error reports
    {
        HealthItem H(string f, HealthState st, string m, string? det = null) => new(f, st, m, DateTime.Now, det);
        Check(H("Python hunter", HealthState.Warning, "x").AffectsGameplay && H("3DS hunt (X)", HealthState.Warning, "x").AffectsGameplay
              && H("Program", HealthState.Error, "x").AffectsGameplay && H("Keep awake", HealthState.Error, "x").AffectsGameplay,
            "hunting / crashes / sleep problems count as gameplay problems");
        Check(!H("Overlay / remote", HealthState.Error, "x").AffectsGameplay && !H("Shiny clips (X)", HealthState.Warning, "x").AffectsGameplay
              && !H("Recording (X)", HealthState.Error, "x").AffectsGameplay && !H("Program", HealthState.Warning, "x").AffectsGameplay
              && !H("Backups", HealthState.Warning, "x").AffectsGameplay && !H("Disk space", HealthState.Warning, "x").AffectsGameplay,
            "overlay / clips / recordings / backups / background hiccups are minor");

        HealthItem? announced = null;
        Action<HealthItem> grab = i => announced ??= i;
        HealthMonitor.Problem += grab;
        var issuesBefore = File.Exists(ErrorDoctor.IssuesLog) ? new FileInfo(ErrorDoctor.IssuesLog).Length : 0;
        HealthMonitor.Report("Overlay / remote", HealthState.Error, "doctor-test: port 8765 in use");
        Check(announced == null, "a minor problem is not announced (no banner / sound / phone alert)");
        Check(HealthMonitor.MinorItems.Any(i => i.Message.Contains("doctor-test")) && !HealthMonitor.Items.Any(i => i.Message.Contains("doctor-test")),
            "a minor problem is listed only under minor issues");
        Check(File.Exists(ErrorDoctor.IssuesLog) && new FileInfo(ErrorDoctor.IssuesLog).Length > issuesBefore
              && ErrorDoctor.QuietLogLines(5).Any(l => l.Contains("doctor-test")), "a minor problem is written to issues.log");
        HealthMonitor.Report("Python hunter", HealthState.Warning, "doctor-test: Bot: the game did not reset");
        Check(announced?.Message.Contains("doctor-test") == true, "a gameplay problem is announced");
        HealthMonitor.Problem -= grab;
        HealthMonitor.Clear("Overlay / remote");
        HealthMonitor.Clear("Python hunter");

        // every known problem gets a cause and steps; the ones ShinyForge can fix offer a fix
        var known = new[]
        {
            H("Disk space", HealthState.Error, "Only 0.5 GB free on C:\\"), H("Program files", HealthState.Error, "Pokédex data is missing"),
            H("Settings / data folder", HealthState.Error, "Can't write"), H("Keep awake", HealthState.Error, "The PC went to sleep"),
            H("Python hunter", HealthState.Error, "Can't reach the Switch at 1.2.3.4"), H("Python hunter", HealthState.Warning, "The Python hunter has stopped."),
            H("Python hunter", HealthState.Error, "The same Deoxys (PID 1) was logged 3 times"), H("Python hunter", HealthState.Warning, "No new encounter for 9 minutes"),
            H("3DS hunt (X)", HealthState.Error, "Lost the connection to the 3DS"), H("Recording (X)", HealthState.Error, "ffmpeg exited"),
            H("Collection tracker (X)", HealthState.Warning, "Last sync failed"), H("Shiny clips (X)", HealthState.Warning, "clip"),
            H("Overlay / remote", HealthState.Error, "port"), H("Backups", HealthState.Warning, "backup"), H("Program", HealthState.Error, "Unexpected error: boom"),
        };
        Check(known.All(k => ErrorDoctor.Diagnose(k) is { Cause.Length: > 10, Steps.Count: > 0 }), $"all {known.Length} known problems are diagnosed with steps");
        var withFix = new[] { 0, 1, 2, 4, 5, 6, 7, 8, 12, 14 };
        Check(withFix.All(n => ErrorDoctor.Diagnose(known[n]).Fix != null) && ErrorDoctor.Diagnose(known[3]).Fix == null,
            "fixable problems offer an automatic fix");
        var repaired = await ErrorDoctor.Diagnose(known[2]).Fix!();
        Check(repaired.Contains("can be written"), "data-folder repair fix runs: " + repaired);

        // the GitHub report: no personal details, fits in a link
        var home = OsInfo.Home;
        var secret = $"Crash in {Path.Combine(home, "Documents", "save.sav")} by {Environment.UserName} on {Environment.MachineName} " +
                     "from 192.168.0.16, mail someone@example.com, mac 01:23:45:67:89:ab";
        var clean = ErrorDoctor.Sanitize(secret);
        Check(!clean.Contains(home, StringComparison.OrdinalIgnoreCase) && !clean.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)
              && !clean.Contains(Environment.MachineName, StringComparison.OrdinalIgnoreCase) && !clean.Contains("192.168.0.16")
              && !clean.Contains("someone@example.com") && !clean.Contains("01:23:45") && clean.Contains("<ip>") && clean.Contains("~"),
            "report text is sanitized: " + clean);
        var crash = H("Program", HealthState.Error, "Unexpected error: " + secret, string.Concat(Enumerable.Repeat("   at ShinyForge.Something.Method() in " + home + "\\x.cs:line 1\n", 400)));
        var url = ErrorDoctor.ReportUrl(crash, ErrorDoctor.Diagnose(crash), "You already have the latest version.", "I pressed Start hunt");
        var decoded = Uri.UnescapeDataString(url);
        Check(url.StartsWith($"https://github.com/{AppInfo.Repo}/issues/new?title=") && url.Length <= 8000, $"report link fits GitHub's limit ({url.Length} chars)");
        Check(decoded.Contains(ErrorDoctor.ReportTitlePrefix) && decoded.Contains("ShinyForge's diagnosis") && decoded.Contains(AppInfo.Display)
              && decoded.Contains("I pressed Start hunt") && decoded.Contains("latest version"), "report has the title tag, diagnosis, version, user note and fix result");
        Check(!decoded.Contains(home, StringComparison.OrdinalIgnoreCase) && !decoded.Contains(Environment.UserName, StringComparison.OrdinalIgnoreCase)
              && !decoded.Contains("192.168.0.16"), "report link has no personal details");
        Check(ErrorDoctor.ReportPrompt(crash, ErrorDoctor.Diagnose(crash), null).Contains("Nothing is sent until you press"), "the user confirms before anything opens");
    }

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
    InitUi();
    var win = new Form { Text = "ShinyForgeHealthTest", Size = new Size(320, 240), StartPosition = FormStartPosition.Manual, Location = new Point(50, 50), BackColor = Color.SteelBlue };
    win.Show(); Application.DoEvents();
    var recDir = Path.Combine(work, "health_rec");
    if (Directory.Exists(recDir)) Directory.Delete(recDir, true);
    var rec = new RecordingService();
    rec.Start(RecordingService.WindowPrefix + "ShinyForgeHealthTest", RecordingService.NoAudio, recDir, 1, 60);
    for (int i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(100); }
    Check(rec.CheckHealth() == null, "healthy recording -> nothing to report");
    // kill only this test's own FFmpeg - never the user's running recordings or shiny-clip buffer
    var ownFfmpeg = (System.Diagnostics.Process?)typeof(RecordingService).GetField("_ffmpeg", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(rec);
    Check(ownFfmpeg is { HasExited: false }, "test recording has its own FFmpeg process");
    try { ownFfmpeg?.Kill(); } catch { }
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
    InitUi();
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

if (tests.Contains("gameslist"))
{
    // The game lists must end at the last game: the scroll range = the content, no empty screens below it.
    InitUi();
    foreach (var sw in new[] { true, false })
    {
        // like the main window: the page is built small, then added to a window that's already open, then scrolled to the end
        var f = new Form { Size = new Size(1320, 900), StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        var content = new Panel { Dock = DockStyle.Fill };
        f.Controls.Add(content);
        f.Show(); for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
        var page = new GamesPage(sw);
        content.Controls.Add(page);
        for (var i = 0; i < 20; i++) { Application.DoEvents(); Thread.Sleep(30); }
        var listPanel = page.Controls.OfType<Panel>().First().Controls[0];
        var head = listPanel.Controls.OfType<Label>().Skip(2).First();
        var firstTile = listPanel.Controls.OfType<FlowLayoutPanel>().First().Controls.OfType<GameTile>().First();
        var tileTop = firstTile.Top + firstTile.Parent!.Top;
        Console.WriteLine($"   header {head.Bounds}, first tile at y {tileTop}");
        Check(tileTop - head.Bottom < 40, $"{(sw ? "Switch" : "3DS")} list: the tiles start right under their console header (gap {tileTop - head.Bottom})");
        page.ScrollTo(int.MaxValue); // scroll as far down as it goes
        for (var i = 0; i < 5; i++) { Application.DoEvents(); Thread.Sleep(30); }
        var view = page.Controls.OfType<Panel>().First();
        var tiles = view.Controls[0].Controls.OfType<FlowLayoutPanel>().SelectMany(f => f.Controls.OfType<GameTile>()).ToList();
        var lastTile = tiles.Last();
        int Bottom() => lastTile.Bottom + lastTile.Parent!.Top + lastTile.Parent!.Parent!.Top;
        var bottom = Bottom();
        Console.WriteLine($"   tiles: {tiles.Count}, scrolled {page.Offset} of {page.MaxOffset}, last tile bottom {bottom} of {view.ClientSize.Height}");
        Check(bottom <= view.ClientSize.Height && (page.MaxOffset == 0 || bottom > view.ClientSize.Height - 120), // fits = nothing to scroll
            $"{(sw ? "Switch" : "3DS")} list scrolled to the end: the last game is the last thing on screen (bottom {bottom} of {view.ClientSize.Height})");
        f.WindowState = FormWindowState.Maximized; for (var i = 0; i < 10; i++) { Application.DoEvents(); Thread.Sleep(30); }
        page.ScrollTo(int.MaxValue);
        bottom = Bottom();
        Check(bottom <= view.ClientSize.Height + 1, $"{(sw ? "Switch" : "3DS")} list after maximizing: still ends at the last game (bottom {bottom} of {view.ClientSize.Height})");
        f.Close();
    }
}

if (tests.Contains("featuresui"))
{
    InitUi();
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
    Snap(new HuntOptionsForm(GameCatalog.Get("sw_frlg_lg"), true), "hunt_options");
    Snap(new HuntOptionsForm(GameCatalog.Get("sw_sword"), false, targetsOnly: true), "stop_targets_sword");
    Snap(new HuntStatsForm("Python"), "hunt_stats", 2000);
    Snap(new SettingsForm(), "settings");
    Snap(new BackupsForm(), "backups");
    Snap(new LivingDexForm(), "living_dex", 40000);
    Snap(new GamePage(GameCatalog.Get("sw_frlg_lg")) is var gp ? new Form { Size = new Size(1320, 1200), Controls = { gp } } : null!, "game_page_frlg", 1500);
}

if (tests.Contains("newui"))
{
    InitUi();
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
    // In memory only (never saved): skip the first-run questions so the window can be drawn.
    AppSettings.Current.PokeldnSetupDone = true;
    var keys = Path.Combine(work, "prod.keys");
    File.WriteAllText(keys, string.Join("\n", PokeldnService.KeysNeeded.Select(k => k + " = 00")));
    AppSettings.Current.ProdKeysPath = keys;
    Snap(new PokeldnForm(), "pokeldn", 15000);
    var desk = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    var us = Path.Combine(desk, @"RoCs-PC-master\RoCs-PC-master\25 - Gen VII - USUM Collection\Save Data\20180228134249 - Ultra Sun\00001b50\main");
    if (PKHeX.Core.SaveUtil.TryGetSaveFile(us, out var usSav) && usSav != null)
    {
        var fake = new List<CheckpointBackup>
        {
            new(@"E:\3ds\Checkpoint\saves\0x01B50 Pokémon Ultra Sun\20261110-090000", @"E:\3ds\Checkpoint\saves\0x01B50 Pokémon Ultra Sun", "20261110-090000", new DateTime(2026, 11, 10, 9, 0, 0), new FoundSave(us, usSav)),
            new(@"E:\3ds\Checkpoint\saves\0x01B50 Pokémon Ultra Sun\20261101-120000", @"E:\3ds\Checkpoint\saves\0x01B50 Pokémon Ultra Sun", "20261101-120000", new DateTime(2026, 11, 1, 12, 0, 0), new FoundSave(us, usSav)),
        };
        Snap(new CheckpointPicker("Ultra Sun", fake), "checkpoint_picker");
    }
    Snap(new GamePage(GameCatalog.Get("sw_shield")) is var gp ? new Form { Size = new Size(1320, 1200), Controls = { gp } } : null!, "game_page_shield", 1500);
}

if (tests.Contains("healthui"))
{
    InitUi();
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
    InitUi();
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

if (tests.Contains("livingdex"))
{
    // Renders the Living Dex window (reads the real saves set up in ShinyForge - only reads) with a few searches selected.
    InitUi();
    var outDir = Path.Combine(work, "screens");
    Directory.CreateDirectory(outDir);
    var f = new LivingDexForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
    f.Show();
    T Field<T>(string n) => (T)typeof(LivingDexForm).GetField(n, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(f)!;
    var status = Field<Label>("_status");
    var until = DateTime.Now.AddSeconds(240);
    while (DateTime.Now < until && !(status.Text.StartsWith("Read ") || status.Text.StartsWith("No saves"))) { Application.DoEvents(); Thread.Sleep(50); }
    Console.WriteLine("   " + status.Text);
    Console.WriteLine("   " + Field<Label>("_counts").Text);
    var grid = Field<DexGrid>("_grid");
    var show = typeof(LivingDexForm).GetMethod("ShowInfo", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
    foreach (var (q, name) in new[] { ("", "ld_all"), ("Unown", "ld_unown"), ("Rattata", "ld_rattata"), ("Alcremie", "ld_alcremie"), ("Pikachu", "ld_pikachu"), ("Pidgeot", "ld_pidgeot") })
    {
        Field<TextBox>("_search").Text = q;
        Application.DoEvents();
        var items = (System.Collections.IList)typeof(DexGrid).GetField("_items", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(grid)!;
        if (items.Count > 0)
        {
            var first = ((ValueTuple<DexEntry, DexState>)items[Math.Min(1, items.Count - 1)]!).Item1;
            show.Invoke(f, new object[] { first });
        }
        for (var i = 0; i < 40; i++) { Application.DoEvents(); Thread.Sleep(50); }
        using var bmp = new Bitmap(f.Width, f.Height);
        f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(Path.Combine(outDir, name + ".png"));
        Console.WriteLine($"   rendered {name} ({items.Count} entries)");
    }
    f.Close();
}

if (tests.Contains("installall"))
{
    // "Install all missing" in Tools & Updates installs every tool the window shows as "Not installed" - also pokeldn before
    // the wireless-trades setup - while the startup banner still only offers what ShinyForge needs now.
    var s = AppSettings.Current;
    var setup = s.PokeldnSetupDone;
    s.PokeldnSetupDone = false; // in memory only
    var shown = DependencyService.MissingTools(true, everything: true).Select(d => d.Name).ToList();
    var needed = DependencyService.MissingTools(true, everything: false).Select(d => d.Name).ToList();
    Console.WriteLine($"   not installed (window): {string.Join(", ", shown)}   needed now: {string.Join(", ", needed)}");
    var rows = DependencyService.All.Where(d => d.ForSwitch && d.State == DepState.Missing).Select(d => d.Name).ToList();
    Check(shown.SequenceEqual(rows), "Install all covers every \"Not installed\" row");
    Check(!needed.Contains("pokeldn"), "startup banner doesn't push pokeldn before its setup");
    if (shown.Contains("pokeldn"))
    {
        var msg = await DependencyService.InstallAllMissing(true, everything: true, p => Console.WriteLine("   " + p));
        Console.WriteLine("   " + msg.Replace(Environment.NewLine, Environment.NewLine + "   "));
        var pk = DependencyService.All.First(d => d.Name == "pokeldn");
        DependencyService.Refresh(pk);
        Check(pk.State != DepState.Missing, $"pokeldn installed ({pk.Path})");
    }
    s.PokeldnSetupDone = setup;
    InitUi();
    using var form = new ToolsForm(true);
    static IEnumerable<Control> Walk(Control c) => c.Controls.Cast<Control>().SelectMany(x => Walk(x).Prepend(x));
    var title = Walk(form).OfType<Label>().FirstOrDefault(l => l.Text == "Tools & Updates");
    Check(title is { UseMnemonic: false }, "title shows \"Tools & Updates\" (the & isn't eaten)");
}

if (tests.Contains("recheck"))
{
    // A release made while ShinyForge stays open (pokeldn shipped 3 versions overnight; the window opened the evening
    // before kept saying "up to date"). Drives the REAL main window and Tools & Updates with a fake pokeldn copy whose
    // version marker is moved (in the work folder only - the real pokeldn and settings aren't touched or saved).
    InitUi();
    var s = AppSettings.Current;
    var keep = (s.PokeldnPath, s.PokeldnSetupDone, s.CheckUpdatesOnStart, s.SkippedAppVersion);
    // SelfTest calls itself 1.0.0: the start-up check would offer the newest ShinyForge in a window - skip that version
    s.SkippedAppVersion = (await AppUpdateService.CheckAsync())?.Tag ?? "";
    var latest = await PokeldnService.LatestTagAsync();
    var fakeExe = Path.Combine(work, "pokeldn-open-overnight", "pokeldn.exe");
    Directory.CreateDirectory(Path.GetDirectoryName(fakeExe)!);
    File.WriteAllText(fakeExe, "test");
    var marker = Path.Combine(Path.GetDirectoryName(fakeExe)!, "shinyforge-version.txt");
    const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    void Age(TimeSpan by) => typeof(DependencyService).GetProperty(nameof(DependencyService.LastChecked))!.GetSetMethod(true)!
        .Invoke(null, new object[] { DateTime.Now - by });
    void Pump(Func<bool> done, int seconds)
    {
        var until = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < until && !done()) { Application.DoEvents(); Thread.Sleep(50); }
    }
    // Like the real app: every window and every await on ONE UI thread with the WinForms message loop.
    Exception? uiError = null;
    var ui = new Thread(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var body = UiBody();
        while (!body.IsCompleted) { Application.DoEvents(); Thread.Sleep(20); }
        uiError = body.Exception?.GetBaseException();
    });
    ui.SetApartmentState(ApartmentState.STA);
    ui.Start();
    ui.Join();
    Check(uiError == null, "the window test ran without errors " + uiError);

    // The newest release of every GitHub tool, read without GitHub's API (60 questions an hour - running out made every
    // check fail quietly and "No updates found" appear)
    foreach (var repo in new[] { PokeldnService.Repo, PokeldnService.EsptoolRepo, DependencyService.SysBotRepo, "kwsch/PKHeX",
                                 "PokemonAutomation/ComputerControl", SysBotBaseInstaller.Repo, AppInfo.Repo })
    {
        string? tag = null;
        try { tag = await WebClients.LatestGitHubTag(repo); } catch (Exception e) { Console.WriteLine("   " + e.Message); }
        Check(DependencyService.ParseVersion(tag) != null, $"{repo}: newest release {tag}");
    }
    // ShinyForge itself (SelfTest says it's 1.0.0, so the newest release is "newer"): offered even when the API is out of
    // questions, with a download link that really exists
    var app = await AppUpdateService.CheckAsync();
    Check(app is { IsNewer: true, ZipUrl: not null }, $"ShinyForge {app?.Tag} offered with its download ({app?.ZipUrl})");
    if (app?.ZipUrl != null)
    {
        using var http = new HttpClient();
        using var head = await http.SendAsync(new HttpRequestMessage(HttpMethod.Head, app.ZipUrl));
        Check(head.IsSuccessStatusCode, $"the ShinyForge download link works ({(int)head.StatusCode})");
    }
    // offline: the window says which tools couldn't be checked instead of "No updates found"
    Check(DependencyService.CheckFailures.Count == 0, "the last check reached every tool: " + string.Join(", ", DependencyService.CheckFailures));
    var failProp = typeof(DependencyService).GetProperty(nameof(DependencyService.CheckFailures))!;
    failProp.GetSetMethod(true)!.Invoke(null, new object[] { new List<string> { "pokeldn", "esptool" } });
    var said = DependencyService.CheckSummary(0);
    Console.WriteLine("   offline: " + said);
    Check(said.Contains("couldn't check pokeldn, esptool"), "a check that couldn't reach GitHub says so (not just 'No updates found')");
    failProp.GetSetMethod(true)!.Invoke(null, new object[] { new List<string>() });

    async Task UiBody()
    {
    MainForm? main = null;
    try
    {
        // like the real PC: pokeldn installed with "Install all missing", the wireless-trades setup never done
        s.PokeldnPath = fakeExe; s.PokeldnSetupDone = false; s.CheckUpdatesOnStart = true;
        File.WriteAllText(marker, latest); // evening: the newest pokeldn is installed

        main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        main.Show();
        Pump(() => DependencyService.LastChecked > DateTime.Now.AddMinutes(-1), 240); // the start-up check
        var banner = (Panel)typeof(MainForm).GetField("_banner", Any)!.GetValue(main)!;
        string BannerText() => banner.Height == 0 ? "" : string.Join(" ", banner.Controls.OfType<Label>().Select(l => l.Text));
        Check(DependencyService.UpdateFor(DependencyService.All.First(d => d.Name == "pokeldn")) == null,
            $"start-up: pokeldn {latest} is current - no update (banner: '{BannerText()}')");
        var timer = (System.Windows.Forms.Timer)typeof(MainForm).GetField("_recheck", Any)!.GetValue(main)!;
        Check(timer.Enabled && timer.Interval == 5 * 60 * 1000, "the main window keeps checking while it's open (looks every 5 minutes)");

        // overnight: pokeldn publishes a newer version (= the installed one is now older than GitHub's)
        File.WriteAllText(marker, "v0.1.0");
        banner.Height = 0;
        async Task Tick() => await (Task)typeof(MainForm).GetMethod("RecheckInBackground", Any)!.Invoke(main, null)!;
        await Tick();
        Check(banner.Height == 0, "a check less than an hour old isn't repeated (no GitHub call every 5 minutes)");
        // an hour later: the window's OWN timer fires (made quick here) and checks again
        Age(TimeSpan.FromMinutes(61));
        timer.Interval = 300;
        Pump(() => banner.Height > 0 || !DependencyService.CheckIsOlderThan(TimeSpan.FromMinutes(1)), 240);
        Pump(() => banner.Height > 0, 5);
        timer.Interval = 5 * 60 * 1000;
        Console.WriteLine($"   banner: {BannerText()}");
        Check(banner.Height > 0 && BannerText().Contains("pokeldn"), "an hour later the open window finds the new pokeldn and says so in the banner");
        var pk = DependencyService.All.First(d => d.Name == "pokeldn");
        Check(pk.State == DepState.UpdateAvailable && DependencyService.UpdateFor(pk)?.Message.Contains(latest) == true,
            $"pokeldn's row: 'Update available' ({DependencyService.UpdateFor(pk)?.Message})");
        banner.Height = 0;
        Age(TimeSpan.FromMinutes(61));
        await Tick();
        Check(banner.Height == 0, "the same update isn't announced again every hour after the banner was closed");

        // Tools & Updates opened after a while: it asks again by itself (no button press)
        File.WriteAllText(marker, latest);
        await DependencyService.CheckUpdates();
        File.WriteAllText(marker, "v0.1.0");
        Age(TimeSpan.FromMinutes(11));
        using (var tools = new ToolsForm(true) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false })
        {
            var status = (Label)typeof(ToolsForm).GetField("_status", Any)!.GetValue(tools)!;
            tools.Show();
            Pump(() => status.Text.Contains("update(s) available") || status.Text.StartsWith("No updates"), 240);
            Console.WriteLine($"   Tools & Updates: {status.Text}");
            Check(DependencyService.UpdateFor(pk) != null && pk.State == DepState.UpdateAvailable,
                "Tools & Updates opened with an old answer: checks again and shows pokeldn's update");
            tools.Close();
        }
        // ... but not when the answer is fresh
        var before = DependencyService.LastChecked;
        using (var tools = new ToolsForm(true) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false })
        {
            tools.Show(); Pump(() => false, 3);
            Check(DependencyService.LastChecked == before, "a fresh answer is shown as it is (no new check)");
            tools.Close();
        }

        // two checks at once (hourly + the button) give one consistent answer
        var both = await Task.WhenAll(DependencyService.CheckUpdates(), DependencyService.CheckUpdates());
        Check(both[0].Any(u => u.Item == "pokeldn") && both[1].Any(u => u.Item == "pokeldn") && DependencyService.UpdateFor(pk) != null,
            "two checks at the same time: both find pokeldn, the row keeps it");
    }
    finally
    {
        main?.Close();
        (s.PokeldnPath, s.PokeldnSetupDone, s.CheckUpdatesOnStart, s.SkippedAppVersion) = keep;
        foreach (var d in DependencyService.All) d.Version = null;
        await DependencyService.CheckUpdates();
    }
    }
}

Console.WriteLine(failures == 0 ? "ALL CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
return failures;
