using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// "Who is using the software?" came up on the Switch: shows that screen and lets the user pick who plays
/// (one button per Switch user, left to right). Used by <see cref="SwitchGameLauncher.ChooseUser"/>; called from a
/// background thread, shown on the UI thread.
/// </summary>
public static class SwitchUserPrompt
{
    public static int? Ask(byte[] jpeg, int users)
    {
        var owner = Application.OpenForms.Count > 0 ? Application.OpenForms[0] : null;
        if (owner == null || owner.IsDisposed) return 0;
        return (int?)owner.Invoke(() => Show(owner, jpeg, users));
    }

    private static int? Show(IWin32Window owner, byte[] jpeg, int users)
    {
        using var ms = new MemoryStream(jpeg);
        using var picture = Image.FromStream(ms);
        using var f = new Form
        {
            Text = "ShinyForge - who is playing?", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
            StartPosition = FormStartPosition.CenterParent, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = Theme.Body,
            ClientSize = new Size(680, 520), ShowInTaskbar = false,
        };
        var text = new Label
        {
            Text = "The Switch asks \"Who is using the software?\". Which user should play the game?",
            Dock = DockStyle.Top, Height = 40, Padding = new Padding(12, 12, 12, 0), ForeColor = Theme.Text,
        };
        var pic = new PictureBox { Image = picture, SizeMode = PictureBoxSizeMode.Zoom, Dock = DockStyle.Top, Height = 382, BackColor = Color.Black };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 12, 10, 8), BackColor = Theme.Panel };
        int? chosen = null;
        for (var i = 0; i < users; i++)
        {
            var n = i;
            var b = new FancyButton(n switch { 0 => "1st user (left)", 1 => "2nd user", 2 => "3rd user", _ => $"{n + 1}th user" },
                Theme.GlyphPlay, n == 0 ? Theme.Accent : Theme.PanelHi) { Height = 36, Margin = new Padding(4) };
            b.Click += (_, _) => { chosen = n; f.DialogResult = DialogResult.OK; };
            buttons.Controls.Add(b);
        }
        var cancel = new FancyButton("Cancel", Theme.GlyphStop, Theme.PanelHi) { Height = 36, Margin = new Padding(4) };
        cancel.Click += (_, _) => f.DialogResult = DialogResult.Cancel;
        buttons.Controls.Add(cancel);
        f.Controls.Add(buttons);
        f.Controls.Add(pic);
        f.Controls.Add(text);
        return f.ShowDialog(owner) == DialogResult.OK ? chosen : null;
    }
}
