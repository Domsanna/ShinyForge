using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Gets the Switch into the right game so the hunt can start (sys-botbase + screenshots only):
///   1. closes the FTP app / Homebrew Menu if it's open (never a game the user plays - that could lose progress),
///   2. HOME menu: puts the cursor on the FIRST game and checks it on a screenshot (the starting point),
///   3. looks for the game by its icon: along the HOME row first, then - if it isn't there - opens "All Software" at the
///      end of the row and goes through every game there. The selected tile is found by its cyan frame and compared with
///      the game's official icons (Assets\SwitchIcons\&lt;title ID&gt;.jpg, every language); nothing is started until the
///      right game is selected,
///   4. starts it; if the Switch asks "Who is using the software?", ShinyForge asks the user which one (<see cref="ChooseUser"/>),
///   5. checks the running title ID, and for FireRed / LeafGreen presses A through the title screen until the save is loaded.
/// </summary>
public static class SwitchGameLauncher
{
    /// <summary>
    /// Asks the user which Switch user should play: (screenshot of "Who is using the software?" as JPEG, number of users)
    /// → 0-based user, or null to cancel. Set by each program (a window with the picture and one button per user).
    /// Not set (tests): the first user.
    /// </summary>
    public static Func<byte[], int, int?>? ChooseUser { get; set; }

    private const double IconLimit = 22;      // mean colour difference for "this is the game's icon" (right game ≈ 6-20, others ≥ 33)
    private const int AllSoftwarePitch = 184; // All Software grid: 174 px icons, 10 px apart, 6 per row
    private const int ScreenBottom = 647;     // the All Software grid ends above the button hints

    /// <summary>The game's Switch title IDs (all languages), or none when it isn't a Switch game.</summary>
    public static string[] TitleIds(Game game) =>
        game.IsSwitch && CheckpointService.TitleIds.TryGetValue(game.Id, out var ids) ? ids : Array.Empty<string>();

    /// <summary>What <see cref="OpenGame"/> does when another game is open: leave it (error), close it, or save it and close it.</summary>
    public enum OtherGame { Refuse, Close, SaveAndClose }

    /// <summary>The Pokémon game (ShinyForge's catalog) a Switch title ID belongs to, or null (not a Pokémon game).</summary>
    public static Game? GameOf(string titleId) => GameCatalog.ForSwitch.FirstOrDefault(g => TitleIds(g).Contains(titleId));

    /// <summary>"Pokémon Sword" for a Switch title ID ShinyForge knows, else "another game (title ID)".</summary>
    public static string GameName(string titleId) => GameOf(titleId) is { } g ? $"Pokémon {g.Title}" : $"another game ({titleId})";

    /// <summary>
    /// What runs on the Switch: (title ID or null, true when it's really the FTP app / Homebrew Menu). The Homebrew Menu and the
    /// apps it starts run under the borrowed game's title ID (that's how the R trick works): an FTP server answering means it's
    /// really ftpd / Checkpoint.
    /// </summary>
    public static (string? TitleId, bool Homebrew) Running(string host, int port)
    {
        using var bot = new SysBotSession(host, port, controller: false);
        var id = bot.TitleId();
        return (id, id != null && ConsoleSdService.FindPort(host) != null);
    }

