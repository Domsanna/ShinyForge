using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ShinyForge.UI;

/// <summary>Message boxes and file/folder pickers (the Windows version's MessageBox / dialogs) for Avalonia.</summary>
public static class Dialogs
{
    public const string Yes = "Yes", No = "No", Cancel = "Cancel", Ok = "OK";

    /// <summary>The window to show a dialog over (the given one, else the main window).</summary>
    public static Window? Owner(Control? c) =>
        (c == null ? null : TopLevel.GetTopLevel(c) as Window) ?? App.MainWindow;

    /// <summary>Shows a message with buttons; returns the text of the button pressed (null if the window was closed).</summary>
    public static async Task<string?> Show(Control? owner, string message, string title, params string[] buttons)
    {
        if (buttons.Length == 0) buttons = new[] { Ok };
        string? result = null;
        var win = new Window
        {
            Title = title, Width = 560, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Skin.B(Skin.Panel), ShowInTaskbar = false, Icon = App.Icon,
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        for (int i = 0; i < buttons.Length; i++)
        {
            var text = buttons[i];
            var b = new FancyButton(text, "", i == 0 ? Skin.Accent : Skin.PanelHi) { MinWidth = 90 };
            b.Click += (_, _) => { result = text; win.Close(); };
            row.Children.Add(b);
        }
        win.Content = new StackPanel
        {
            Margin = new Thickness(22, 20, 22, 18),
            Children =
            {
                new SelectableTextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Skin.B(Skin.Text), FontSize = 13.5, MaxWidth = 516 },
                row,
            },
        };
        var o = Owner(owner);
        if (o is { IsVisible: true }) await win.ShowDialog(o);
        else
        {
            var done = new TaskCompletionSource();
            win.Closed += (_, _) => done.TrySetResult();
            win.Show();
            await done.Task;
        }
        return result;
    }

    public static Task Info(Control? owner, string message, string title = "ShinyForge") => Show(owner, message, title, Ok);

    public static async Task<bool> YesNo(Control? owner, string message, string title = "ShinyForge") =>
        await Show(owner, message, title, Yes, No) == Yes;

    /// <summary>Yes / No / Cancel: returns "Yes", "No" or "Cancel".</summary>
    public static async Task<string> YesNoCancel(Control? owner, string message, string title = "ShinyForge") =>
        await Show(owner, message, title, Yes, No, Cancel) ?? Cancel;

    public static async Task<string?> PickFolder(Control? owner, string title, string? start = null)
    {
        var top = Owner(owner);
        if (top == null) return null;
        var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
            options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(start);
        var picked = await top.StorageProvider.OpenFolderPickerAsync(options);
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Opens a file. <paramref name="patterns"/>: e.g. ("Excel workbooks", new[] {"*.xlsm","*.xlsx"}).</summary>
    public static async Task<string?> PickFile(Control? owner, string title, string? start = null, params (string Name, string[] Patterns)[] patterns)
    {
        var top = Owner(owner);
        if (top == null) return null;
        var options = new FilePickerOpenOptions
        {
            Title = title, AllowMultiple = false,
            FileTypeFilter = patterns.Select(p => new FilePickerFileType(p.Name) { Patterns = p.Patterns }).Append(FilePickerFileTypes.All).ToList(),
        };
        if (!string.IsNullOrWhiteSpace(start) && Directory.Exists(start))
            options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(start);
        var picked = await top.StorageProvider.OpenFilePickerAsync(options);
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>Runs on the UI thread (from any thread).</summary>
    public static void Ui(Action a)
    {
        if (Dispatcher.UIThread.CheckAccess()) a();
        else Dispatcher.UIThread.Post(a);
    }

}
