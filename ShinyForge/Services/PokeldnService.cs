using System.Diagnostics;
using System.IO.Compression;
using System.IO.Ports;
using System.Management;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>An ESP32 board seen on USB (serial port).</summary>
public sealed record UsbBoard(string Port, string Name, string Vid, string Pid)
{
    /// <summary>Espressif native USB Serial/JTAG (ESP32-S3 / C3 / C6). Classic boards use a CP210x or CH340 bridge.</summary>
    public bool NativeUsb => Vid == "303A";
    public string Kind => NativeUsb ? "ESP32-S3 (native USB)" : Vid == "10C4" ? "classic ESP32 (CP210x bridge)" : Vid == "1A86" ? "classic ESP32 (CH340 bridge)" : "USB serial device";
}

/// <summary>What the pokeldn radio firmware answered (HELLO → INFO "pokeldn-radio esp32s3 version=1.11.0"), or why it didn't.</summary>
public sealed record FirmwareInfo(bool Answered, string? Version, string Text, string? Error)
{
    /// <summary>The chip the firmware was built for ("esp32s3"), the second word of the INFO text.</summary>
    public string Target => Text.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } w && !w[1].Contains('=') ? w[1] : "";
}

/// <summary>A pokeldn release: tag, app archive, S3 radio firmware and the checksums file.</summary>
public sealed record PokeldnRelease(string Tag, string? AppUrl, long AppSize, string? FirmwareUrl, string? SumsUrl, string PageUrl)
{
    public Version? Version => AppInfo.ParseTag(Tag);
}