    /// <summary>
    /// Closes the FTP app / Homebrew Menu (if open), finds <paramref name="game"/> on the HOME menu or in All Software, starts it
    /// and - for FireRed / LeafGreen - loads the save. If the game already runs it's brought back on screen (from the HOME menu).
    /// Another game running: <paramref name="otherGame"/> says whether it's left alone (an error), closed, or saved and closed.
    /// Returns the running title ID.
    /// </summary>
    public static string OpenGame(string host, int port, Game game, Action<string>? progress = null, CancellationToken ct = default,
                                  OtherGame otherGame = OtherGame.Refuse)
    {
        var ids = TitleIds(game);
        if (ids.Length == 0) throw new InvalidOperationException($"Pokémon {game.Title} isn't a Switch game ShinyForge can start.");
        if (ConsoleFtpLauncher.Decode == null) throw new InvalidOperationException("No image decoder is set up.");
        var icons = IconPrints(ids);
        if (icons.Count == 0) throw new InvalidOperationException($"The icons of Pokémon {game.Title} (Assets/SwitchIcons) are missing.");
        using var bot = new SysBotSession(host, port);
        var running = bot.TitleId();
        if (running != null)
        {
            var homebrew = ConsoleSdService.FindPort(host) != null;
            if (ids.Contains(running) && !homebrew) // the game itself already runs
            {
                ToFront(bot);
                return Ready(bot, game, running, progress, ct);
            }
            if (!homebrew && otherGame == OtherGame.Refuse)
                throw new IOException($"{GameName(running)} is open on the Switch. Save and close it, then try again.");
            if (!homebrew && otherGame == OtherGame.SaveAndClose)
            {
                progress?.Invoke($"Saving {GameName(running)}...");
                SaveGame(bot, running, ct);
            }
            progress?.Invoke(homebrew ? "Closing the FTP app / Homebrew Menu on the Switch..." : $"Closing {GameName(running)} on the Switch...");
            Close(bot);
        }

        progress?.Invoke($"Looking for Pokémon {game.Title} on the HOME menu...");
        bot.Tap("HOME", 1500);
        var (img, box) = StartAtFirstGame(bot);
        if (!FindOnHomeRow(bot, icons, ref img, ref box, ct))
        {
            progress?.Invoke($"Pokémon {game.Title} isn't on the HOME menu - looking in All Software...");
            if (!FindInAllSoftware(bot, icons, progress, ct))
            {
                bot.Tap("B", 800); bot.Tap("HOME", 1000);
                throw new IOException($"Pokémon {game.Title} wasn't found on the HOME menu or in All Software. Is it installed?");
            }
        }

        progress?.Invoke($"Starting Pokémon {game.Title}...");
        bot.Tap("A", 300);
        var started = WaitForStart(bot, ct);
        if (started == null) throw new IOException($"Pokémon {game.Title} didn't start - start it yourself.");
        if (!ids.Contains(started))
        {
            Close(bot);
            throw new IOException($"Another game started ({started}) - it was closed again. Start Pokémon {game.Title} yourself.");
        }
        return Ready(bot, game, started, progress, ct);
    }

    /// <summary>
    /// Closes whatever runs on the Switch (<paramref name="how"/> = Close or SaveAndClose; SaveAndClose only for games
    /// <see cref="CanSave"/> says ShinyForge can save). Used before the FTP app is started. Nothing running = nothing to do.
    /// </summary>
    public static void CloseGame(string host, int port, OtherGame how, Action<string>? progress = null, CancellationToken ct = default)
    {
        using var bot = new SysBotSession(host, port);
        if (bot.TitleId() is not { } running) return;
        if (how == OtherGame.SaveAndClose)
        {
            progress?.Invoke($"Saving {GameName(running)}...");
            SaveGame(bot, running, ct);
        }
        progress?.Invoke($"Closing {GameName(running)} on the Switch...");
        Close(bot);
    }

    private static string Ready(SysBotSession bot, Game game, string title, Action<string>? progress, CancellationToken ct)
    {
        AppPaths.Log($"Switch: {game.Title} running ({title})");
        if (game.Bot == BotKind.FrlgSwitch) LoadFrlgSave(bot, title, progress, ct);
        return title;
    }

