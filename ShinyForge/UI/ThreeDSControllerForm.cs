using ShinyForge.Core;
using ShinyForge.Services.ThreeDS;

namespace ShinyForge.UI;

/// <summary>
/// Play the 3DS from the PC with TPPFLUSH: click the buttons or use the keyboard, click the touch screen area to tap it.
/// Keys: arrows = D-pad, WASD = circle pad, X/Z/S/A... see the help line. Closing the window gives the buttons back to the 3DS.
/// </summary>
public sealed class ThreeDSControllerForm : Form
{
    private readonly TppflushController _ctl;
    private readonly Label _status = new() { AutoSize = true, ForeColor = Theme.Muted, Font = Theme.Small };
    private readonly HashSet<Keys> _down = new();

    // keyboard -> 3DS button (letters chosen like most emulators: X = A, Z = B, S = X, A = Y)
    private static readonly Dictionary<Keys, string> KeyMap = new()
    {
        [Keys.X] = "A", [Keys.Z] = "B", [Keys.S] = "X", [Keys.A] = "Y", [Keys.Q] = "L", [Keys.E] = "R",
        [Keys.D1] = "ZL", [Keys.D3] = "ZR", [Keys.Enter] = "START", [Keys.Back] = "SELECT",
        [Keys.Up] = "DPADUP", [Keys.Down] = "DPADDOWN", [Keys.Left] = "DPADLEFT", [Keys.Right] = "DPADRIGHT", [Keys.H] = "HOME",
    };
    private static readonly Keys[] PadKeys = { Keys.I, Keys.J, Keys.K, Keys.L };

