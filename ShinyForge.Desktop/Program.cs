using Avalonia;

namespace ShinyForge;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var i = Array.IndexOf(args, "--screenshot");
        if (i >= 0 && i + 1 < args.Length) UI.App.ScreenshotDir = args[i + 1];
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<UI.App>().UsePlatformDetect().LogToTrace();
}