/// <summary>
/// pokeldn (Decryptu/pokeldn, AGPL-3.0) integration: local wireless trades and Mystery Gift with Switch Pokémon games
/// through an ESP32-S3 radio on USB. ShinyForge manages the pieces - the pokeldn desktop app, the board's radio
/// firmware (flashed with Espressif's esptool), detection of the board, and the user's prod.keys location - and starts
/// pokeldn, which does the trading. Nothing here sends or copies the keys anywhere: only their file path is stored.
/// </summary>
public static class PokeldnService
{
    public const string Repo = "Decryptu/pokeldn";
    public const string EsptoolRepo = "espressif/esptool";
    /// <summary>The pokeldn download for this OS and processor.</summary>
    public static string AppAsset =>
        OsInfo.IsWindows ? "pokeldn-windows-x64.zip"
        : OsInfo.IsMac ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "pokeldn-macos-arm64.zip" : "pokeldn-macos-x64.zip")
        : "pokeldn-linux-x64.tar.gz";
    /// <summary>The program inside it: pokeldn.exe, pokeldn.app (macOS) or pokeldn (Linux).</summary>
    private static string AppFile => OsInfo.IsWindows ? "pokeldn.exe" : OsInfo.IsMac ? "pokeldn.app" : "pokeldn";
    /// <summary>esptool's download for this OS ("windows-amd64.zip", "macos-arm64.tar.gz", "linux-amd64.tar.gz").</summary>
    private static string EsptoolAssetSuffix =>
        OsInfo.IsWindows ? "windows-amd64.zip"
        : OsInfo.IsMac ? (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "macos-arm64.tar.gz" : "macos-amd64.tar.gz")
        : System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "linux-aarch64.tar.gz" : "linux-amd64.tar.gz";
    public const string FirmwareAsset = "pokeldn-radio-s3.bin";
    /// <summary>Entries pokeldn's radio needs in prod.keys (pokeldn docs: ldn.KEYS_NEEDED).</summary>
    public static readonly string[] KeysNeeded = { "aes_kek_generation_source", "aes_key_generation_source", "master_key_00", "master_key_12" };

    private static readonly HttpClient Http = WebClients.Make(TimeSpan.FromSeconds(20), githubJson: false);

    // ------------------------------------------------------------------ where things live
    public static string AppDir => Path.Combine(AppPaths.ToolsDir, "pokeldn");
    public static string FirmwareDir => Path.Combine(AppPaths.ToolsDir, "pokeldn-firmware");
    public static string EsptoolDir => Path.Combine(AppPaths.ToolsDir, "esptool");
    /// <summary>pokeldn's own data folder (settings.json with the prod.keys path).</summary>
    public static string PokeldnDataDir => Path.Combine(OsInfo.DataRoot, "pokeldn");

    public static string? FindApp()
    {
        if (File.Exists(AppSettings.Current.PokeldnPath)) return AppSettings.Current.PokeldnPath;
        if (OsInfo.IsMac)
        {
            // an app bundle is a folder
            foreach (var root in new[] { AppDir, AppPaths.BackupToolsDir, "/Applications" })
                try
                {
                    if (Directory.Exists(root) && Directory.EnumerateDirectories(root, "pokeldn.app", new System.IO.EnumerationOptions { RecurseSubdirectories = root != "/Applications", MaxRecursionDepth = 4, IgnoreInaccessible = true }).FirstOrDefault() is { } app)
                        return app;
                }
                catch { }
            return null;
        }
        var local = Path.Combine(AppDir, AppFile);
        if (File.Exists(local)) return local;
        if (Directory.Exists(AppDir) && Directory.EnumerateFiles(AppDir, AppFile, SearchOption.AllDirectories).FirstOrDefault() is { } nested) return nested;
        return AppPaths.FindInBackupTools(AppFile);
    }

    public static string? FindEsptool()
    {
        if (Directory.Exists(EsptoolDir) && Directory.EnumerateFiles(EsptoolDir, OsInfo.Exe("esptool"), SearchOption.AllDirectories).FirstOrDefault() is { } e) return e;
        return AppPaths.FindInBackupTools(OsInfo.Exe("esptool")) ?? (OsInfo.IsWindows ? null : OsInfo.Which("esptool"));
    }

    /// <summary>The newest S3 radio firmware file ShinyForge has (Tools, then Backup Tools).</summary>
    public static string? FindFirmware()
    {
        var local = Path.Combine(FirmwareDir, FirmwareAsset);
        return File.Exists(local) ? local : AppPaths.FindInBackupTools(FirmwareAsset);
    }

    /// <summary>The pokeldn release ShinyForge installed ("v0.65.5"), from version.txt beside the app.</summary>
    public static string? InstalledAppTag(string? exe)
    {
        if (exe == null) return null;
        var marker = Path.Combine(Path.GetDirectoryName(exe.TrimEnd(Path.DirectorySeparatorChar))!, "shinyforge-version.txt");
        return File.Exists(marker) ? File.ReadAllText(marker).Trim() : null;
    }

    // ------------------------------------------------------------------ releases
    public static async Task<PokeldnRelease> LatestAsync()
    {
        var j = await Http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{Repo}/releases/latest")
                ?? throw new IOException("GitHub didn't return the pokeldn release.");
        string? Url(string name) => (j["assets"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["name"]?.ToString() == name)?["browser_download_url"]?.ToString();
        long size = (j["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => a["name"]?.ToString() == AppAsset)?["size"]?.GetValue<long>() ?? 0;
        return new PokeldnRelease(j["tag_name"]?.ToString() ?? "", Url(AppAsset), size, Url(FirmwareAsset), Url("SHA256SUMS"),
            j["html_url"]?.ToString() ?? $"https://github.com/{Repo}/releases/latest");
    }

    /// <summary>"name" → sha256 hex, from a SHA256SUMS file's text.</summary>
    public static Dictionary<string, string> ParseSums(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split(new[] { ' ', '\t', '*' }, 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(p => p.Length == 2 && p[0].Length == 64)
            .GroupBy(p => p[1].Trim().TrimStart('*'), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First()[0].ToLowerInvariant(), StringComparer.OrdinalIgnoreCase);

    public static string Sha256(string file)
    {
        using var s = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();
    }

    private static async Task DownloadVerified(string url, string file, string? expectedSha, Action<string> progress, string label)
    {
        using var http = WebClients.Make(TimeSpan.FromMinutes(30), githubJson: false);
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using (var src = await resp.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(file))
        {
            var buf = new byte[1 << 20];
            long done = 0; int n; var last = 0L;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                done += n;
                if (Environment.TickCount64 - last > 300) { last = Environment.TickCount64; progress(total > 0 ? $"Downloading {label}... {done * 100 / total}%" : $"Downloading {label}... {done / 1048576} MB"); }
            }
        }
        if (expectedSha != null && Sha256(file) != expectedSha)
        {
            File.Delete(file);
            throw new IOException($"{label}: the download doesn't match its published SHA-256 checksum - nothing was installed. Please try again.");
        }
    }

    /// <summary>Downloads and installs (or updates) the pokeldn desktop app and the ESP32-S3 radio firmware file.</summary>
    public static async Task<string> InstallAppAndFirmware(Action<string> progress)
    {
        if (Process.GetProcessesByName("pokeldn").Length > 0)
            throw new IOException("pokeldn is running - close it first, then try again.");
        progress("Looking up the latest pokeldn release...");
        var rel = await LatestAsync();
        if (rel.AppUrl == null || rel.SumsUrl == null)
            throw new IOException($"pokeldn {rel.Tag} has no {OsInfo.Name} download or checksums - get it from {rel.PageUrl}.");
        var sums = ParseSums(await Http.GetStringAsync(rel.SumsUrl));
        var tmp = Path.Combine(Path.GetTempPath(), "ShinyForge-pokeldn-" + rel.Tag);
        Directory.CreateDirectory(tmp);

        var zip = Path.Combine(tmp, AppAsset);
        await DownloadVerified(rel.AppUrl, zip, sums.GetValueOrDefault(AppAsset), progress, $"pokeldn {rel.Tag}");
        progress("Unpacking pokeldn...");
        var unpack = Path.Combine(tmp, "app");
        if (Directory.Exists(unpack)) Directory.Delete(unpack, true);
        await Unpack(zip, unpack);
        if (Directory.Exists(AppDir)) Directory.Delete(AppDir, true);
        if (OsInfo.IsMac)
        {
            var bundle = Directory.EnumerateDirectories(unpack, "pokeldn.app", SearchOption.AllDirectories).FirstOrDefault()
                         ?? throw new IOException("The pokeldn download doesn't contain pokeldn.app.");
            Directory.CreateDirectory(AppDir);
            Directory.Move(bundle, Path.Combine(AppDir, "pokeldn.app"));
        }
        else
        {
            var exe = Directory.EnumerateFiles(unpack, AppFile, SearchOption.AllDirectories).FirstOrDefault()
                      ?? throw new IOException($"The pokeldn download doesn't contain {AppFile}.");
            Directory.Move(Path.GetDirectoryName(exe)!, AppDir);
        }
        File.WriteAllText(Path.Combine(AppDir, "shinyforge-version.txt"), rel.Tag);
        AppSettings.Current.PokeldnPath = "";
        AppSettings.Current.Save();

        if (rel.FirmwareUrl != null)
        {
            Directory.CreateDirectory(FirmwareDir);
            var fw = Path.Combine(FirmwareDir, FirmwareAsset);
            await DownloadVerified(rel.FirmwareUrl, fw + ".new", sums.GetValueOrDefault(FirmwareAsset), progress, "ESP32-S3 radio firmware");
            File.Move(fw + ".new", fw, true);
        }
        try { Directory.Delete(tmp, true); } catch { }
        return $"pokeldn {rel.Tag} installed to {AppDir}" + (rel.FirmwareUrl != null ? $", firmware {FirmwareVersionOf(FindFirmware()) ?? "?"} downloaded." : ".");
    }

    /// <summary>Downloads Espressif's esptool (the firmware flasher) into ShinyForge's Tools folder.</summary>
    public static async Task<string> InstallEsptool(Action<string> progress)
    {
        progress("Looking up the latest esptool release...");
        var j = await Http.GetFromJsonAsync<JsonObject>($"https://api.github.com/repos/{EsptoolRepo}/releases/latest")
                ?? throw new IOException("GitHub didn't return the esptool release.");
        var tag = j["tag_name"]?.ToString() ?? "";
        var asset = (j["assets"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(a => a["name"]?.ToString().EndsWith(EsptoolAssetSuffix) == true)
                    ?? throw new IOException($"The esptool release has no {OsInfo.Name} download.");
        var tmp = Path.Combine(Path.GetTempPath(), "ShinyForge-esptool-" + tag);
        Directory.CreateDirectory(tmp);
        var zip = Path.Combine(tmp, asset["name"]!.ToString());
        // GitHub publishes each asset's SHA-256 in its "digest" field ("sha256:...").
        var digest = asset["digest"]?.ToString();
        await DownloadVerified(asset["browser_download_url"]!.ToString(), zip, digest?.StartsWith("sha256:") == true ? digest[7..].ToLowerInvariant() : null, progress, $"esptool {tag}");
        progress("Unpacking esptool...");
        if (Directory.Exists(EsptoolDir)) Directory.Delete(EsptoolDir, true);
        await Unpack(zip, EsptoolDir);
        try { Directory.Delete(tmp, true); } catch { }
        return FindEsptool() is { } exe ? $"esptool {tag} installed ({exe})." : "esptool was unpacked but esptool.exe wasn't found.";
    }

    /// <summary>
    /// Unpacks a .zip or .tar.gz. On macOS "ditto" keeps app bundles intact (symbolic links, permissions); tar keeps
    /// Linux/macOS file permissions.
    /// </summary>
    private static async Task Unpack(string archive, string dest)
    {
        Directory.CreateDirectory(dest);
        if (OsInfo.IsWindows && archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, dest);
            return;
        }
        var psi = OsInfo.IsMac && archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo("ditto") { ArgumentList = { "-x", "-k", archive, dest } }
            : new ProcessStartInfo("tar") { ArgumentList = { archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "-xf" : "-xzf", archive, "-C", dest } };
        psi.UseShellExecute = false;
        psi.RedirectStandardError = true;
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new IOException($"Unpacking {Path.GetFileName(archive)} failed: {err.Trim()}");
    }

    // ------------------------------------------------------------------ the board
    /// <summary>ESP32 boards on USB serial ports (Espressif native USB, CP210x, CH340).</summary>
    public static List<UsbBoard> FindBoards()
    {
        var list = new List<UsbBoard>();
        if (OsInfo.IsLinux)
        {
            // /dev/ttyACM* (native USB) and /dev/ttyUSB* (bridges); the USB ids come from sysfs.
            foreach (var dev in Directory.EnumerateFiles("/dev").Where(d => Path.GetFileName(d).StartsWith("ttyACM") || Path.GetFileName(d).StartsWith("ttyUSB")).Order())
            {
                string Id(string f)
                {
                    foreach (var up in new[] { "..", "../.." })
                        try { return File.ReadAllText(Path.Combine("/sys/class/tty", Path.GetFileName(dev), "device", up, f)).Trim().ToUpperInvariant(); } catch { }
                    return "";
                }
                var vid = Id("idVendor");
                if (vid is "303A" or "10C4" or "1A86") list.Add(new UsbBoard(dev, Id("product") is { Length: > 0 } n ? n : Path.GetFileName(dev), vid, Id("idProduct")));
            }
            return list.OrderByDescending(b => b.NativeUsb).ToList();
        }
        if (OsInfo.IsMac)
        {
            // macOS names the port after the driver: cu.usbmodem* = native USB (ESP32-S3), cu.usbserial / SLAB / wchusbserial = bridges.
            foreach (var dev in Directory.EnumerateFiles("/dev", "cu.*").Order())
            {
                var n = Path.GetFileName(dev);
                if (n.StartsWith("cu.usbmodem")) list.Add(new UsbBoard(dev, n, "303A", ""));
                else if (n.StartsWith("cu.SLAB") || n.StartsWith("cu.usbserial")) list.Add(new UsbBoard(dev, n, "10C4", ""));
                else if (n.StartsWith("cu.wchusbserial")) list.Add(new UsbBoard(dev, n, "1A86", ""));
            }
            return list.OrderByDescending(b => b.NativeUsb).ToList();
        }
        try
        {
            using var q = new ManagementObjectSearcher("SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'");
            foreach (ManagementObject o in q.Get())
            {
                var id = (o["PNPDeviceID"] as string ?? "").ToUpperInvariant();
                var name = o["Name"] as string ?? "";
                string Part(string key) { var i = id.IndexOf(key); return i < 0 ? "" : id.Substring(i + key.Length, 4); }
                var vid = Part("VID_");
                if (vid is not ("303A" or "10C4" or "1A86")) continue;
                var port = System.Text.RegularExpressions.Regex.Match(name, @"\((COM\d+)\)").Groups[1].Value;
                if (port.Length > 0) list.Add(new UsbBoard(port, name, vid, Part("PID_")));
            }
        }
        catch (Exception e) { AppPaths.Log("Board scan failed: " + e.Message); }
        return list.OrderByDescending(b => b.NativeUsb).ToList();
    }

    /// <summary>Asks the board's pokeldn radio firmware who it is (HELLO → INFO "version=x.y.z").</summary>
    public static FirmwareInfo QueryFirmware(string port, int timeoutMs = 4000)
    {
        try
        {
            using var sp = new SerialPort(port, 115200) { ReadTimeout = 200, WriteTimeout = 1000, DtrEnable = false, RtsEnable = false };
            sp.Open();
            Thread.Sleep(1200); // opening the port resets an S3; let the firmware start
            var buf = new List<byte>();
            var end = Environment.TickCount64 + timeoutMs;
            var nextHello = 0L;
            while (Environment.TickCount64 < end)
            {
                if (Environment.TickCount64 >= nextHello)
                {
                    var hello = Frame(0x01, Array.Empty<byte>());
                    sp.Write(new byte[] { 0 }, 0, 1);
                    sp.Write(hello, 0, hello.Length);
                    nextHello = Environment.TickCount64 + 800;
                }
                try { while (sp.BytesToRead > 0) buf.Add((byte)sp.ReadByte()); } catch (TimeoutException) { }
                int zero;
                while ((zero = buf.IndexOf(0)) >= 0)
                {
                    var frame = buf.GetRange(0, zero).ToArray();
                    buf.RemoveRange(0, zero + 1);
                    if (TryDecode(frame, out var type, out var payload) && type == 0x81 && payload.Length >= 14)
                    {
                        var text = Encoding.UTF8.GetString(payload, 14, payload.Length - 14);
                        return new FirmwareInfo(true, ParseVersion(text), text, null);
                    }
                }
                Thread.Sleep(30);
            }
            return new FirmwareInfo(false, null, "", "no pokeldn firmware answered");
        }
        catch (UnauthorizedAccessException) { return new FirmwareInfo(false, null, "", $"{port} is in use by another program (close pokeldn first)"); }
        catch (Exception e) { return new FirmwareInfo(false, null, "", e.Message); }
    }

    /// <summary>"pokeldn-radio esp32s3 version=1.11.0" → "1.11.0" (null when it names none).</summary>
    public static string? ParseVersion(string infoText) =>
        infoText.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(w => w.StartsWith("version="))?["version=".Length..] is { Length: > 0 } v ? v : null;

    /// <summary>The firmware version inside a merged image: the ESP-IDF app descriptor (magic 0xABCD5432 at 0x10020, version at +16).</summary>
    public static string? FirmwareVersionOf(string? bin)
    {
        if (bin == null || !File.Exists(bin)) return null;
        try
        {
            using var f = File.OpenRead(bin);
            if (f.Length < 0x10020 + 48) return null;
            var head = new byte[48];
            f.Position = 0x10020;
            f.ReadExactly(head);
            if (BitConverter.ToUInt32(head, 0) != 0xABCD5432) return null;
            var v = Encoding.ASCII.GetString(head, 16, 32).TrimEnd('\0').Trim();
            return v.TrimStart('v');
        }
        catch { return null; }
    }

    /// <summary>Writes the ESP32-S3 radio firmware to the board with esptool. The board must not be in use.</summary>
    public static async Task<string> FlashFirmware(string port, Action<string> progress)
    {
        var esptool = FindEsptool() ?? throw new IOException("esptool isn't installed yet.");
        var fw = FindFirmware() ?? throw new IOException("The pokeldn firmware file isn't downloaded yet.");
        if (Process.GetProcessesByName("pokeldn").Length > 0)
            throw new IOException("pokeldn is running and may be using the board - close it first.");
        progress($"Installing firmware {FirmwareVersionOf(fw)} on {port} (about a minute - don't unplug the board)...");
        var psi = new ProcessStartInfo(esptool) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--chip", "esp32s3", "--port", port, "--baud", "460800", "write-flash", "0x0", fw }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var log = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data == null) return; log.AppendLine(e.Data); var t = e.Data.Trim(); if (t.Length > 0) progress(t); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        await p.WaitForExitAsync();
        AppPaths.Log("esptool: " + log);
        if (p.ExitCode != 0)
        {
            var last = log.ToString().Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            throw new IOException("Installing the firmware failed: " + last +
                (last.Contains("chip", StringComparison.OrdinalIgnoreCase) ? "\n(This board may not be an ESP32-S3.)" : ""));
        }
        await Task.Delay(1500);
        var info = QueryFirmware(port);
        return info.Answered ? $"Firmware installed - the board answers with version {info.Version}." : "Firmware written. Unplug and re-plug the board if it doesn't answer yet.";
    }

    // ------------------------------------------------------------------ prod.keys
    /// <summary>Which of the needed key names the file lacks (values are never read into memory beyond the line scan or logged).</summary>
    public static List<string> MissingKeys(string path)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadLines(path))
        {
            var eq = line.IndexOf('=');
            if (eq > 0) present.Add(line[..eq].Trim());
        }
        return KeysNeeded.Where(k => !present.Contains(k)).ToList();
    }

    /// <summary>Stores the prod.keys path in ShinyForge's settings and in pokeldn's settings.json ("keys"). The file itself is never copied.</summary>
    public static void SetKeysPath(string path, string? pokeldnDataDir = null)
    {
        if (pokeldnDataDir == null)
        {
            AppSettings.Current.ProdKeysPath = path;
            AppSettings.Current.Save();
        }
        var dir = pokeldnDataDir ?? PokeldnDataDir;
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "settings.json");
        JsonObject o;
        try { o = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject() : new JsonObject(); }
        catch { File.Copy(file, file + ".shinyforge-backup", true); o = new JsonObject(); }
        o["keys"] = path;
        var tmp = file + ".tmp";
        File.WriteAllText(tmp, o.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, file, true);
    }

    public static void Launch()
    {
        var exe = FindApp() ?? throw new IOException("pokeldn isn't installed yet.");
        if (OsInfo.IsMac) { Process.Start(new ProcessStartInfo("open") { ArgumentList = { exe }, UseShellExecute = false }); return; }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = OsInfo.IsWindows, WorkingDirectory = Path.GetDirectoryName(exe)! });
    }

    // ------------------------------------------------------------------ serial framing (pokeldn docs: Serial protocol)
    /// <summary>COBS(type | payload | crc32-le(type | payload)) followed by 0x00.</summary>
    public static byte[] Frame(byte type, byte[] payload)
    {
        var raw = new byte[1 + payload.Length + 4];
        raw[0] = type;
        payload.CopyTo(raw, 1);
        BitConverter.GetBytes(Crc32(raw.AsSpan(0, 1 + payload.Length))).CopyTo(raw, 1 + payload.Length);
        var enc = CobsEncode(raw);
        var framed = new byte[enc.Length + 1];
        enc.CopyTo(framed, 0);
        return framed;
    }

    public static bool TryDecode(byte[] cobs, out byte type, out byte[] payload)
    {
        type = 0; payload = Array.Empty<byte>();
        var raw = CobsDecode(cobs);
        if (raw == null || raw.Length < 5) return false;
        var crc = BitConverter.ToUInt32(raw, raw.Length - 4);
        if (crc != Crc32(raw.AsSpan(0, raw.Length - 4))) return false;
        type = raw[0];
        payload = raw[1..^4];
        return true;
    }

    public static byte[] CobsEncode(ReadOnlySpan<byte> data)
    {
        var o = new List<byte>(data.Length + data.Length / 254 + 2) { 0 };
        int codeAt = 0; byte code = 1;
        foreach (var b in data)
        {
            if (b == 0) { o[codeAt] = code; codeAt = o.Count; o.Add(0); code = 1; continue; }
            o.Add(b); code++;
            if (code == 0xFF) { o[codeAt] = code; codeAt = o.Count; o.Add(0); code = 1; }
        }
        o[codeAt] = code;
        return o.ToArray();
    }

    public static byte[]? CobsDecode(ReadOnlySpan<byte> data)
    {
        var o = new List<byte>(data.Length);
        int i = 0;
        while (i < data.Length)
        {
            int code = data[i++];
            if (code == 0 || i + code - 1 > data.Length) return null;
            for (int k = 1; k < code; k++) o.Add(data[i++]);
            if (code < 0xFF && i < data.Length) o.Add(0);
        }
        return o.ToArray();
    }

    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        var c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    /// <summary>CRC-32/ISO-HDLC (zlib.crc32).</summary>
    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        var c = 0xFFFFFFFFu;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