    public ThreeDSControllerForm(string host)
    {
        _ctl = new TppflushController(host);
        Text = $"ShinyForge - 3DS controller ({host})";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(760, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        KeyPreview = true;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var help = Theme.Label("Luma3DS: open Rosalina (L + Down + Select) → InputRedirection. Keyboard: arrows = D-pad, I J K L = circle pad, " +
                               "X = A, Z = B, S = X, A = Y, Q / E = L / R, 1 / 3 = ZL / ZR, Enter = START, Backspace = SELECT, H = HOME. " +
                               "Click the grey screen to tap the touch screen.", Theme.Small, Theme.Muted);
        help.AutoSize = false;
        help.Bounds = new Rectangle(14, 10, 730, 36);
        Controls.Add(help);

        // touch screen (320 x 240, same size as the 3DS bottom screen)
        var touch = new Panel { Bounds = new Rectangle(220, 56, 320, 240), BackColor = Theme.PanelHi, Cursor = Cursors.Cross };
        touch.Paint += (_, e) => TextRenderer.DrawText(e.Graphics, "Touch screen\n(click to tap)", Theme.Small, touch.ClientRectangle, Theme.Muted,
                                                     TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        touch.MouseDown += (_, e) => Do(() => _ctl.Touch(e.X, e.Y, 0.15), $"touch {e.X}, {e.Y}");
        Controls.Add(touch);

        // D-pad (left) and A B X Y (right), L / R / ZL / ZR along the top, START / SELECT / HOME below the screen
        AddButton("▲", "DPADUP", 70, 110); AddButton("◀", "DPADLEFT", 20, 150); AddButton("▶", "DPADRIGHT", 120, 150); AddButton("▼", "DPADDOWN", 70, 190);
        AddButton("X", "X", 630, 110); AddButton("Y", "Y", 580, 150); AddButton("A", "A", 680, 150); AddButton("B", "B", 630, 190);
        AddButton("L", "L", 20, 60, 90); AddButton("ZL", "ZL", 120, 60, 70); AddButton("ZR", "ZR", 570, 60, 70); AddButton("R", "R", 650, 60, 90);
        AddButton("SELECT", "SELECT", 220, 310, 100); AddButton("HOME", "HOME", 330, 310, 100); AddButton("START", "START", 440, 310, 100);

        // circle pad: a small square, click where the stick should point (it springs back to the centre)
        var pad = new Panel { Bounds = new Rectangle(40, 250, 110, 110), BackColor = Theme.Panel, Cursor = Cursors.Hand };
        pad.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Border, 2);
            e.Graphics.DrawEllipse(pen, 4, 4, 100, 100);
            TextRenderer.DrawText(e.Graphics, "Circle\npad", Theme.Small, pad.ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        };
        pad.MouseDown += (_, e) => Do(() => _ctl.CirclePad((e.X - 55) / 50.0, (55 - e.Y) / 50.0), "circle pad");
        pad.MouseUp += (_, _) => Do(() => _ctl.CirclePad(0, 0), "circle pad centre");
        Controls.Add(pad);

        var release = new FancyButton("Let go of everything", Theme.GlyphStop, Theme.PanelHi) { Location = new Point(560, 250), Width = 180 };
        release.Click += (_, _) => Do(() => _ctl.Send("clear"), "clear");
        Controls.Add(release);
        _status.Location = new Point(14, 440);
        Controls.Add(_status);

        _ctl.Output += line => BeginInvokeSafe(() => _status.Text = line == "ready" ? $"Connected to {host} (TPPFLUSH). The 3DS's own buttons work again when this window closes." : line);
        Shown += (_, _) => Do(_ctl.Start, "start");
        Deactivate += (_, _) => ReleaseKeys();
        FormClosed += (_, _) => _ctl.Dispose();
    }

    private void AddButton(string text, string button, int x, int y, int width = 46)
    {
        var b = new Button
        {
            Text = text, Bounds = new Rectangle(x, y, width, 38), FlatStyle = FlatStyle.Flat, BackColor = Theme.PanelHi, ForeColor = Theme.Text,
            Font = new Font("Segoe UI Semibold", 10f), TabStop = false,
        };
        b.FlatAppearance.BorderColor = Theme.Border;
        // held while the mouse button is down, like the real button
        b.MouseDown += (_, _) => Do(() => _ctl.Hold(button), "hold " + button);
        b.MouseUp += (_, _) => Do(() => _ctl.Release(button), "release " + button);
        Controls.Add(b);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // arrows / Enter / Backspace never reach KeyDown when a control has the focus - key presses are handled here,
        // key releases in OnKeyUp (KeyPreview sends them to the form)
        const int WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104;
        if (msg.Msg is WM_KEYDOWN or WM_SYSKEYDOWN)
        {
            var key = keyData & Keys.KeyCode;
            if (KeyMap.ContainsKey(key) || PadKeys.Contains(key))
            {
                if (_down.Add(key)) Apply(key, true);
                return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        KeyReleased(e.KeyCode);
    }

    private void KeyReleased(Keys key)
    {
        if (_down.Remove(key)) Apply(key, false);
    }

    private void Apply(Keys key, bool down)
    {
        if (KeyMap.TryGetValue(key, out var button))
        {
            Do(() => { if (down) _ctl.Hold(button); else _ctl.Release(button); }, (down ? "hold " : "release ") + button);
            return;
        }
        // circle pad from I J K L
        double x = (_down.Contains(Keys.L) ? 1 : 0) - (_down.Contains(Keys.J) ? 1 : 0);
        double y = (_down.Contains(Keys.I) ? 1 : 0) - (_down.Contains(Keys.K) ? 1 : 0);
        Do(() => _ctl.CirclePad(x, y), "circle pad");
    }

    private void ReleaseKeys()
    {
        if (_down.Count == 0) return;
        _down.Clear();
        Do(() => _ctl.Send("clear"), "clear");
    }

    private void Do(Action a, string what)
    {
        try { a(); }
        catch (Exception ex) { _status.Text = $"{what}: {ex.Message}"; }
    }

    private void BeginInvokeSafe(Action a)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(a); } catch (InvalidOperationException) { }
    }
}
