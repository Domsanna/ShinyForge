using System.Text;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// The Python hunter's display inside ShinyForge: its live text output, the picture it sees (vision mode) and the
/// keep hunting / stop question it asks after a find. Everything is read from files in the bot folder, so a hunt that kept
/// running while ShinyForge was closed shows up again when ShinyForge comes back.
/// </summary>
public sealed class HuntConsolePanel : Panel
{
    private const int MaxChars = 200_000, AttachBytes = 64 * 1024, ReadBytes = 256 * 1024;

    private readonly TextBox _text;
    private readonly PictureBox _picture;
    private readonly Panel _question;
    private readonly CheckBox _follow;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 500 };
    private readonly StringBuilder _held = new();
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private long _offset = -1;
    private int _run = -1;
    private DateTime _pictureTime;

    public HuntConsolePanel()
    {
        BackColor = Theme.Panel;

        _text = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both,
            MaxLength = int.MaxValue,
        };
        Theme.StyleInput(_text);
        _text.Font = Theme.Mono;

        _picture = new PictureBox { Dock = DockStyle.Right, Width = 384, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Visible = false };

        _follow = new CheckBox
        {
            Dock = DockStyle.Bottom, Height = 28, Text = "Follow new output", Checked = true,
            ForeColor = Theme.Muted, BackColor = Theme.Panel, Font = Theme.Body,
        };
        _follow.CheckedChanged += (_, _) =>
        {
            if (!_follow.Checked || _held.Length == 0) return;
            var waiting = _held.ToString();
            _held.Clear();
            Append(waiting);
        };

        _question = BuildQuestion();

        // docking works from the last control added to the first: the text takes what the others leave
        Controls.Add(_text);
        Controls.Add(_picture);
        Controls.Add(_follow);
        Controls.Add(_question);

        _timer.Tick += (_, _) => Tick();
        _timer.Start();
        Disposed += (_, _) =>
        {
            _timer.Dispose();
            _picture.Image?.Dispose();
        };
    }

    private Panel BuildQuestion()
    {
        var bar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 50, FlowDirection = FlowDirection.LeftToRight, WrapContents = false,
            BackColor = Theme.PanelHi, Visible = false, Padding = new Padding(6),
        };
        var keep = new FancyButton("Keep hunting", Theme.GlyphPlay, Theme.Accent) { Margin = new Padding(0, 0, 6, 0) };
        var stop = new FancyButton("Stop hunter", Theme.GlyphStop, Color.FromArgb(150, 40, 60)) { Margin = new Padding(0, 0, 12, 0) };
        var text = new Label
        {
            AutoSize = true, ForeColor = Theme.Text, Font = Theme.Body, BackColor = Theme.PanelHi, Margin = new Padding(0, 9, 0, 0),
            Text = "The hunter found something and stopped pressing buttons. Take over with your controller.",
        };
        keep.Click += (_, _) =>
        {
            // this used to take a typed answer: one click must not reset the game on a Pokémon that hasn't been caught
            if (MessageBox.Show(FindForm(), "Keep hunting now? The hunter may reset the game, so anything you haven't caught and saved will be lost.",
                    "Keep hunting", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            bar.Visible = false;
            HuntService.AnswerHunter(keepHunting: true);
        };
        stop.Click += (_, _) =>
        {
            bar.Visible = false;
            HuntService.AnswerHunter(keepHunting: false);
        };
        bar.Controls.AddRange(new Control[] { keep, stop, text });
        return bar;
    }

    private void Tick()
    {
        if (!Visible) return; // another page is showing: it catches up when it's shown again
        ReadNewOutput();
        _question.Visible = HuntService.IsWaitingForAnswer;
        ShowPicture();
    }

    private void ReadNewOutput()
    {
        if (HuntService.ConsoleLogPath is not { } path || !File.Exists(path)) return;
        if (_run != HuntService.ConsoleRun)
        {
            _run = HuntService.ConsoleRun; // a new hunt started a new log
            Restart();
        }
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < _offset) Restart();
            var start = _offset < 0 ? Math.Max(0, fs.Length - AttachBytes) : _offset;
            var startsMidLine = _offset < 0 && start > 0;
            _offset = start;
            if (start >= fs.Length) return;

            fs.Seek(start, SeekOrigin.Begin);
            var bytes = new byte[Math.Min(fs.Length - start, ReadBytes)];
            var count = fs.Read(bytes, 0, bytes.Length);
            _offset = start + count;
            var chars = new char[_decoder.GetCharCount(bytes, 0, count)];
            var text = new string(chars, 0, _decoder.GetChars(bytes, 0, count, chars, 0));
            if (startsMidLine && text.IndexOf('\n') is >= 0 and var lineEnd) text = text[(lineEnd + 1)..];
            Append(text.Replace("\r", "").Replace("\n", "\r\n"));
        }
        catch (IOException) { /* the log is being replaced: read it on the next tick */ }
        catch (UnauthorizedAccessException) { }
    }

    private void Restart()
    {
        _offset = -1;
        _decoder.Reset();
        _held.Clear();
        _text.Clear();
    }

    private void Append(string text)
    {
        if (text.Length == 0) return;
        if (!_follow.Checked)
        {
            _held.Append(text);
            if (_held.Length > MaxChars) _held.Remove(0, _held.Length - MaxChars);
            return;
        }
        if (_text.TextLength + text.Length > MaxChars) _text.Text = _text.Text[Math.Max(0, _text.TextLength - MaxChars / 2)..];
        _text.AppendText(text);
    }

    private void ShowPicture()
    {
        if (HuntService.PreviewPath is not { } path || !File.Exists(path))
        {
            if (!_picture.Visible) return;
            _picture.Visible = false;
            _picture.Image?.Dispose();
            _picture.Image = null;
            _pictureTime = default;
            return;
        }
        try
        {
            var written = File.GetLastWriteTimeUtc(path);
            if (written == _pictureTime) return;
            using var stream = new MemoryStream(File.ReadAllBytes(path));
            using var decoded = Image.FromStream(stream);
            var old = _picture.Image;
            _picture.Image = new Bitmap(decoded);
            old?.Dispose();
            _picture.Visible = true;
            _pictureTime = written;
        }
        catch (IOException) { /* the bot is replacing the picture: the next tick shows the new one */ }
        catch (UnauthorizedAccessException) { }
        catch (ArgumentException) { /* not a whole picture yet */ }
    }
}
