using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace CHC.App.Services;

public static class ShellService
{
    /// <summary>Starts a second, elevated copy of CHC and closes this one. False if the user declined UAC.</summary>
    public static bool RestartAsAdministrator()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
            return true;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { }
    }

    public static void ShowInExplorer(string file)
    {
        try { Process.Start("explorer.exe", $"/select,\"{file}\""); }
        catch (Win32Exception) { }
    }
}
