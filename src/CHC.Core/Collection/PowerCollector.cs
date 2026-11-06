using CHC.Core.Models;
using Microsoft.Win32;

namespace CHC.Core.Collection;

/// <summary>Reads the power plan settings that matter for "freezes when left idle" problems.</summary>
internal static class PowerCollector
{
    private static readonly Guid SleepGroup = new("238c9fa8-0aad-41ed-83f4-97be242c8f20");
    private static readonly Guid SleepAfter = new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da");
    private static readonly Guid HibernateAfter = new("9d7815a6-7ee4-497e-8888-515a05f02364");
    private static readonly Guid HybridSleep = new("94ac6d29-73ce-41a6-809f-6363ba21b47e");
    private static readonly Guid PcieGroup = new("501a4d13-42af-4429-9fd1-a8218c268e20");
    private static readonly Guid LinkPowerManagement = new("ee12f906-d277-404b-b6da-e5fa1a576df5");
    private static readonly Guid UsbGroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid UsbSelectiveSuspend = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly Guid DiskGroup = new("0012ee47-9041-4b5d-9b77-535fba8b1442");
    private static readonly Guid DiskOffAfter = new("6738e2c4-e8a5-4a42-b16a-e040e769756e");
    private static readonly Guid ProcessorGroup = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid ProcessorMaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    private static readonly Guid ProcessorBoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    public static PowerInfo Collect()
    {
        var power = new PowerInfo();
        if (Native.ActivePowerScheme() is { } scheme)
        {
            power.PlanGuid = scheme;
            power.PlanName = Native.PowerSchemeName(scheme) ?? "";
            power.SleepAfterSeconds = Native.PowerAcValue(scheme, SleepGroup, SleepAfter);
            power.HibernateAfterSeconds = Native.PowerAcValue(scheme, SleepGroup, HibernateAfter);
            power.HybridSleep = Native.PowerAcValue(scheme, SleepGroup, HybridSleep) is { } hybrid ? hybrid != 0 : null;
            power.PcieLinkPowerManagement = Native.PowerAcValue(scheme, PcieGroup, LinkPowerManagement);
            power.UsbSelectiveSuspend = Native.PowerAcValue(scheme, UsbGroup, UsbSelectiveSuspend) is { } usb ? usb != 0 : null;
            power.DiskOffAfterSeconds = Native.PowerAcValue(scheme, DiskGroup, DiskOffAfter);
            power.ProcessorMaxState = Native.PowerAcValue(scheme, ProcessorGroup, ProcessorMaxState);
            power.CpuBoostMode = Native.PowerAcValue(scheme, ProcessorGroup, ProcessorBoostMode);
        }

        if (Native.PowerCapabilities() is { } caps)
        {
            power.HibernationAvailable = caps.HiberFilePresent;
            power.ModernStandby = caps.ModernStandby;
        }

        // Fast Startup is a kind of hibernation, so it only works when the hibernation file exists.
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power");
        if (key?.GetValue("HiberbootEnabled") is int fastStartup)
            power.FastStartup = fastStartup == 1 && power.HibernationAvailable != false;

        return power;
    }
}
