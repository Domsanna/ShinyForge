using System.Runtime.InteropServices;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Updates the user's Pokédex tracker workbooks (.xlsm) through Microsoft Excel itself, so macros, formulas,
/// external links and the embedded sprite pictures are all preserved. Columns are found by their header text
/// ("Name", "In Box", "OT", "Shiny"); rows are matched by Pokémon name. Cells are only ever changed from N to Y.
/// </summary>
public static class SpreadsheetService
{
    /// <summary>
    /// Spreadsheets shipped with ShinyForge. Portable: "Spreadsheets" next to ShinyForge.exe (used in place, so it travels with the
    /// drive). Installed: the installer puts them in Documents\ShinyForge\Spreadsheets (never overwritten / removed, it's your progress).
    /// </summary>
    public static string BundledRoot => AppPaths.IsPortable
        ? Path.Combine(AppPaths.AppDir, "Spreadsheets")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShinyForge", "Spreadsheets");

    /// <summary>Where the tracker for <paramref name="game"/> is: a file you chose, else your spreadsheet folder (Settings), else the bundled copy.</summary>
    public static string? FindWorkbook(Game game) => FindWorkbook(game, AppSettings.Current.SpreadsheetRoot);

    public static string? FindWorkbook(Game game, string root)
    {
        var configured = AppSettings.Current.For(game.Id).SpreadsheetPath;
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;
        return FindIn(game, root) ?? FindIn(game, BundledRoot) ?? FindIn(game, Path.Combine(AppPaths.AppDir, "Spreadsheets"));
    }

    /// <summary>Text for the UI: where the tracker came from.</summary>
    public static string Describe(Game game, string? wb)
    {
        if (wb == null)
            return string.IsNullOrWhiteSpace(game.Spreadsheet)
                ? "No Pokédex tracker spreadsheet exists for this game yet - use 'Choose spreadsheet' if you have one."
                : $"No \"{game.Spreadsheet}\" tracker found (looked in {AppSettings.Current.SpreadsheetRoot} and the bundled Spreadsheets folder).";
        var full = Path.GetFullPath(wb);
        var where = !string.IsNullOrWhiteSpace(AppSettings.Current.For(game.Id).SpreadsheetPath) ? "chosen by you"
            : full.StartsWith(Path.GetFullPath(AppSettings.Current.SpreadsheetRoot), StringComparison.OrdinalIgnoreCase) ? "your spreadsheet folder"
            : "bundled with ShinyForge";
        return $"Spreadsheet (auto-recognized, {where}): {Path.GetFileName(wb)}";
    }

    /// <summary>"(25) Sun" → "Sun": a tracker folder's game name without its number.</summary>
    public static string LabelOf(string folderName)
    {
        var close = folderName.IndexOf(')');
        return close >= 0 ? folderName[(close + 1)..] : folderName;
    }

    private static string? FindIn(Game game, string root)
    {
        if (string.IsNullOrWhiteSpace(game.Spreadsheet) || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return null;
        var want = Collection.Normalize(game.Spreadsheet);
        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            if (Collection.Normalize(LabelOf(Path.GetFileName(dir))) != want)
                continue;
            var file = Directory.EnumerateFiles(dir, "*.xls*").FirstOrDefault(f => !Path.GetFileName(f).StartsWith("~$"));
            if (file != null)
                return file;
        }
        return null;
    }

    public sealed record Result(int MarkedInBox, int MarkedOT, int MarkedShiny, int Sheets, List<string> Notes);

    /// <summary>Updating a tracker drives Microsoft Excel (COM), which exists only on Windows.</summary>
    public static bool CanUpdate => OperatingSystem.IsWindows();

