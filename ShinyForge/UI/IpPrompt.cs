using System.Net;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Asks for a console's IP address: the 3DS (asked once per run, before 3DS features) and - whenever it doesn't answer at
/// the saved address - the Switch or the 3DS again ("its IP may have changed").
/// </summary>
public sealed class IpPrompt : Form
{
    private readonly TextBox _ip;
    public string Ip => _ip.Text.Trim();

    private IpPrompt(string device, string current, string? problem, string help)
    {
        Text = $"ShinyForge - {device} IP address";
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        TopMost = true;
        var y = 18;
        var controls = new List<Control>();
        if (problem != null)
        {
            var warn = Theme.Label("⚠  " + problem, Theme.Body, Theme.Warn, autoSize: false);
            warn.Location = new Point(20, y);
            warn.Size = new Size(470, 46);
            controls.Add(warn);
            y += 52;
        }
        var title = Theme.Label(problem == null ? $"What is your {device}'s IP address?" : $"Enter the {device}'s new IP address:", Theme.H2);
        title.Location = new Point(20, y);
        var helpLabel = Theme.Label(help, Theme.Small, Theme.Muted);
        helpLabel.Location = new Point(22, y + 34);
        _ip = new TextBox { Location = new Point(24, y + 82), Width = 250, Text = current };
        Theme.StyleInput(_ip);
        _ip.Font = new Font("Segoe UI", 13f);
        var ok = new FancyButton("Continue", Theme.GlyphCheck, Theme.Accent) { Location = new Point(270, y + 140) };
        var cancel = new FancyButton("Cancel", "", Theme.PanelHi) { Location = new Point(390, y + 140) };
        ClientSize = new Size(510, y + 196);
        ok.Click += (_, _) =>
        {
            if (!IPAddress.TryParse(Ip, out _))
            {
                MessageBox.Show(this, "That doesn't look like an IP address (example: 192.168.0.57).", $"{device} IP");
                return;
            }
            DialogResult = DialogResult.OK;
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        _ip.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ok.PerformClickSafe(); };
        controls.AddRange(new Control[] { title, helpLabel, _ip, ok, cancel });
        Controls.AddRange(controls.ToArray());
        Shown += (_, _) => { _ip.Focus(); _ip.SelectAll(); };
    }

    private const string Help3DS = "Open Rosalina on the 3DS (L + D-Pad Down + Select) - the IP is shown there.\n" +
                                   "The 3DS must be on the same WiFi network as this PC.";
    private const string HelpSwitch = "On the Switch: System Settings → Internet → Connection Status shows the IP address.\n" +
                                      "The Switch must be on the same network as this PC, with sys-botbase running.";

    /// <summary>Shows the 3DS prompt; remembers the address for the rest of this run (not saved to disk). Null if cancelled.</summary>
    public static string? Ask(IWin32Window? owner, string? problem = null)
    {
        using var f = new IpPrompt("3DS", AppSettings.Current.ThreeDSIP, problem, Help3DS);
        if (f.ShowDialog(owner) != DialogResult.OK)
            return null;
        AppSettings.Current.ThreeDSIP = f.Ip;
        return f.Ip;
    }

    /// <summary>The 3DS IP for this run: asks only if it hasn't been entered since ShinyForge started.</summary>
    public static string? GetOrAsk(IWin32Window? owner) =>
        string.IsNullOrWhiteSpace(AppSettings.Current.ThreeDSIP) ? Ask(owner) : AppSettings.Current.ThreeDSIP;

    /// <summary>
    /// The 3DS IP, checked: if the 3DS doesn't answer there (its IP changed, or the debugger is off) the user is asked for
    /// the new address. Null if cancelled.
    /// </summary>
    public static string? GetChecked3DS(IWin32Window? owner)
    {
        var ip = GetOrAsk(owner);
        while (ip != null && !HealthMonitor.CanReach(ip, 4000, 2500))
        {
            ip = Ask(owner, $"The 3DS doesn't answer at {ip}. Its IP address may have changed - or Rosalina's debugger " +
                            "(Debugger options → Enable debugger) is off.");
        }
        return ip;
    }

    private static bool _switchAskedThisRun;

    /// <summary>
    /// The Switch IP for this run: asked once when the Switch section is opened (like the 3DS), pre-filled with the last
    /// address, and saved. Null if cancelled.
    /// </summary>
    public static string? GetOrAskSwitch(IWin32Window? owner)
    {
        if (_switchAskedThisRun && !string.IsNullOrWhiteSpace(AppSettings.Current.SwitchIP)) return AppSettings.Current.SwitchIP;
        var ip = AskSwitch(owner);
        if (ip != null) _switchAskedThisRun = true;
        return ip;
    }

    /// <summary>Asks for the Switch's new IP and saves it (settings + the Python bot's config). Null if cancelled.</summary>
    public static string? AskSwitch(IWin32Window? owner, string? problem = null)
    {
        var s = AppSettings.Current;
        using var f = new IpPrompt("Switch", s.SwitchIP, problem, HelpSwitch);
        if (f.ShowDialog(owner) != DialogResult.OK)
            return null;
        s.SwitchIP = f.Ip;
        s.Save();
        var bot = AppPaths.FindPythonBotFolder(s.PythonBotFolder);
        if (bot != null) try { HuntService.SyncBotConfig(bot, s.SwitchIP, s.SwitchPort); } catch { }
        AppPaths.Log("Switch IP changed to " + f.Ip);
        return f.Ip;
    }

    /// <summary>What to do when sys-botbase is set to USB but no Switch is plugged in.</summary>
    internal const string UsbMissing =
        "sys-botbase is set to USB, but no Switch is connected by USB.\n\nPlug the Switch (out of the dock) into this PC with a USB-C " +
        "cable. The first time, install the WinUSB driver for \"Nintendo Switch\" with Zadig (zadig.akeo.ie). To use WiFi again: Shiny " +
        "Hunt card → sys-botbase → WiFi.";

    /// <summary>
    /// Makes sure the Switch answers (sys-botbase) at the saved IP - or by USB when that's set; if not, asks for the new IP
    /// until it does or the user cancels. True when the Switch can be reached.
    /// </summary>
    public static bool EnsureSwitch(IWin32Window? owner)
    {
        var s = AppSettings.Current;
        if (s.SwitchUsb && !HealthMonitor.SwitchReachable())
        {
            MessageBox.Show(owner, UsbMissing, "ShinyForge - Switch", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        while (!HealthMonitor.SwitchReachable(2500))
        {
            if (AskSwitch(owner, string.IsNullOrWhiteSpace(s.SwitchIP)
                    ? "Enter your Switch's IP address (on the Switch: System Settings → Internet)."
                    : $"The Switch doesn't answer at {s.SwitchIP}:{s.SwitchPort}. Its IP address may have changed " +
                      "(or it's asleep / sys-botbase isn't running).") == null)
                return false;
        }
        return true;
    }
}

internal static class ControlExtensions
{
    public static void PerformClickSafe(this Control c) => c.GetType().GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.Invoke(c, new object[] { EventArgs.Empty });
}
