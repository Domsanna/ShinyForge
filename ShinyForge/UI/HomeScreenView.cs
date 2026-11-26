using ShinyForge.Services.HomeSorter;

namespace ShinyForge.UI;

/// <summary>
/// The console's screen, live, from the HOME capture card (about 10 pictures a second). It shares the one card reader with
/// the HOME sorter (<see cref="HomeCapture"/>), so watching never blocks sorting. Starts when shown, lets go of the card when
/// hidden or closed. Click it to try again after a problem (e.g. Streamlabs had the card).
/// </summary>
public sealed class HomeScreenView : Panel
{
    private readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _message = new()
    {
        Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, ForeColor = Theme.Muted, BackColor = Color.Black,
        Font = Theme.Body, Padding = new Padding(20), UseMnemonic = false,
    };
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 100 };
    private HomeCapture? _cam;
    private DateTime _shown;
    private bool _starting;
    private int _waits;

    public HomeScreenView()
    {
        BackColor = Color.Black;
        Controls.Add(_picture);
        Controls.Add(_message);
        _message.BringToFront();
        _message.Text = "Starting the capture card...";
        _tick.Tick += (_, _) => Refresh_();
        _message.Click += (_, _) => Restart();
        _picture.Click += (_, _) => Restart();
        Cursor = Cursors.Hand;
        VisibleChanged += (_, _) => { if (Visible && IsHandleCreated) Begin(); else Stop(); };
        HandleCreated += (_, _) => { if (Visible) Begin(); };
    }

    private void Restart()
    {
        if (_cam != null && !_cam.Stopped && _message.Visible == false) return;
        Stop();
        Begin();
    }

    private void Begin()
    {
        if (_cam != null || _starting) return;
        _starting = true;
        _waits = 0;
        Show("Starting the capture card...");
        Task.Run(() =>
        {
            try
            {
                var device = HomeCapture.DefaultDevice();
                return device == null ? (null, "No capture card was found.") : (HomeCapture.Start(device), (string?)null);
            }
            catch (Exception e) { return ((HomeCapture?)null, e.Message); }
        }).ContinueWith(t =>
        {
            _starting = false;
            var (cam, error) = t.Result;
            if (IsDisposed || !Visible) { cam?.Dispose(); return; }
            if (cam == null) { Show(error + "\n\nClick to try again."); return; }
            _cam = cam;
            _tick.Start();
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void Refresh_()
    {
        if (_cam == null) return;
        if (_cam.Stopped)
        {
            var why = _cam.NoPictureReason();
            Stop();
            Show(why + "\n\nClick to try again.");
            return;
        }
        var at = _cam.LastAt;
        if (at == _shown)
        {
            if (++_waits == 150 && _shown == default) Show(_cam.NoPictureReason()); // nothing for 15 s
            return;
        }
        if (_cam.LastJpeg() is not { } jpg) return;
        try
        {
            using var ms = new MemoryStream(jpg);
            using var decoded = Image.FromStream(ms);
            var bmp = new Bitmap(decoded); // a copy that doesn't need the stream
            var old = _picture.Image;
            _picture.Image = bmp;
            old?.Dispose();
            _shown = at;
            if (_message.Visible) _message.Visible = false;
        }
        catch (ArgumentException) { } // a broken picture: skip it
    }

    private void Show(string text)
    {
        _message.Text = text;
        _message.Visible = true;
        _message.BringToFront();
    }

    private void Stop()
    {
        _tick.Stop();
        _cam?.Dispose(); // shared: the card stays open while the sorter still uses it
        _cam = null;
        _shown = default;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            _tick.Dispose();
            _picture.Image?.Dispose();
        }
        base.Dispose(disposing);
    }
}