    public static Result Update(string workbookPath, Collection owned, Action<string> progress)
    {
        if (!CanUpdate)
            throw new PlatformNotSupportedException("Updating a Pokédex tracker uses Microsoft Excel on Windows, which keeps the trackers' macros and pictures intact. " +
                                                    "On macOS and Linux you can open the tracker and read your collection in ShinyForge's Pokédex window.");
        var notes = new List<string>();
        int inBox = 0, ot = 0, shiny = 0, sheets = 0;
        var type = Type.GetTypeFromProgID("Excel.Application")
                   ?? throw new InvalidOperationException("Microsoft Excel is not installed - it's needed to update the .xlsm trackers safely.");
        dynamic? app = null, wb = null;
        try
        {
            progress("Starting Excel...");
            app = Activator.CreateInstance(type)!;
            app.Visible = false;
            app.DisplayAlerts = false;
            app.ScreenUpdating = false;
            app.AskToUpdateLinks = false;
            app.AutomationSecurity = 3; // msoAutomationSecurityForceDisable: never run the workbook's macros

            progress($"Opening {Path.GetFileName(workbookPath)} (large trackers can take a while)...");
            wb = app.Workbooks.Open(workbookPath, 0 /*UpdateLinks: don't*/, false /*ReadOnly*/);
            if ((bool)wb.ReadOnly)
                throw new IOException("The tracker is open somewhere else (read-only). Close it in Excel and try again.");

            foreach (dynamic ws in wb.Worksheets)
            {
                string sheetName = ws.Name;
                dynamic used = ws.UsedRange;
                int firstRow = used.Row, firstCol = used.Column;
                object raw = used.Value2;
                if (raw is not object[,] values)
                    continue;
                int rows = values.GetLength(0), cols = values.GetLength(1);

                // Header row: within the first 10 rows, contains "Name" and "In Box".
                int header = -1, cName = -1, cBox = -1, cOT = -1, cShiny = -1;
                for (int r = 1; r <= Math.Min(rows, 10) && header < 0; r++)
                {
                    int n = -1, b = -1, o = -1, s = -1;
                    for (int c = 1; c <= cols; c++)
                    {
                        var h = (values[r, c] as string)?.Trim().ToLowerInvariant();
                        if (h == "name") n = c;
                        else if (h == "in box") b = c;
                        else if (h == "ot") o = c;
                        else if (h == "shiny") s = c;
                    }
                    if (n > 0 && b > 0)
                        (header, cName, cBox, cOT, cShiny) = (r, n, b, o, s);
                }
                if (header < 0)
                    continue;
                sheets++;
                progress($"Updating sheet '{sheetName}'...");

                for (int r = header + 1; r <= rows; r++)
                {
                    if (values[r, cName] is not string name || name.Length == 0)
                        continue;
                    if (!owned.BySpecies.TryGetValue(Collection.Normalize(name), out var e))
                        continue;
                    if (e.Owned && SetY(ws, values, r, cBox, firstRow, firstCol)) inBox++;
                    if (e.OwnOT && cOT > 0 && SetY(ws, values, r, cOT, firstRow, firstCol)) ot++;
                    if (e.Shiny && cShiny > 0 && SetY(ws, values, r, cShiny, firstRow, firstCol)) shiny++;
                }
                Marshal.ReleaseComObject(used);
            }

            progress("Saving the tracker...");
            wb.Save();
            notes.Add($"Updated {Path.GetFileName(workbookPath)}");
            return new Result(inBox, ot, shiny, sheets, notes);
        }
        finally
        {
            try { wb?.Close(false); } catch { }
            try { app?.Quit(); } catch { }
            if (wb != null) Marshal.ReleaseComObject(wb);
            if (app != null) Marshal.ReleaseComObject(app);
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    /// <summary>Sets a cell to "Y" only if it is currently "N" (never touches "Y" or "N/A"). Returns true if changed.</summary>
    private static bool SetY(dynamic ws, object[,] values, int r, int c, int firstRow, int firstCol)
    {
        var current = (values[r, c] as string)?.Trim().ToUpperInvariant();
        if (current != "N")
            return false;
        ws.Cells[firstRow + r - 1, firstCol + c - 1].Value2 = "Y";
        values[r, c] = "Y";
        return true;
    }
}
