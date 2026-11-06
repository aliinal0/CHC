using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace CHC.App.Services;

public static class ShellService
{
    /// <summary>
    /// Starts a second, elevated copy of CHC with the same command-line options (--lang, --demo...)
    /// and closes this one. False if the user declined UAC.
    /// </summary>
    public static bool RestartAsAdministrator()
    {
        try
        {
            // The elevated copy shows its window, even when this one was started in the background.
            var options = Environment.GetCommandLineArgs().Skip(1)
                .Where(a => !a.Equals("--background", StringComparison.OrdinalIgnoreCase))
                .Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            // Let the new copy be "the" CHC; take the place back if UAC is declined.
            SingleInstance.Release();
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', options)) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception)
        {
            SingleInstance.TryAcquire(signalOther: false);
            return false;
        }
    }

    public static void Open(string target)
    {
        try
        {
            // From an elevated CHC a browser would run as administrator too, and so would what it downloads. The shell starts it as the normal user.
            if (Core.Collection.SnapshotCollector.IsElevated && (target.StartsWith("https://", StringComparison.Ordinal) || target.StartsWith("ms-settings:", StringComparison.Ordinal)))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = false });
            else
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    public static void ShowInExplorer(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); }
        catch (Win32Exception) { }
    }
}
