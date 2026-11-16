using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ShinyForge.Core;

namespace ShinyForge.Services;

/// <summary>
/// Sets up Streamlabs Desktop for ShinyForge while it is open: one scene per console (Game Boy, Game Boy Color, Game Boy
/// Advance, Nintendo DS, 3DS, Switch 1, Switch 2) with the game capture fitted exactly into the console frame's screen hole(s)
/// and the frame picture on top. Talks to Streamlabs' own API (JSON-RPC over the "slobs" named pipe, Windows only).
/// Existing scenes with a console's name are updated instead of duplicated; existing capture sources are reused.
/// </summary>
public sealed class StreamlabsService : IDisposable
{
    public const string PipeName = "slobs";
    private const int CanvasFallbackW = 1920, CanvasFallbackH = 1080;

    private readonly NamedPipeClientStream _pipe;
    private readonly StreamReader _reader;
    private int _id;

    private StreamlabsService(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _reader = new StreamReader(pipe, new UTF8Encoding(false));
    }

    /// <summary>Is Streamlabs Desktop open (its API pipe exists)?</summary>
    public static bool IsOpen => OsInfo.IsWindows && File.Exists(@"\\.\pipe\" + PipeName);

    public static StreamlabsService Connect()
    {
        if (!IsOpen) throw new InvalidOperationException("Streamlabs Desktop isn't open.");
        var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut);
        pipe.Connect(5000);
        return new StreamlabsService(pipe);
    }

    public void Dispose() { _reader.Dispose(); _pipe.Dispose(); }

