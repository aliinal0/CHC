using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;

namespace CHC.App.Services;

/// <summary>
/// The scheduled task that starts CHC in the notification area at sign-in ("CHC\Background"). A task, not the Run
/// key, because it can start CHC with the highest rights the user has without a UAC prompt at every sign-in, and
/// CPU temperatures need those rights. It is set up for every user of the PC; whether CHC then keeps recording is
/// each user's own setting. Creating or removing it needs administrator rights once.
/// </summary>
public static class StartupService
{
    public const string TaskName = @"CHC\Background";

    /// <summary>True if the task exists (asked with the user's normal rights).</summary>
    public static bool IsRegistered()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p is null) return false;
            p.WaitForExit(5000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>Creates or removes the task. Runs schtasks elevated (one UAC prompt) unless CHC already is.</summary>
    public static bool Set(bool enabled, bool elevated)
    {
        string? xml = null;
        try
        {
            string arguments;
            if (enabled)
            {
                xml = Path.Combine(Path.GetTempPath(), $"chc-task-{Guid.NewGuid():N}.xml");
                File.WriteAllText(xml, TaskXml(Environment.ProcessPath!), System.Text.Encoding.Unicode);
                arguments = $"/Create /TN \"{TaskName}\" /XML \"{xml}\" /F";
            }
            else
            {
                arguments = $"/Delete /TN \"{TaskName}\" /F";
            }
            var start = new ProcessStartInfo("schtasks.exe", arguments) { CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
            if (elevated) start.UseShellExecute = false;
            else
            {
                start.UseShellExecute = true;
                start.Verb = "runas";
            }
            using var p = Process.Start(start);
            if (p is null) return false;
            p.WaitForExit(15000);
            return p.HasExited && p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Win32Exception: the UAC prompt was declined.
            return false;
        }
        finally
        {
            if (xml is not null)
                try { File.Delete(xml); } catch (IOException) { }
        }
    }

    /// <summary>
    /// Any user who signs in; with the highest rights they have (an administrator gets every sensor, a standard
    /// user what Windows allows); 30 seconds after sign-in so the desktop comes first; never stopped on battery.
    /// </summary>
    private static string TaskXml(string exe) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Author>CHC</Author>
            <Description>Starts CHC - Computer Health Check in the notification area at sign-in, so it can keep the temperature and usage history of this PC. Turn it off in CHC's settings.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <Delay>PT30S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Users">
              <GroupId>S-1-5-32-545</GroupId>
              <RunLevel>HighestAvailable</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>true</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>7</Priority>
          </Settings>
          <Actions Context="Users">
            <Exec>
              <Command>"{SecurityElement.Escape(exe)}"</Command>
              <Arguments>--background</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;
}
