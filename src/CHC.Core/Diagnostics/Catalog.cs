namespace CHC.Core.Diagnostics;

/// <summary>
/// What Windows error codes usually mean. The cause categories match the "cause.*"
/// language keys: heat, gpu, memory, cpu, hardware, storage, driver, sleep, lowmemory, system, silent, unknown.
/// </summary>
public static class Catalog
{
    public sealed record Code(string Name, string Cause);

    /// <summary>Blue screen (bug check) codes seen most often on home PCs.</summary>
    private static readonly Dictionary<uint, Code> Bugchecks = new()
    {
        [0xA] = new("IRQL_NOT_LESS_OR_EQUAL", "driver"),
        [0x18] = new("REFERENCE_BY_POINTER", "driver"),
        [0x19] = new("BAD_POOL_HEADER", "driver"),
        [0x1A] = new("MEMORY_MANAGEMENT", "memory"),
        [0x1E] = new("KMODE_EXCEPTION_NOT_HANDLED", "driver"),
        [0x24] = new("NTFS_FILE_SYSTEM", "storage"),
        [0x3B] = new("SYSTEM_SERVICE_EXCEPTION", "driver"),
        [0x3D] = new("INTERRUPT_EXCEPTION_NOT_HANDLED", "driver"),
        [0x4E] = new("PFN_LIST_CORRUPT", "memory"),
        [0x50] = new("PAGE_FAULT_IN_NONPAGED_AREA", "memory"),
        [0x77] = new("KERNEL_STACK_INPAGE_ERROR", "storage"),
        [0x7A] = new("KERNEL_DATA_INPAGE_ERROR", "storage"),
        [0x7B] = new("INACCESSIBLE_BOOT_DEVICE", "storage"),
        [0x7E] = new("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "driver"),
        [0x7F] = new("UNEXPECTED_KERNEL_MODE_TRAP", "hardware"),
        [0x8E] = new("KERNEL_MODE_EXCEPTION_NOT_HANDLED", "memory"),
        [0x9C] = new("MACHINE_CHECK_EXCEPTION", "hardware"),
        [0x9F] = new("DRIVER_POWER_STATE_FAILURE", "sleep"),
        [0xA0] = new("INTERNAL_POWER_ERROR", "sleep"),
        [0xBE] = new("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "driver"),
        [0xC2] = new("BAD_POOL_CALLER", "driver"),
        [0xC4] = new("DRIVER_VERIFIER_DETECTED_VIOLATION", "driver"),
        [0xC5] = new("DRIVER_CORRUPTED_EXPOOL", "driver"),
        [0xCE] = new("DRIVER_UNLOADED_WITHOUT_CANCELLING_PENDING_OPERATIONS", "driver"),
        [0xD1] = new("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "driver"),
        [0xEA] = new("THREAD_STUCK_IN_DEVICE_DRIVER", "gpu"),
        [0xED] = new("UNMOUNTABLE_BOOT_VOLUME", "storage"),
        [0xEF] = new("CRITICAL_PROCESS_DIED", "system"),
        [0xF4] = new("CRITICAL_OBJECT_TERMINATION", "storage"),
        [0xFC] = new("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "driver"),
        [0x101] = new("CLOCK_WATCHDOG_TIMEOUT", "cpu"),
        [0x109] = new("CRITICAL_STRUCTURE_CORRUPTION", "memory"),
        [0x10D] = new("WDF_VIOLATION", "driver"),
        [0x10E] = new("VIDEO_MEMORY_MANAGEMENT_INTERNAL", "gpu"),
        [0x113] = new("VIDEO_DXGKRNL_FATAL_ERROR", "gpu"),
        [0x116] = new("VIDEO_TDR_FAILURE", "gpu"),
        [0x117] = new("VIDEO_TDR_TIMEOUT_DETECTED", "gpu"),
        [0x119] = new("VIDEO_SCHEDULER_INTERNAL_ERROR", "gpu"),
        [0x124] = new("WHEA_UNCORRECTABLE_ERROR", "hardware"),
        [0x12B] = new("FAULTY_HARDWARE_CORRUPTED_PAGE", "memory"),
        [0x133] = new("DPC_WATCHDOG_VIOLATION", "driver"),
        [0x139] = new("KERNEL_SECURITY_CHECK_FAILURE", "driver"),
        [0x13A] = new("KERNEL_MODE_HEAP_CORRUPTION", "driver"),
        [0x13B] = new("PASSIVE_INTERRUPT_ERROR", "driver"),
        [0x144] = new("BUGCODE_USB3_DRIVER", "driver"),
        [0x14F] = new("PDC_WATCHDOG_TIMEOUT", "sleep"),
        [0x154] = new("UNEXPECTED_STORE_EXCEPTION", "storage"),
        [0x157] = new("KERNEL_THREAD_PRIORITY_FLOOR_VIOLATION", "driver"),
        [0x15B] = new("WORKER_THREAD_RETURNED_WITH_SYSTEM_PAGE_PRIORITY_ACTIVE", "driver"),
        [0x15F] = new("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", "sleep"),
        [0x1A1] = new("WIN32K_CALLOUT_WATCHDOG_LIVEDUMP", "driver"),
        [0x1A2] = new("WIN32K_CALLOUT_WATCHDOG_BUGCHECK", "driver"),
        [0x1C7] = new("STORE_DATA_STRUCTURE_CORRUPTION", "storage"),
        [0x1000007E] = new("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED_M", "driver"),
        [0x1000008E] = new("KERNEL_MODE_EXCEPTION_NOT_HANDLED_M", "memory"),
        [0x100000EA] = new("THREAD_STUCK_IN_DEVICE_DRIVER_M", "gpu"),
    };

    /// <summary>LiveKernelEvent codes: Windows caught a hang and recovered without a blue screen.</summary>
    private static readonly Dictionary<uint, Code> LiveKernel = new()
    {
        [0x117] = new("VIDEO_TDR_TIMEOUT_DETECTED", "gpu"),
        [0x141] = new("VIDEO_ENGINE_TIMEOUT_DETECTED", "gpu"),
        [0x193] = new("VIDEO_DXGKRNL_LIVEDUMP", "gpu"),
        [0x144] = new("BUGCODE_USB3_DRIVER", "driver"),
        [0x14F] = new("PDC_WATCHDOG_TIMEOUT", "sleep"),
        [0x15F] = new("CONNECTED_STANDBY_WATCHDOG_TIMEOUT_LIVEDUMP", "sleep"),
        [0x1A1] = new("WIN32K_CALLOUT_WATCHDOG_LIVEDUMP", "driver"),
        [0x1A8] = new("BUGCODE_WIFIADAPTER_DRIVER", "driver"),
        [0x1B8] = new("SECURE_KERNEL_ERROR", "system"),
    };

    public static Code Bugcheck(uint code) =>
        Bugchecks.TryGetValue(code, out var c) ? c : new Code($"0x{code:X}", "unknown");

    public static Code LiveKernelEvent(uint code) =>
        LiveKernel.TryGetValue(code, out var c) ? c : new Code($"LiveKernelEvent 0x{code:X}", "hardware");

    /// <summary>
    /// When a Windows version stops getting security updates (Home and Pro: 24 months for an autumn release,
    /// Enterprise and Education: 36). Null for LTSC, Server, Insider and versions not listed here.
    /// </summary>
    public static DateTime? WindowsSupportEnds(int build, string edition)
    {
        if (edition.Contains("Server", StringComparison.OrdinalIgnoreCase) || edition.EndsWith("EnterpriseS", StringComparison.OrdinalIgnoreCase)) return null;
        var longTrack = edition.StartsWith("Enterprise", StringComparison.OrdinalIgnoreCase) || edition.Equals("Education", StringComparison.OrdinalIgnoreCase);
        (DateTime HomePro, DateTime Enterprise)? end = build switch
        {
            < 19045 => (new(2023, 6, 13), new(2024, 6, 11)),
            19045 => (new(2025, 10, 14), new(2025, 10, 14)),
            22000 => (new(2023, 10, 10), new(2024, 10, 8)),
            22621 => (new(2024, 10, 8), new(2025, 10, 14)),
            22631 => (new(2025, 11, 11), new(2026, 11, 10)),
            26100 => (new(2026, 10, 13), new(2027, 10, 12)),
            26200 => (new(2027, 10, 12), new(2028, 10, 10)),
            _ => null,
        };
        return end is { } e ? longTrack ? e.Enterprise : e.HomePro : null;
    }

    private static readonly int[] MemorySpeeds =
        [2133, 2400, 2666, 2933, 3000, 3200, 3466, 3600, 3733, 3866, 4000, 4133, 4266, 4400, 4600, 4800, 5000, 5200, 5600, 6000, 6200, 6400, 6600, 6800, 7000, 7200, 7600, 8000, 8200, 8400];

    /// <summary>
    /// The speed a memory module is sold for, read from its part number: Kingston "KF560C36" / "HX432C16",
    /// Crucial "BL8G32C16", or a speed written out ("F5-6000J3038F16G", "CMK32GX5M2B6000C36", "6000 Series").
    /// Null for OEM modules whose part numbers don't contain a speed.
    /// </summary>
    public static int? RatedMemorySpeed(string partNumber)
    {
        var p = partNumber.Trim().ToUpperInvariant();
        var shortForm = System.Text.RegularExpressions.Regex.Match(p, @"^(?:KF[345]|HX[345]|BL\d{1,2}G)(\d{2})C\d");
        if (shortForm.Success)
        {
            var approx = int.Parse(shortForm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) * 100;
            return MemorySpeeds.MinBy(s => Math.Abs(s - approx));
        }
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(p, @"(?<!\d)(\d{4})(?!\d)"))
            if (int.TryParse(m.Value, System.Globalization.CultureInfo.InvariantCulture, out var speed) && MemorySpeeds.Contains(speed)) return speed;
        return null;
    }

    /// <summary>Graphics cards that support Resizable BAR (GeForce RTX 30 and newer, Radeon RX 6000 and newer, Intel Arc).</summary>
    public static bool SupportsResizableBar(string gpuName) =>
        System.Text.RegularExpressions.Regex.IsMatch(gpuName, @"RTX\s?(30|40|50)\d\d|RX\s?(6|7|9)\d{3}|\bArc\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Ryzen X3D processors with two core dies, only one of them under the extra cache (7900X3D, 7950X3D, 9900X3D,
    /// 9950X3D): games must be kept on the cache die by AMD's driver, Game Bar and core parking.
    /// </summary>
    public static bool IsDualDieX3D(string cpuName) =>
        System.Text.RegularExpressions.Regex.IsMatch(cpuName, @"\b[79]9[05]0X3D", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex GameFolder = new(
        @"\\(steamapps\\common|Epic Games|Riot Games|XboxGames|GOG Games|GOG Galaxy\\Games|Ubisoft Game Launcher\\games|EA Games|Electronic Arts|Battle\.net|Rockstar Games)\\",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>The exe is in a folder that a game launcher installs games into.</summary>
    public static bool IsGamePath(string? path) => path is not null && GameFolder.IsMatch(path);

    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "ApplicationFrameHost.exe", "LockApp.exe", "SearchHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "TextInputHost.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "vivaldi.exe",
        "vlc.exe", "mpc-hc64.exe", "mpc-hc.exe", "mpc-be64.exe", "PotPlayerMini64.exe", "wmplayer.exe", "Microsoft.Media.Player.exe", "Video.UI.exe", "Netflix.exe", "Spotify.exe",
        "POWERPNT.EXE", "mstsc.exe", "CHC.exe",
    };

    /// <summary>Programs that fill the screen without being games: the desktop, browsers, video players, presentations.</summary>
    public static bool IsNotAGame(string exeName) => NotGames.Contains(exeName);

    private static readonly HashSet<string> WindowsComponents = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "smss.exe", "csrss.exe", "wininit.exe", "winlogon.exe", "services.exe", "lsass.exe", "LsaIso.exe",
        "svchost.exe", "dwm.exe", "explorer.exe", "fontdrvhost.exe", "sihost.exe", "taskhostw.exe", "ctfmon.exe", "spoolsv.exe", "dllhost.exe", "conhost.exe", "WUDFHost.exe",
        "RuntimeBroker.exe", "ApplicationFrameHost.exe", "SearchHost.exe", "SearchIndexer.exe", "SearchProtocolHost.exe", "SearchFilterHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "TextInputHost.exe", "LockApp.exe", "SystemSettings.exe", "WmiPrvSE.exe", "audiodg.exe", "backgroundTaskHost.exe", "CompatTelRunner.exe",
        "MsMpEng.exe", "NisSrv.exe", "MpDefenderCoreService.exe", "SecurityHealthService.exe", "SecurityHealthSystray.exe", "smartscreen.exe",
        "TiWorker.exe", "TrustedInstaller.exe", "wuauclt.exe", "MoUsoCoreWorker.exe", "usocoreworker.exe", "msiexec.exe",
    };

    /// <summary>Part of Windows: nothing the user should be told to close.</summary>
    public static bool IsWindowsComponent(string exeName) => WindowsComponents.Contains(exeName);

    /// <summary>GeForce RTX 40 and 50 series: DLSS Frame Generation needs hardware-accelerated GPU scheduling.</summary>
    public static bool SupportsFrameGeneration(string gpuName) =>
        System.Text.RegularExpressions.Regex.IsMatch(gpuName, @"RTX\s?(40|50)\d\d", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>"0x00000116 (VIDEO_TDR_FAILURE)" style text for tables.</summary>
    public static string BugcheckText(uint code)
    {
        var name = Bugcheck(code).Name;
        return name.StartsWith("0x", StringComparison.Ordinal) ? $"0x{code:X8}" : $"0x{code:X8} {name}";
    }

    /// <summary>
    /// Graphics driver DLLs: when a game crashes inside one of these, the driver (or the card) is the suspect.
    /// Returns "nvidia", "amd", "intel" or null.
    /// </summary>
    public static string? GraphicsDriverVendor(string module)
    {
        var m = module.ToLowerInvariant();
        if (m.StartsWith("nvwgf2um") || m.StartsWith("nvd3dum") || m.StartsWith("nvoglv") || m.StartsWith("nvcuda") || m.StartsWith("nvapi") || m.StartsWith("nvldumd") || m.StartsWith("nvgpucomp"))
            return "nvidia";
        if (m.StartsWith("atidxx") || m.StartsWith("amdxx") || m.StartsWith("atiumd") || m.StartsWith("amdvlk") || m.StartsWith("atig6") || m.StartsWith("aticfx") || m.StartsWith("amdxc"))
            return "amd";
        if (m.StartsWith("igd") || m.StartsWith("igxel") || m.StartsWith("ig9icd") || m.StartsWith("igc64") || m.StartsWith("igvk"))
            return "intel";
        return null;
    }

    /// <summary>Which part of the hardware a WHEA-Logger event id refers to.</summary>
    public static string WheaComponent(int eventId) => eventId switch
    {
        17 or 20 => "pcie",
        18 or 19 => "cpu",
        46 or 47 => "memory",
        _ => "other",
    };
}
