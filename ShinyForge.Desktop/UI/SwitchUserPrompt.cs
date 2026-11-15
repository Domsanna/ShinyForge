using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>
/// "Who is using the software?" came up on the Switch: shows that screen and lets the user pick who plays
/// (one button per Switch user, left to right). Used by <see cref="SwitchGameLauncher.ChooseUser"/>; called from a
/// background thread, shown on the UI thread.
/// </summary>
public static class SwitchUserPrompt
{
    public static int? Ask(byte[] jpeg, int users) =>
        Dispatcher.UIThread.CheckAccess() ? 0 : Dispatcher.UIThread.InvokeAsync(() => Show(jpeg, users)).GetAwaiter().GetResult();

    private static async Task<int?> Show(byte[] jpeg, int users)
    {
        var owner = App.MainWindow;
        if (owner == null) return 0;
        int? chosen = null;
        using var ms = new MemoryStream(jpeg);
        using var picture = new Bitmap(ms);
        var win = new Window
        {
            Title = "ShinyForge - who is playing?", Width = 700, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Skin.B(Skin.Panel), ShowInTaskbar = false, Icon = App.Icon,
        };
        var buttons = new WrapPanel();
        for (var i = 0; i < users; i++)
        {
            var n = i;
            var b = new FancyButton(n switch { 0 => "1st user (left)", 1 => "2nd user", 2 => "3rd user", _ => $"{n + 1}th user" },
                Skin.GlyphPlay, n == 0 ? Skin.Accent : Skin.PanelHi) { Margin = new Thickness(4) };
            b.Click += (_, _) => { chosen = n; win.Close(); };
            buttons.Children.Add(b);
        }
        var cancel = new FancyButton("Cancel", Skin.GlyphStop, Skin.PanelHi) { Margin = new Thickness(4) };
        cancel.Click += (_, _) => win.Close();
        buttons.Children.Add(cancel);
        win.Content = new StackPanel
        {
            Margin = new Thickness(16), Spacing = 10,
            Children =
            {
                Skin.T("The Switch asks \"Who is using the software?\". Which user should play the game?", 13, Skin.Text),
                new Image { Source = picture, Height = 380 },
                buttons,
            },
        };
        await win.ShowDialog(owner);
        return chosen;
    }
}
