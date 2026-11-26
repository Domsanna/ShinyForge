using ShinyForge.Core;
using ShinyForge.UI;

namespace ShinyForge;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        // One ShinyForge per data folder: starting it again brings the open window forward instead of a second copy (two copies
        // ran two web servers, health monitors and update checks and fought over the Switch). The installed and a portable copy
        // have different data folders, so they can still run side by side.
        var key = "ShinyForge-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(AppPaths.DataDir.ToLowerInvariant())))[..16];
        using var single = new Mutex(true, @"Local\" + key, out var first);
        if (!first)
        {
            try { using var wake = EventWaitHandle.OpenExisting(@"Local\" + key + "-show"); wake.Set(); } catch { }
            return;
        }
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "-show");

        ApplicationConfiguration.Initialize();
        Application.SetColorMode(SystemColorMode.Dark);
        Application.ThreadException += (_, e) => Report(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Report(e.ExceptionObject as Exception);
        // Errors in background work that nobody awaited: logged and shown in the health panel instead of vanishing.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            e.SetObserved();
            AppPaths.Log("Background error: " + e.Exception);
            Services.HealthMonitor.Report("Program", Services.HealthState.Warning, "A background task failed: " + e.Exception.GetBaseException().Message, e.Exception.ToString());
        };
        AppPaths.Log($"ShinyForge started (portable={AppPaths.IsPortable})");
        AppPaths.EnsureBackupToolsDir();
        // Safety net: every minute, check that everything that's running works (see Services\HealthMonitor.cs).
        Services.HealthMonitor.RegisterBuiltIns();
        Services.HealthMonitor.Start();
        Services.NotifyService.Attach();          // problems -> phone / Discord (if set up)
        // Keep the PC awake while something important runs
        Services.PowerGuard.BusyReason = () =>
            Services.RecordingService.Instances.Any(r => r.IsRecording) ? "a recording"
            : UI.GamePage.Any3DSHunt ? "the 3DS hunt"
            : Services.HuntService.IsPythonHuntRunning() ? "the Python hunter" : null;
        Services.PowerGuard.Start();
        Services.ConsoleFtpLauncher.Decode = GdiImage.Decode;        // reading Switch screenshots (HOME menu, Homebrew Menu)
        Services.HomeSorter.HomeVision.Ocr = WinOcr.Read;           // HOME sorter: reading the summary screen
        Services.HomeSorter.HomeVision.OcrAll = WinOcr.ReadAll;
        Services.HomeSorter.HomeVision.DecodeRgba = WinOcr.DecodeRgba;
        Services.HomeSorter.ConsoleKeepAwake.Apply();                 // keep the Switch / Switch 2 awake (if turned on)
        Services.SwitchGameLauncher.ChooseUser = SwitchUserPrompt.Ask; // "Who is using the software?" -> ask which user
        Services.WebServer.Apply();              // stream overlay / phone remote (if turned on)
        var main = new MainForm();
        ThreadPool.RegisterWaitForSingleObject(show, (_, _) =>
        {
            try { main.BeginInvoke(() => main.BringToFront(true)); } catch { } // window not ready / closing
        }, null, Timeout.Infinite, false);
        Application.Run(main);
        Services.WebServer.Stop();
        Services.HealthMonitor.Stop();
    }

    private static void Report(Exception? e)
    {
        AppPaths.Log("Unhandled: " + e);
        Services.HealthMonitor.Report("Program", Services.HealthState.Error, "Unexpected error: " + (e?.Message ?? "unknown"), e?.ToString());
        MessageBox.Show(e?.Message ?? "Unknown error", "ShinyForge - unexpected error", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
