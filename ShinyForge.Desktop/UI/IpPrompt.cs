using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// Asks for a console's IP address: each console section asks once per run, and again whenever the console doesn't
/// answer at the saved address ("its IP may have changed").
/// </summary>
public sealed class IpPrompt : Window
{
    private readonly TextBox _ip;
    private string? _result;

    private IpPrompt(string device, string current, string? problem, string help)
    {
        Title = $"ShinyForge - {device} IP address";
        Icon = App.Icon;
        Width = 540;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Skin.B(Skin.Panel);
        _ip = Skin.Input(current, 260);
        _ip.FontSize = 17;
        var ok = new FancyButton("Continue", Skin.GlyphCheck, Skin.Accent);
        var cancel = new FancyButton("Cancel", "", Skin.PanelHi);
        var stack = new StackPanel { Margin = new Thickness(22, 18), Spacing = 10 };
        if (problem != null) stack.Children.Add(Skin.T("⚠  " + problem, 13, Skin.Warn));
        stack.Children.Add(Skin.H2(problem == null ? $"What is your {device}'s IP address?" : $"Enter the {device}'s new IP address:"));
        stack.Children.Add(Skin.Small(help));
        stack.Children.Add(_ip);
        stack.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } });
        Content = stack;
        async void Accept()
        {
            var ip = (_ip.Text ?? "").Trim();
            if (!IPAddress.TryParse(ip, out _))
            {
                await Dialogs.Info(this, "That doesn't look like an IP address (example: 192.168.0.57).", $"{device} IP");
                return;
            }
            _result = ip;
            Close();
        }
        ok.Click += (_, _) => Accept();
        cancel.Click += (_, _) => Close();
        _ip.KeyDown += (_, e) => { if (e.Key == Key.Enter) Accept(); };
        Opened += (_, _) => { _ip.Focus(); _ip.SelectAll(); };
    }

    private const string Help3DS = "Open Rosalina on the 3DS (L + D-Pad Down + Select) - the IP is shown there.\nThe 3DS must be on the same WiFi network as this computer.";
    private const string HelpSwitch = "On the Switch: System Settings → Internet → Connection Status shows the IP address.\nThe Switch must be on the same network as this computer, with sys-botbase running.";

    private static async Task<string?> Show(Control? owner, string device, string current, string? problem, string help)
    {
        var w = new IpPrompt(device, current, problem, help);
        if (Dialogs.Owner(owner) is { } o) await w.ShowDialog(o);
        return w._result;
    }

    /// <summary>The 3DS prompt; the address is remembered for the rest of this run (not saved). Null if cancelled.</summary>
    public static async Task<string?> Ask(Control? owner, string? problem = null)
    {
        var ip = await Show(owner, "3DS", AppSettings.Current.ThreeDSIP, problem, Help3DS);
        if (ip != null) AppSettings.Current.ThreeDSIP = ip;
        return ip;
    }

    /// <summary>The 3DS IP for this run: asks only if it hasn't been entered since ShinyForge started.</summary>
    public static Task<string?> GetOrAsk(Control? owner) =>
        string.IsNullOrWhiteSpace(AppSettings.Current.ThreeDSIP) ? Ask(owner) : Task.FromResult<string?>(AppSettings.Current.ThreeDSIP);

    /// <summary>The 3DS IP, checked: asks again while the 3DS doesn't answer there. Null if cancelled.</summary>
    public static async Task<string?> GetChecked3DS(Control? owner)
    {
        var ip = await GetOrAsk(owner);
        while (ip != null && !await Task.Run(() => HealthMonitor.CanReach(ip, 4000, 2500)))
            ip = await Ask(owner, $"The 3DS doesn't answer at {ip}. Its IP address may have changed - or Rosalina's debugger (Debugger options → Enable debugger) is off.");
        return ip;
    }

    private static bool _switchAskedThisRun;

    /// <summary>The Switch IP for this run: asked once when the Switch section opens (pre-filled with the last address) and saved.</summary>
    public static async Task<string?> GetOrAskSwitch(Control? owner)
    {
        if (_switchAskedThisRun && !string.IsNullOrWhiteSpace(AppSettings.Current.SwitchIP)) return AppSettings.Current.SwitchIP;
        var ip = await AskSwitch(owner);
        if (ip != null) _switchAskedThisRun = true;
        return ip;
    }

    /// <summary>Asks for the Switch's IP and saves it (settings + the Python bot's config). Null if cancelled.</summary>
    public static async Task<string?> AskSwitch(Control? owner, string? problem = null)
    {
        var s = AppSettings.Current;
        var ip = await Show(owner, "Switch", s.SwitchIP, problem, HelpSwitch);
        if (ip == null) return null;
        s.SwitchIP = ip;
        s.Save();
        var bot = AppPaths.FindPythonBotFolder(s.PythonBotFolder);
        if (bot != null) try { HuntService.SyncBotConfig(bot, s.SwitchIP, s.SwitchPort); } catch { }
        AppPaths.Log("Switch IP changed to " + ip);
        return ip;
    }

    /// <summary>Makes sure the Switch answers at the saved IP (or by USB when that's set); asks for the new IP until it does or the user cancels.</summary>
    public static async Task<bool> EnsureSwitch(Control? owner)
    {
        var s = AppSettings.Current;
        if (s.SwitchUsb && !HealthMonitor.SwitchReachable())
        {
            await Dialogs.Info(owner, "sys-botbase is set to USB, but no Switch is connected by USB. USB works on Windows only - " +
                                      "switch back to WiFi: Shiny Hunt card → sys-botbase → WiFi.", "ShinyForge - Switch");
            return false;
        }
        while (!await Task.Run(() => HealthMonitor.SwitchReachable(2500)))
        {
            if (await AskSwitch(owner, string.IsNullOrWhiteSpace(s.SwitchIP)
                    ? "Enter your Switch's IP address (on the Switch: System Settings → Internet)."
                    : $"The Switch doesn't answer at {s.SwitchIP}:{s.SwitchPort}. Its IP address may have changed (or it's asleep / sys-botbase isn't running).") == null)
                return false;
        }
        return true;
    }
}
