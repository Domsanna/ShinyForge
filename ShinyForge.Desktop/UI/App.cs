using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using ShinyForge.Core;
using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>The Avalonia application: the same background services as the Windows version, then the main window.</summary>
public sealed class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    public static WindowIcon? Icon { get; private set; }
    /// <summary>--screenshot DIR: draws every page and window to PNG files and exits (used by the self-test).</summary>
    public static string? ScreenshotDir { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        try { Icon = new WindowIcon(Path.Combine(AppPaths.AssetsDir, "ShinyForge.png")); } catch { }
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            AppPaths.Log("Background error: " + e.Exception);
            HealthMonitor.Report("Program", HealthState.Warning, "A background task failed: " + e.Exception.GetBaseException().Message);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => AppPaths.Log("Unhandled: " + e.ExceptionObject);
        AppPaths.Log($"ShinyForge {AppInfo.Display} started on {OsInfo.Name} (portable={AppPaths.IsPortable})");
        AppPaths.EnsureBackupToolsDir();
        HealthMonitor.RegisterBuiltIns();
        if (ScreenshotDir == null) HealthMonitor.Start();
        NotifyService.Attach();
        PowerGuard.BusyReason = () =>
            RecordingService.Instances.Any(r => r.IsRecording) ? "a recording"
            : GamePage.Any3DSHunt ? "the 3DS hunt"
            : HuntService.IsPythonHuntRunning() ? "the Python hunter" : null;
        PowerGuard.Start();
        if (ScreenshotDir == null) WebServer.Apply();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            MainWindow = new MainWindow();
            desktop.MainWindow = MainWindow;
            desktop.Exit += (_, _) =>
            {
                WebServer.Stop();
                HealthMonitor.Stop();
            };
            if (ScreenshotDir != null) _ = Screenshots.Run(ScreenshotDir, desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    public static Bitmap? LoadBitmap(string path)
    {
        try { return File.Exists(path) ? new Bitmap(path) : null; } catch { return null; }
    }
}