    /// <summary>
    /// Closes the running software: on the HOME menu (checked on a screenshot - HOME on the HOME menu would go back INTO the
    /// software, where X and A are game buttons), X, then A on "Close". Checks that nothing runs any more.
    /// </summary>
    private static void Close(SysBotSession bot)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var home = HomeShown(bot);
            if (home == null) { Thread.Sleep(500); continue; } // no screenshot: pressing HOME blindly could open the game again
            if (home == false)
            {
                bot.Tap("HOME", 1500);
                if (HomeShown(bot) != true) continue;
            }
            bot.Tap("X", 1200);
            bot.Tap("A", 3500);
            if (bot.TitleId() == null) return;
        }
        throw new IOException("The software on the Switch didn't close - close it yourself (HOME, X, Close).");
    }

    // ------------------------------------------------------------------ saving the running game
    /// <summary>
    /// Can ShinyForge save this running game itself (before closing it)? Only where the save was checked on the real Switch:
    /// FireRed / LeafGreen (their GBA menus are read from screenshots). Other Pokémon games: the user saves, ShinyForge closes.
    /// </summary>
    public static bool CanSave(string titleId) => GameOf(titleId)?.Bot == BotKind.FrlgSwitch;

    /// <summary>
    /// Saves the running game in-game (it's brought on screen first). Each step is checked on a screenshot; a step that does
    /// nothing stops with an error, so a game that couldn't be saved is never closed.
    /// </summary>
    private static void SaveGame(SysBotSession bot, string titleId, CancellationToken ct)
    {
        if (!CanSave(titleId)) throw new IOException($"ShinyForge can't save {GameName(titleId)} - save it yourself.");
        if (!ToFront(bot)) throw new IOException($"{GameName(titleId)} didn't come back on screen - it wasn't saved or closed.");
        SaveFrlg(bot, titleId, ct);
        AppPaths.Log($"Switch: {GameName(titleId)} saved");
    }

    /// <summary>
    /// FireRed / LeafGreen: START (+) → SAVE → YES ("Would you like to save the game?") → YES ("There is already a saved file.
    /// Is it okay to overwrite it?") → "SAVING..." → "... saved the game." → back on the map. Every step is read from a
    /// screenshot (the GBA picture is drawn at fixed places): the START menu's rows sit 65 px apart from y 68 with the ▶ at
    /// x 880-906; SAVE is always third from the bottom (…, SAVE, OPTION, EXIT); the YES/NO box and the text box are white.
    /// Presses get dropped, so nothing is pressed blindly.
    /// </summary>
    private static void SaveFrlg(SysBotSession bot, string titleId, CancellationToken ct)
    {
        var fail = $"{GameName(titleId)} couldn't be saved - it was left open. Save it yourself (START → SAVE).";
        var overworld = FrlgOverworld(titleId);
        if (bot.Peek(overworld, 4) is not { } ow || BitConverter.ToUInt32(ow) == 0) throw new IOException(fail + " (The save isn't loaded.)");

        // the START menu, open with nothing else on screen (a text box: B closes it once). The plain map is remembered: the
        // text box / YES-NO box are recognised as "much whiter than the map was there", so a bright map isn't taken for them.
        RgbImage? img = null;
        double textMap = 0, askMap = 0;
        var pressedB = false;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            img = Shot(bot) ?? throw new IOException(fail);
            if (StartMenuRows(img) >= 3) break;
            double t = White(img, TextBox), q = White(img, AskBox);
            if ((t > 0.6 || q > 0.6) && !pressedB) { bot.Tap("B", 900); pressedB = true; continue; }
            (textMap, askMap) = (t, q);
            bot.Tap("PLUS", 1200);
        }
        var rows = StartMenuRows(img!);
        if (rows < 3) throw new IOException(fail + " (The START menu didn't open - is a battle or a talk going on?)");
        if (textMap > 0.7 || askMap > 0.7)
        {
            bot.Tap("B", 800);
            throw new IOException(fail + " (The map is too bright here to follow the save on screen - step somewhere darker.)");
        }

        // ▶ onto SAVE, one press at a time
        var save = rows - 3;
        for (var step = 0; ; step++)
        {
            var at = MenuArrow(img!, rows);
            if (at == save) break;
            if (at < 0 || step > 12) { bot.Tap("B", 800); throw new IOException(fail); }
            bot.Tap(at < save ? "DDOWN" : "DUP", 450);
            img = Shot(bot) ?? throw new IOException(fail);
        }
        bot.Tap("A", 1200);

        // answer YES to each question until the game is back on the map
        var yes = 0;
        var until = DateTime.Now.AddSeconds(30);
        while (DateTime.Now < until)
        {
            ct.ThrowIfCancellationRequested();
            img = Shot(bot) ?? throw new IOException(fail);
            var question = White(img, AskBox) > Math.Max(0.6, askMap + 0.25);
            var text = White(img, TextBox) > Math.Max(0.6, textMap + 0.25);
            if (question && yes < 3) { bot.Tap("A", 1500); yes++; continue; } // YES is preselected
            if (!question && !text && StartMenuRows(img) < 3)
            {
                if (yes == 0) throw new IOException(fail);
                return; // "... saved the game." closed by itself: saved
            }
            if (!question && !text && yes == 0) { bot.Tap("A", 1200); continue; } // still on SAVE: the A was dropped
            Thread.Sleep(600);
        }
        throw new IOException(fail);
    }

    /// <summary>The FRLG overworld flag (heap offset): set once the save is loaded. Japanese versions differ.</summary>
    private static uint FrlgOverworld(string title) => title is "01006FA0233F8000" or "0100F1E0233FA000" ? 0x1222B54u : 0x1222BDCu;

    /// <summary>Where FRLG draws its text box and its YES / NO box (x0, x1, y0, y1 on the 1280×720 screenshot).</summary>
    private static readonly (int X0, int X1, int Y0, int Y1) TextBox = (220, 1080, 540, 650), AskBox = (850, 1050, 305, 465);

    private static double White(RgbImage img, (int X0, int X1, int Y0, int Y1) box) => White(img, box.X0, box.X1, box.Y0, box.Y1);

    /// <summary>Share of near-white pixels in a box (every 6th pixel).</summary>
    private static double White(RgbImage img, int x0, int x1, int y0, int y1)
    {
        int white = 0, n = 0;
        for (var y = y0; y <= y1; y += 6)
        for (var x = x0; x <= x1; x += 6, n++)
        {
            var i = (y * img.Width + x) * 3;
            if (img.Pixels[i] > 225 && img.Pixels[i + 1] > 225 && img.Pixels[i + 2] > 225) white++;
        }
        return n == 0 ? 0 : (double)white / n;
    }

    private static int RowY(int row) => 68 + 65 * row;

    private static int DarkIn(RgbImage img, int x0, int x1, int y0, int y1)
    {
        var dark = 0;
        for (var y = y0; y <= y1; y += 2)
        for (var x = x0; x <= x1; x += 2)
        {
            var i = (y * img.Width + x) * 3;
            if (img.Pixels[i] < 120 && img.Pixels[i + 1] < 120 && img.Pixels[i + 2] < 120) dark++;
        }
        return dark;
    }

    /// <summary>FRLG START menu open: how many rows it has (0 = not open). Its box is white at the top right.</summary>
    internal static int StartMenuRows(RgbImage img)
    {
        if (img.Width < 1280 || img.Height < 720 || White(img, 900, 1100, 40, 50) < 0.5) return 0;
        var rows = 0;
        while (rows < 8 && RowY(rows) + 16 < 520 && DarkIn(img, 910, 1150, RowY(rows) - 10, RowY(rows) + 10) > 6) rows++;
        return rows;
    }

    /// <summary>Which START-menu row the ▶ is on, or -1.</summary>
    internal static int MenuArrow(RgbImage img, int rows)
    {
        int best = -1, most = 4;
        for (var r = 0; r < rows; r++)
        {
            var d = DarkIn(img, 880, 906, RowY(r) - 12, RowY(r) + 12);
            if (d > most) { most = d; best = r; }
        }
        return best;
    }

    /// <summary>The running game is suspended behind the HOME menu → HOME brings it back on screen. True = the game is on screen.</summary>
    private static bool ToFront(SysBotSession bot)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var home = HomeShown(bot);
            if (home == false) return true;
            if (home == true) bot.Tap("HOME", 2000);
            else Thread.Sleep(500); // no screenshot: don't press blindly
        }
        return false;
    }

    /// <summary>Is the HOME menu on screen? Null = no screenshot (don't guess).</summary>
    private static bool? HomeShown(SysBotSession bot) => Shot(bot) is { } img ? IsHomeMenu(img) : null;

    /// <summary>
    /// The HOME menu is on screen: its thin divider line above the button hints (y ≈ 647) runs across the whole screen in one
    /// colour, with plain background just above and below it. Games don't draw that.
    /// </summary>
    internal static bool IsHomeMenu(RgbImage img)
    {
        if (img.Width < 1280 || img.Height < 720) return false;
        int Px(int x, int y, int c) => img.Pixels[(y * img.Width + x) * 3 + c];
        bool Same(int x1, int y1, int x2, int y2, int tol) => Enumerable.Range(0, 3).All(c => Math.Abs(Px(x1, y1, c) - Px(x2, y2, c)) <= tol);
        for (var y = 640; y <= 655; y++)
        {
            var line = true;
            for (var x = 40; x <= 1240 && line; x += 8)
                line = Same(x, y, 640, y, 20) && Same(x, y - 6, 640, y - 6, 12) && Same(x, y + 6, 640, y + 6, 12) && !Same(x, y, x, y - 6, 60);
            if (line && Same(640, y - 6, 640, y + 6, 12) && Same(640, y - 6, 20, 120, 12)) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ the HOME menu
    /// <summary>
    /// On the HOME menu: down, down = the round buttons along the bottom (from any row); up once = the game row. Never goes up
    /// to the user icons at the top - the start is the first game, not a user profile.
    /// </summary>
    private static void ToGameRow(SysBotSession bot)
    {
        bot.Tap("DDOWN", 300); bot.Tap("DDOWN", 300); bot.Tap("DUP", 400);
    }

    /// <summary>
    /// Cursor on the first game, checked on a screenshot - the starting point. LEFT is pressed ONE at a time with a screenshot
    /// after each: the row wraps both ways (LEFT on the first game jumps to "All Software" at the end), so blind presses
    /// overshoot, and quick presses get dropped while the row scrolls. The first game = a selected tile with nothing but
    /// background left of it (a tile at the left edge alone isn't enough: the row scrolls, so every tile ends up there).
    /// "All Software" selected (no tile frame) → RIGHT wraps to the first game.
    /// </summary>
    internal static (RgbImage Img, Box Box) StartAtFirstGame(SysBotSession bot)
    {
        ToGameRow(bot);
        var noTile = 0;
        for (var step = 0; step < 40; step++)
        {
            Thread.Sleep(250);
            var img = Shot(bot) ?? throw new IOException("Couldn't take a screenshot of the Switch.");
            if (SelectedBox(img) is { W: > 220 } box)
            {
                noTile = 0;
                if (box.X < 160 && NothingLeftOf(img, box)) return (img, box);
                bot.Tap("DLEFT", 450);
            }
            else if (++noTile == 1) bot.Tap("DRIGHT", 450); // "All Software" → wraps to the first game
            else { ToGameRow(bot); noTile = 0; }            // not on the game row at all (a press was missed)
        }
        throw new IOException("Couldn't put the cursor on the first game of the HOME menu.");
    }

    /// <summary>True when the strip left of the selected tile is plain background (no tile there): the first game.</summary>
    internal static bool NothingLeftOf(RgbImage img, Box box)
    {
        int Px(int x, int y, int c) => img.Pixels[(y * img.Width + x) * 3 + c];
        const int bx = 20, by = 120; // background above the row, left of the name tooltip
        int other = 0, total = 0;
        for (var y = box.Y + 20; y < box.Y + box.H - 20; y += 4)
            for (var x = 8; x < box.X - 6; x += 4, total++)
                if (Enumerable.Range(0, 3).Any(c => Math.Abs(Px(x, y, c) - Px(bx, by, c)) > 30)) other++;
        return total > 0 && other < total / 20;
    }

    /// <summary>
    /// Steps right along the HOME row, checking each selected tile, until the game is selected (true) or "All Software" at
    /// the end of the row is (false, the cursor stays there). A press the Switch missed is repeated.
    /// </summary>
    internal static bool FindOnHomeRow(SysBotSession bot, List<double[]> icons, ref RgbImage img, ref Box box, CancellationToken ct)
    {
        for (var step = 0; step < 16; step++)
        {
            ct.ThrowIfCancellationRequested();
            if (IsIcon(img, box, icons)) return true;
            var before = ConsoleFtpLauncher.Fingerprint(img, Inner(box));
            Box? next = null;
            // WiFi drops presses now and then (two in a row seen on the real Switch): up to 4 tries before giving up
            for (var tries = 0; tries < 4 && next == null; tries++)
            {
                bot.Tap("DRIGHT", 550);
                var now = Shot(bot) ?? throw new IOException("Couldn't take a screenshot of the Switch.");
                var b = SelectedBox(now);
                if (b == null) { img = now; return false; }           // "All Software" (a circle, no tile frame) is selected
                if (ConsoleFtpLauncher.Difference(ConsoleFtpLauncher.Fingerprint(now, Inner(b.Value)), before) > 3) { img = now; next = b; }
            }
            // 4 presses and the cursor never moved: the Switch isn't taking the controller - say so instead of "not installed"
            if (next == null) throw new IOException("The Switch doesn't react to ShinyForge's button presses on the HOME menu. Check the WiFi and try again.");
            box = next.Value;
        }
        return false;
    }

    /// <summary>
    /// "All Software" is selected: opens it and goes down the grid row by row. Every fully visible icon is compared; when the
    /// game is seen the cursor is moved onto it (and checked). False = not installed (the bottom was reached).
    /// </summary>
    internal static bool FindInAllSoftware(SysBotSession bot, List<double[]> icons, Action<string>? progress, CancellationToken ct)
    {
        var img = Shot(bot);
        if (img == null || SelectedBox(img) != null) return false; // not on "All Software"
        bot.Tap("A", 2500);
        double[]? last = null;
        for (var page = 0; page < 200; page++)
        {
            ct.ThrowIfCancellationRequested();
            img = Shot(bot) ?? throw new IOException("Couldn't take a screenshot of the Switch.");
            if (SelectedBox(img) is not { } sel) return false;
            // every fully visible icon in the selected row and the rows under it
            foreach (var (col, row) in GridCells(sel))
            {
                var cell = new Box(sel.X + col * AllSoftwarePitch, sel.Y + row * AllSoftwarePitch, sel.W, sel.H);
                if (!IsIcon(img, cell, icons)) continue;
                for (var i = 0; i < Math.Abs(col); i++) bot.Tap(col > 0 ? "DRIGHT" : "DLEFT", 300);
                for (var i = 0; i < row; i++) bot.Tap("DDOWN", 350);
                Thread.Sleep(300);
                if (Shot(bot) is { } check && SelectedBox(check) is { } at && IsIcon(check, at, icons)) return true;
                return false; // seen but the cursor didn't land on it - better stop than start something else
            }
            var print = ConsoleFtpLauncher.Fingerprint(img, Inner(sel));
            if (last != null && ConsoleFtpLauncher.Difference(print, last) < 2) return false; // DOWN didn't move: the end of the list
            last = print;
            progress?.Invoke($"Looking through All Software (row {page + 2})...");
            bot.Tap("DDOWN", 500);
        }
        return false;
    }

    /// <summary>Grid cells (column offset, row offset) to check around the selected icon: its row and the rows below that fit on screen.</summary>
    private static IEnumerable<(int Col, int Row)> GridCells(Box sel)
    {
        for (var row = 0; sel.Y + row * AllSoftwarePitch + sel.H <= ScreenBottom + 4; row++)
            for (var col = -5; col <= 5; col++)
            {
                var x = sel.X + col * AllSoftwarePitch;
                if (x >= 60 && x + sel.W <= 1220) yield return (col, row);
            }
    }

    // ------------------------------------------------------------------ pictures
    internal readonly record struct Box(int X, int Y, int W, int H);

    private static RgbImage? Shot(SysBotSession bot) =>
        bot.Screenshot() is { } jpg ? ConsoleFtpLauncher.Decode?.Invoke(jpg) : null;

    /// <summary>The tile inside its frame (a few pixels in: no frame, no rounded corners).</summary>
    private static (int X, int Y, int W, int H) Inner(Box b) => (b.X + 10, b.Y + 10, b.W - 20, b.H - 20);

    private static bool IsIcon(RgbImage img, Box b, List<double[]> icons)
    {
        var print = ConsoleFtpLauncher.Fingerprint(img, Inner(b));
        return icons.Any(i => ConsoleFtpLauncher.Difference(print, i) <= IconLimit);
    }

    internal static List<double[]> IconPrints(IEnumerable<string> titleIds)
    {
        var list = new List<double[]>();
        foreach (var id in titleIds)
        {
            var path = Path.Combine(AppPaths.AssetsDir, "SwitchIcons", id + ".jpg");
            if (File.Exists(path) && ConsoleFtpLauncher.Decode?.Invoke(File.ReadAllBytes(path)) is { } img)
                list.Add(ConsoleFtpLauncher.Fingerprint(img, (6, 6, img.Width - 12, img.Height - 12)));
        }
        return list;
    }

    /// <summary>
    /// The selected tile: the HOME menu draws a cyan frame around it. Its left and right edges are long vertical runs of
    /// frame colour (the name tooltip can break the top edge, so the height comes from the left edge). Null = no tile
    /// selected (e.g. the round "All Software" button).
    /// </summary>
    internal static Box? SelectedBox(RgbImage img)
    {
        bool Frame(int x, int y)
        {
            var i = (y * img.Width + x) * 3;
            int r = img.Pixels[i], g = img.Pixels[i + 1], b = img.Pixels[i + 2];
            return b > 150 && g > 110 && r < 110 && b - r > 70;
        }
        int left = -1, right = -1, top = 0, bottom = 0;
        for (var x = 0; x < img.Width; x++)
        {
            int run = 0, start = 0, bestStart = 0, bestEnd = 0;
            for (var y = 80; y < Math.Min(660, img.Height); y++)
            {
                if (Frame(x, y))
                {
                    if (run++ == 0) start = y;
                    if (y + 1 - start > bestEnd - bestStart) { bestStart = start; bestEnd = y + 1; }
                }
                else run = 0;
            }
            if (bestEnd - bestStart < 140) continue;
            if (left < 0) { left = x; top = bestStart; bottom = bestEnd; }
            right = x;
        }
        return left < 0 || right - left < 140 ? null : new Box(left, top, right - left, bottom - top);
    }

    // ------------------------------------------------------------------ starting the game
    /// <summary>After A on a tile: the title ID once the game runs (asking which user plays if the Switch asks), or null if nothing started.</summary>
    private static string? WaitForStart(SysBotSession bot, CancellationToken ct)
    {
        var until = DateTime.Now.AddSeconds(25);
        while (DateTime.Now < until)
        {
            ct.ThrowIfCancellationRequested();
            Thread.Sleep(700);
            if (bot.TitleId() is { } id) return id;
            if (PickUserIfAsked(bot)) until = DateTime.Now.AddSeconds(25);
        }
        return null;
    }

    /// <summary>
    /// "Who is using the software?" on screen? Then the user chooses who plays (<see cref="ChooseUser"/>) and that user is
    /// selected (far left, then right). Detected on a screenshot - never pressed blindly. Cancelling backs out (B) and stops.
    /// </summary>
    internal static bool PickUserIfAsked(SysBotSession bot)
    {
        if (ConsoleFtpLauncher.Decode is not { } decode || bot.Screenshot() is not { } shot || decode(shot) is not { } img ||
            !ConsoleFtpLauncher.IsUserPicker(img))
            return false;
        var users = CountUsers(img);
        var choice = ChooseUser == null ? 0 : ChooseUser(shot, users);
        if (choice == null)
        {
            bot.Tap("B", 800);
            throw new OperationCanceledException("No user was chosen - nothing was started.");
        }
        for (var k = 0; k < 8; k++) bot.Tap("DLEFT", 150);
        for (var k = 0; k < Math.Clamp(choice.Value, 0, users - 1); k++) bot.Tap("DRIGHT", 200);
        bot.Tap("A", 200);
        return true;
    }

    /// <summary>The user icons on "Who is using the software?" sit 150 px apart in one row: how many are there.</summary>
    internal static int CountUsers(RgbImage img)
    {
        const int y = 470;
        int first = -1, last = -1;
        for (var x = 30; x < Math.Min(880, img.Width); x++)
        {
            var i = (y * img.Width + x) * 3;
            int r = img.Pixels[i], g = img.Pixels[i + 1], b = img.Pixels[i + 2];
            var panel = Math.Abs(r - 70) < 12 && Math.Abs(g - 70) < 12 && Math.Abs(b - 70) < 12;
            if (panel) continue;
            if (first < 0) first = x;
            last = x;
        }
        return first < 0 ? 1 : Math.Max(1, (int)Math.Round((last - first) / 150.0));
    }

    /// <summary>
    /// FireRed / LeafGreen: A through the intro, title screen and CONTINUE until the overworld flag in RAM is set (the save is
    /// loaded), then B through the recap. Heap offsets as in the Python hunter's memory.py (Japanese versions differ).
    /// </summary>
    private static void LoadFrlgSave(SysBotSession bot, string title, Action<string>? progress, CancellationToken ct)
    {
        var overworld = FrlgOverworld(title);
        bool Started() => bot.Peek(overworld, 4) is { } b && BitConverter.ToUInt32(b) != 0;
        // "started" alone isn't enough: a NEW GAME sets the same pointer - only a trainer name + party mean the save is loaded
        bool Loaded() => Started() && LiveFrlg.SaveLoaded(bot, title);
        if (Loaded()) return; // already in the game
        progress?.Invoke("Loading your save (pressing A through the title screen)...");
        Thread.Sleep(4000); // the game's own start-up
        var until = DateTime.Now.AddSeconds(90);
        DateTime? emptyGameSince = null;
        while (!Loaded())
        {
            ct.ThrowIfCancellationRequested();
            if (DateTime.Now > until) throw new IOException("The save didn't load - check the Switch and press CONTINUE yourself.");
            if (Started())
            {
                // in a game but without trainer / party: A went into NEW GAME - there's no save for this Switch user
                emptyGameSince ??= DateTime.Now;
                if (DateTime.Now - emptyGameSince > TimeSpan.FromSeconds(6))
                    throw new IOException($"{GameName(title)} has no save for this Switch user - the title screen only offered NEW GAME, " +
                                          "so a new game started (nothing was saved). Close it (HOME, X, Close) and choose the user that has " +
                                          "your save, or play and save the game once.");
                Thread.Sleep(1000); // a loading save sets the name a moment later - don't press A into the intro
                continue;
            }
            emptyGameSince = null;
            bot.Tap("A", 1000);
        }
        for (var i = 0; i < 4; i++) bot.Tap("B", 800); // "previously on your quest" recap
        Thread.Sleep(1000);
    }
}