    /// <summary>One API call: <c>resource.method(args)</c>.</summary>
    public JsonNode? Call(string resource, string method, params object?[] args)
    {
        int id = ++_id;
        var req = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method,
            ["params"] = new JsonObject { ["resource"] = resource, ["args"] = JsonSerializer.SerializeToNode(args) },
        };
        var bytes = Encoding.UTF8.GetBytes(req.ToJsonString() + "\n");
        _pipe.Write(bytes, 0, bytes.Length);
        _pipe.Flush();
        while (true)
        {
            var line = _reader.ReadLine() ?? throw new IOException("Streamlabs closed the connection.");
            if (line.Length == 0) continue;
            var msg = JsonNode.Parse(line);
            if (msg?["id"]?.GetValueKind() != JsonValueKind.Number || (int)msg["id"]! != id) continue; // events
            if (msg["error"] is { } err) throw new InvalidOperationException($"Streamlabs {resource}.{method}: {err["message"] ?? err}");
            return msg["result"];
        }
    }

    // ------------------------------------------------------------------ console scenes
    private sealed record Screen(string Kind, (int L, int T, int R, int B) Border);   // Kind: top | bottom | switch1 | switch2
    private sealed record ConsoleScene(string Name, string[] Aliases, string FrameKey, Screen[] Screens);

    private static readonly ConsoleScene[] Scenes =
    {
        new("Game Boy", Array.Empty<string>(), "gb", new[] { new Screen("top", RecordingService.TopScreenBorder(Platform.GB)) }),
        new("Game Boy Color", Array.Empty<string>(), "gbc", new[] { new Screen("top", RecordingService.TopScreenBorder(Platform.GBC)) }),
        new("Game Boy Advance", Array.Empty<string>(), "gba", new[] { new Screen("top", RecordingService.TopScreenBorder(Platform.GBA)) }),
        new("Nintendo DS", new[] { "DS" }, "nds", new[] { new Screen("top", RecordingService.TopScreenBorder(Platform.NDS)), new Screen("bottom", default) }),
        new("3DS", new[] { "Nintendo 3DS" }, "3ds", new[] { new Screen("top", default), new Screen("bottom", default) }),
        new("Switch 1", new[] { "Switch", "Nintendo Switch", "Switch One" }, "switch1", new[] { new Screen("switch1", default) }),
        new("Switch 2", new[] { "Nintendo Switch 2" }, "switch2", new[] { new Screen("switch2", default) }),
    };

    /// <summary>
    /// Creates / updates the console scenes. <paramref name="switch1Device"/> / <paramref name="switch2Device"/>: the capture
    /// device (DirectShow name) for each Switch, or null to skip that scene when it doesn't exist yet. Returns a report.
    /// </summary>
    public List<string> SetUpConsoleScenes(string? switch1Device, string? switch2Device)
    {
        var report = new List<string>();
        // canvases: horizontal (the normal one) and vertical (Streamlabs Dual Output keeps a copy of every item for it)
        var canvases = new List<(string Display, int W, int H)>();
        JsonNode? both = null;
        try { both = Call("VideoSettingsService", "baseResolutions"); } catch { /* older Streamlabs */ }
        foreach (var d in new[] { "horizontal", "vertical" })
            if (both?[d] is { } r) canvases.Add((d, (int?)r["baseWidth"] ?? CanvasFallbackW, (int?)r["baseHeight"] ?? CanvasFallbackH));
        if (canvases.Count == 0)
        {
            var res = Call("VideoSettingsService", "baseResolution");
            canvases.Add(("horizontal", (int?)res?["baseWidth"] ?? CanvasFallbackW, (int?)res?["baseHeight"] ?? CanvasFallbackH));
        }
        var sources = Call("SourcesService", "getSources")!.AsArray().Select(n => n!).ToList();
        var scenes = Call("ScenesService", "getScenes")!.AsArray().Select(n => n!).ToList();

        // ---- capture sources (found or made once, shared by all scenes)
        var top = WindowSource(sources, "3DS Capture - Top", "3DS Top", report);
        var bottom = WindowSource(sources, "3DS Capture - Bottom", "3DS Bottom", report);
        var topSize = RecordingService.FindWindow("3DS Capture - Top") is { } t && t.Width > 0 ? (t.Width, t.Height) : (400, 240);
        var bottomSize = RecordingService.FindWindow("3DS Capture - Bottom") is { } b && b.Width > 0 ? (b.Width, b.Height) : (320, 240);

        foreach (var c in Scenes)
        {
            var frame = ConsoleFrames.Get(c.FrameKey)!;
            if (!ConsoleFrames.Exists(frame)) { report.Add($"{c.Name}: skipped - {ConsoleFrames.PathOf(frame)} is missing"); continue; }
            var scene = scenes.FirstOrDefault(s => new[] { c.Name }.Concat(c.Aliases).Any(n => string.Equals(((string?)s["name"])?.Trim(), n, StringComparison.OrdinalIgnoreCase)));

            // what goes into each hole
            var holes = new List<(JsonNode Source, int W, int H, (int L, int T, int R, int B) Crop, FrameScreen Hole)>();
            bool missing = false;
            for (int k = 0; k < c.Screens.Length; k++)
            {
                var sc = c.Screens[k];
                JsonNode? src; int w, h;
                switch (sc.Kind)
                {
                    case "top": src = top; (w, h) = topSize; break;
                    case "bottom": src = bottom; (w, h) = bottomSize; break;
                    default:
                        var device = sc.Kind == "switch1" ? switch1Device : switch2Device;
                        // a scene that already shows a capture card keeps it; otherwise the chosen device
                        src = SceneDeviceSource(scene, sources) ?? DeviceSource(sources, device, sc.Kind == "switch1" ? "Switch 1 Capture" : "Switch 2 Capture", report);
                        w = (int?)src?["width"] is int sw and > 0 ? sw : 1920;
                        h = (int?)src?["height"] is int sh and > 0 ? sh : 1080;
                        break;
                }
                if (src == null) { missing = true; break; }
                // the border is measured on a 400x240 screen: scale it to the window's real size
                var crop = (sc.Border.L * w / 400, sc.Border.T * h / 240, sc.Border.R * w / 400, sc.Border.B * h / 240);
                holes.Add((src, w, h, crop, frame.Screens[k]));
            }
            if (missing) { report.Add($"{c.Name}: skipped - choose its capture device first"); continue; }

            if (scene == null)
            {
                scene = Call("ScenesService", "createScene", c.Name)!;
                scenes.Add(scene);
                report.Add($"{c.Name}: scene created");
            }
            else report.Add($"{(string?)scene["name"]}: scene updated");
            var sceneId = (string)scene["id"]!;
            var sceneRes = $"Scene[\"{sceneId}\"]";

            // the frame picture: an image source showing this PNG (any name), or a new "<scene> Frame"
            var framePath = ConsoleFrames.PathOf(frame);
            var frameSrc = FrameSource(sources, framePath) ?? sources.FirstOrDefault(x => (string?)x["name"] == c.Name + " Frame" && (string?)x["type"] == "image_source");
            if (frameSrc == null)
            {
                frameSrc = Call("SourcesService", "createSource", c.Name + " Frame", "image_source", new { file = framePath }, new { })!;
                sources.Add(frameSrc);
            }
            else Call($"Source[\"{(string)frameSrc["sourceId"]!}\"]", "updateSettings", new { file = framePath });
            var frameId = (string)frameSrc["sourceId"]!;

            // make sure every source is in the scene (Streamlabs adds the vertical-canvas copy itself)
            foreach (var id in holes.Select(hl => (string)hl.Source["sourceId"]!).Append(frameId).Distinct())
                if (!Items(sceneRes).Any(i => (string?)i["sourceId"] == id && Display(i) == "horizontal"))
                    Call(sceneRes, "addSource", id);

            // lay out each canvas: the normal (horizontal) one and, with Dual Output, the vertical one
            foreach (var (display, cw, ch) in canvases)
            {
                var items = Items(sceneRes).Where(i => Display(i) == display).ToList();
                if (items.Count == 0) continue;
                // fit the console into the canvas, centred
                double s = Math.Min((double)cw / frame.Width, (double)ch / frame.Height);
                double fx = (cw - frame.Width * s) / 2, fy = (ch - frame.Height * s) / 2;
                var screenItems = new List<string>();
                foreach (var (src, w, h, crop, hole) in holes)
                {
                    int vw = w - crop.Item1 - crop.Item3, vh = h - crop.Item2 - crop.Item4;
                    // 1 canvas pixel bigger on every side so no gap shows; the frame's edge covers the extra
                    double x = fx + hole.X * s - 1, y = fy + hole.Y * s - 1, ww = hole.Width * s + 2, hh = hole.Height * s + 2;
                    var transform = new
                    {
                        position = new { x, y },
                        scale = new { x = ww / vw, y = hh / vh },
                        crop = new { left = crop.Item1, top = crop.Item2, right = crop.Item3, bottom = crop.Item4 },
                        rotation = 0,
                    };
                    foreach (var it in items.Where(i => (string?)i["sourceId"] == (string)src["sourceId"]!))
                    {
                        Call(ItemRes(sceneId, it), "setTransform", transform);
                        Call(ItemRes(sceneId, it), "setVisibility", true);
                        screenItems.Add((string)it["sceneItemId"]!);
                    }
                }
                var frameItems = items.Where(i => (string?)i["sourceId"] == frameId).ToList();
                foreach (var fi in frameItems)
                    Call(ItemRes(sceneId, fi), "setTransform", new { position = new { x = fx, y = fy }, scale = new { x = s, y = s }, crop = new { left = 0, top = 0, right = 0, bottom = 0 }, rotation = 0 });
                if (frameItems.Count == 0 || screenItems.Count == 0) continue;
                // the frame directly above the highest screen (a webcam etc. above it stays above); nodes are top layer first
                var ids = items.Select(i => (string)i["sceneItemId"]!).ToHashSet();
                var order = Call(sceneRes, "getNodes")!.AsArray().Select(n => (string)n!["id"]!).Where(ids.Contains).ToList();
                var highest = order.First(screenItems.Contains);
                var mainFrame = (string)frameItems[0]["sceneItemId"]!;
                if (order.IndexOf(mainFrame) != order.IndexOf(highest) - 1)
                    Call(ItemRes(sceneId, frameItems[0]), "placeBefore", highest);
            }
        }
        return report;
    }

    private static string Display(JsonNode item) => (string?)item["display"] ?? "horizontal";

    /// <summary>An image source that already shows <paramref name="path"/>.</summary>
    private JsonNode? FrameSource(List<JsonNode> sources, string path)
    {
        foreach (var src in sources.Where(x => (string?)x["type"] == "image_source"))
        {
            var file = (string?)Call($"Source[\"{(string)src["sourceId"]!}\"]", "getSettings")?["file"];
            if (file != null && string.Equals(Path.GetFullPath(file), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) return src;
        }
        return null;
    }

    private IEnumerable<JsonNode> Items(string sceneRes) => Call(sceneRes, "getItems")!.AsArray().Select(n => n!);

    private static string ItemRes(string sceneId, JsonNode item) => $"SceneItem[\"{sceneId}\",\"{(string)item["sceneItemId"]!}\",\"{(string)item["sourceId"]!}\"]";

    /// <summary>A Window Capture of a 3DS Capture window: an existing one, or a new one for the window as it is now.</summary>
    private JsonNode? WindowSource(List<JsonNode> sources, string window, string name, List<string> report)
    {
        foreach (var src in sources.Where(x => (string?)x["type"] == "window_capture"))
        {
            var settings = Call($"Source[\"{(string)src["sourceId"]!}\"]", "getSettings");
            var w = Regex.Replace((string?)settings?["window"] ?? "", "#3A", ":");
            if (w.StartsWith(window + ":") || w.StartsWith(window + " ")) return src;
        }
        // the title as 3DS Capture shows it right now (with a 3DS plugged in it ends with the board id); ':' is written #3A
        var title = RecordingService.WindowTitle(window).Replace(":", "#3A");
        var made = Call("SourcesService", "createSource", name, "window_capture",
            new { window = $"{title}:Capture DisplayWindow:3ds_capture.exe", cursor = false }, new { })!;
        sources.Add(made);
        report.Add($"Window Capture \"{name}\" created");
        return made;
    }

    /// <summary>The capture-card source already in a scene (when updating a scene that has one).</summary>
    private JsonNode? SceneDeviceSource(JsonNode? scene, List<JsonNode> sources)
    {
        if (scene == null) return null;
        // the lowest capture-card layer of the scene's own items (webcams are left alone)
        var ids = Items($"Scene[\"{(string)scene["id"]!}\"]").Where(i => Display(i) == "horizontal").Select(i => (string?)i["sourceId"]).Reverse().ToList();
        foreach (var id in ids)
            if (sources.FirstOrDefault(s => (string?)s["sourceId"] == id) is { } s && (string?)s["type"] == "dshow_input"
                && !((string?)s["name"] ?? "").Contains("cam", StringComparison.OrdinalIgnoreCase))
                return s;
        return null;
    }

    /// <summary>A Video Capture Device source for <paramref name="device"/>: an existing one, or a new one.</summary>
    private JsonNode? DeviceSource(List<JsonNode> sources, string? device, string name, List<string> report)
    {
        if (string.IsNullOrWhiteSpace(device)) return null;
        foreach (var src in sources.Where(x => (string?)x["type"] == "dshow_input"))
        {
            var settings = Call($"Source[\"{(string)src["sourceId"]!}\"]", "getSettings");
            if (((string?)settings?["video_device_id"] ?? "").StartsWith(device + ":")) return src;
        }
        var made = Call("SourcesService", "createSource", name, "dshow_input",
            new { video_device_id = DeviceId(device), res_type = 0, audio_output_mode = 1 }, new { })!;
        sources.Add(made);
        report.Add($"Video Capture Device \"{name}\" ({device}) created");
        return made;
    }

    /// <summary>Streamlabs/OBS device id: "name:path", with the path from FFmpeg's "Alternative name" ('#' written #22, ':' #3A).</summary>
    private static string DeviceId(string device)
    {
        var path = RecordingService.DevicePath(device);
        return path == null ? device + ":" : device.Replace(":", "#3A") + ":" + path.Replace("#", "#22").Replace(":", "#3A");
    }
}
