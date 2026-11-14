using System.Diagnostics;

namespace ShinyForge.Core;

/// <summary>Alert sounds on every OS: Windows system sounds, macOS system sounds (afplay), Linux freedesktop sounds (paplay).</summary>
public static class Sounds
{
    public enum Kind { Alert, Error }

    public static void Play(Kind kind = Kind.Alert)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // System.Media lives in the Windows-only System.Windows.Extensions assembly: loaded by name so this file builds everywhere.
                var t = Type.GetType("System.Media.SystemSounds, System.Windows.Extensions");
                var sound = t?.GetProperty(kind == Kind.Error ? "Hand" : "Exclamation")?.GetValue(null);
                sound?.GetType().GetMethod("Play")?.Invoke(sound, null);
                return;
            }
            var (exe, file) = OperatingSystem.IsMacOS()
                ? ("afplay", kind == Kind.Error ? "/System/Library/Sounds/Basso.aiff" : "/System/Library/Sounds/Glass.aiff")
                : ("paplay", kind == Kind.Error ? "/usr/share/sounds/freedesktop/stereo/dialog-error.oga" : "/usr/share/sounds/freedesktop/stereo/complete.oga");
            if (File.Exists(file))
                Process.Start(new ProcessStartInfo(exe) { ArgumentList = { file }, UseShellExecute = false, CreateNoWindow = true });
            else
                Console.Write('\a');
        }
        catch { /* no sound is never a problem */ }
    }
}
