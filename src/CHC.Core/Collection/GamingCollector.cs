using System.Text.RegularExpressions;
using CHC.Core.Diagnostics;
using CHC.Core.Models;
using Microsoft.Win32;

namespace CHC.Core.Collection;

/// <summary>Display mode and Windows gaming settings.</summary>
internal static class GamingCollector
{
    public static GamingInfo Collect()
    {
        var gaming = new GamingInfo();
        if (Native.DisplayModes() is { } display)
        {
            gaming.Width = display.Width;
            gaming.Height = display.Height;
            gaming.RefreshHz = display.Current;
            gaming.MaxRefreshHz = display.Max;
            gaming.NativeWidth = display.NativeWidth;
            gaming.NativeHeight = display.NativeHeight;
        }

        // Game Mode is on unless the user turned it off (the value is missing on a fresh install).
        using (var gameBar = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\GameBar"))
            gaming.GameMode = gameBar?.GetValue("AutoGameModeEnabled") is int gameMode ? gameMode != 0 : true;

        using (var graphics = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers"))
            gaming.HardwareGpuScheduling = graphics?.GetValue("HwSchMode") is int scheduling ? scheduling == 2 : null;

        // SecurityServicesRunning lists 2 when Memory Integrity (hypervisor-enforced code integrity) is active.
        if (Wmi.First("SELECT SecurityServicesRunning FROM Win32_DeviceGuard", Wmi.DeviceGuard) is { } guard)
            gaming.MemoryIntegrity = guard.IntArray("SecurityServicesRunning").Contains(2);

        // Background recording is off unless the user turned on "Record what happened".
        using (var dvr = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\GameDVR"))
        using (var store = Registry.CurrentUser.OpenSubKey(@"System\GameConfigStore"))
            gaming.BackgroundRecording = dvr?.GetValue("HistoricalCaptureEnabled") is int history && history != 0 && store?.GetValue("GameDVR_Enabled") is not 0;

        // "Enhance pointer precision": MouseSpeed 1 (with the two thresholds) is acceleration, 0 is a straight 1:1.
        using (var mouse = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse"))
            gaming.MouseAcceleration = mouse?.GetValue("MouseSpeed") is string speed ? speed.Trim() != "0" : null;

        GraphicsPreferences(gaming);

        // The driver arrives with AMD's chipset software; its service name has changed between versions.
        using (var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services"))
            gaming.VCacheDriver = services?.GetSubKeyNames().Any(n => n.StartsWith("amd3dvcache", StringComparison.OrdinalIgnoreCase));

        return gaming;
    }

    /// <summary>
    /// Settings → Display → Graphics: Windows 11's windowed-game optimizations, and games someone set to "Power saving"
    /// (stored per program as "GpuPreference=1;"), which then run on the integrated graphics.
    /// </summary>
    private static void GraphicsPreferences(GamingInfo gaming)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\DirectX\UserGpuPreferences");
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string value) continue;
            if (name == "DirectXUserGlobalSettings")
            {
                var setting = Regex.Match(value, @"SwapEffectUpgradeEnable=(\d)");
                if (setting.Success) gaming.WindowedOptimizations = setting.Groups[1].Value == "1";
            }
            else if (value.Contains("GpuPreference=1;", StringComparison.Ordinal) && Catalog.IsGamePath(name))
            {
                gaming.PowerSavingGames.Add(Path.GetFileNameWithoutExtension(name));
            }
        }
    }
}
