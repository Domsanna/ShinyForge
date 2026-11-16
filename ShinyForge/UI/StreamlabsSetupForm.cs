using System.Diagnostics;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// "Set up Streamlabs scenes": makes one Streamlabs scene per console (Game Boy … Switch 2) with the game fitted into the
/// console frame. Streamlabs Desktop has to be open; the Switch capture devices are chosen here.
/// </summary>
public sealed class StreamlabsSetupForm : Form
{
    private const string Skip = "(skip - no capture device for this Switch)";

    public StreamlabsSetupForm(IReadOnlyList<string> videoDevices)
    {
        Text = "ShinyForge - Streamlabs console scenes";
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(640, 470);

        var title = Theme.Label("Create the console scenes in Streamlabs", Theme.H2);
        title.Location = new Point(20, 16);
        var help = Theme.Label("Game Boy, Game Boy Color, Game Boy Advance, Nintendo DS, 3DS, Switch 1 and Switch 2 - each with the\n" +
                               "game fitted into the console picture. Scenes that already exist are updated, not duplicated,\n" +
                               "and the capture sources you already have are reused. 3DS games use the 3DS Capture windows.", Theme.Small, Theme.Muted);
        help.Location = new Point(22, 52);

        var devices = new List<string> { Skip };
        devices.AddRange(videoDevices);
        ComboBox Pick(int y, string label, string? guess)
        {
            var l = Theme.Label(label, Theme.Body, Theme.Text);
            l.Location = new Point(22, y);
            var c = new ComboBox { Location = new Point(24, y + 28), Width = 590, DropDownStyle = ComboBoxStyle.DropDownList };
            c.Items.AddRange(devices.Cast<object>().ToArray());
            c.SelectedItem = guess != null && devices.Contains(guess) ? guess : Skip;
            Controls.AddRange(new Control[] { l, c });
            return c;
        }
        var sw1 = Pick(128, "Switch 1 capture device", videoDevices.FirstOrDefault(d => d.Contains("Game Capture", StringComparison.OrdinalIgnoreCase) || d.Contains("Elgato", StringComparison.OrdinalIgnoreCase)));
        var sw2 = Pick(196, "Switch 2 capture device", videoDevices.FirstOrDefault(d => d.Contains("USB Video", StringComparison.OrdinalIgnoreCase)));
        var note = Theme.Label("A Switch scene that already shows a capture card keeps it.", Theme.Small, Theme.Muted);
        note.Location = new Point(22, 262);

        var log = new TextBox { Location = new Point(24, 292), Size = new Size(590, 110), Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        Theme.StyleInput(log);
        var go = new FancyButton("Create scenes", Theme.GlyphCheck, Theme.Accent) { Location = new Point(330, 414) };
        var close = new FancyButton("Close", "", Theme.PanelHi) { Location = new Point(500, 414) };
        close.Click += (_, _) => Close();
        go.Click += async (_, _) =>
        {
            if (!await EnsureStreamlabsOpen(log)) return;
            var d1 = sw1.SelectedItem as string is { } a && a != Skip ? a : null;
            var d2 = sw2.SelectedItem as string is { } b && b != Skip ? b : null;
            go.Enabled = false;
            log.Text = "Working...";
            try
            {
                var report = await Task.Run(() => { using var sl = StreamlabsService.Connect(); return sl.SetUpConsoleScenes(d1, d2); });
                log.Text = string.Join(Environment.NewLine, report) + Environment.NewLine + "Done - the scenes are in Streamlabs now.";
                AppPaths.Log("Streamlabs scenes: " + string.Join(" | ", report));
            }
            catch (Exception ex) { log.Text = "Couldn't set up the scenes: " + ex.Message; AppPaths.Log("Streamlabs scenes failed: " + ex); }
            finally { go.Enabled = true; }
        };
        Controls.AddRange(new Control[] { title, help, note, log, go, close });
        if (!ConsoleFrames.All.Any(ConsoleFrames.Exists))
            log.Text = $"The console pictures weren't found in {ConsoleFrames.Folder} - set the folder in Settings first.";
    }

    /// <summary>Streamlabs must be running for its API; offers to start it and waits until it answers.</summary>
    private async Task<bool> EnsureStreamlabsOpen(TextBox log)
    {
        if (StreamlabsService.IsOpen) return true;
        var exe = DependencyService.PathOf("Streamlabs Desktop");
        if (exe == null) { log.Text = "Streamlabs Desktop isn't installed (Tools & Updates can install it)."; return false; }
        if (MessageBox.Show(this, "Streamlabs Desktop isn't open. Start it now?", "Streamlabs", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
        log.Text = "Starting Streamlabs Desktop...";
        for (int i = 0; i < 90 && !StreamlabsService.IsOpen; i++) await Task.Delay(1000);
        if (!StreamlabsService.IsOpen) { log.Text = "Streamlabs didn't start in time - open it and try again."; return false; }
        await Task.Delay(5000); // let it load the scene collection
        return true;
    }
}
