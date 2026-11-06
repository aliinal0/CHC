namespace CHC.Core.Performance;

/// <summary>One second of a game session: frame rate plus the sensors that explain it.</summary>
public sealed class PerfSample
{
    public double Second { get; set; }
    public double? Fps { get; set; }
    public double? WorstFrameMs { get; set; }
    public int Stutters { get; set; }

    public double? CpuLoad { get; set; }
    public double? CpuMaxCoreLoad { get; set; }
    public double? CpuTemp { get; set; }
    public double? CpuClock { get; set; }

    public double? GpuLoad { get; set; }
    public double? GpuTemp { get; set; }
    public double? GpuHotSpot { get; set; }
    public double? GpuClock { get; set; }
    public double? GpuPowerPercent { get; set; }
    public double? VramUsedMb { get; set; }
    public double? VramTotalMb { get; set; }

    public double? RamLoad { get; set; }
    public double? DiskActivity { get; set; }

    /// <summary>Power the processor package and the graphics card reported, in watts.</summary>
    public double? CpuPowerW { get; set; }
    public double? GpuPowerW { get; set; }

    /// <summary>Laptops only: on battery, charge level, and power into (+) or out of (−) the battery in watts.</summary>
    public bool? OnBattery { get; set; }
    public double? BatteryPercent { get; set; }
    public double? BatteryRateW { get; set; }

    /// <summary>Online games only: average ping this second to 1.1.1.1 and to the game server.</summary>
    public double? PingMs { get; set; }
    public double? ServerPingMs { get; set; }
}

/// <summary>A program other than the game that used CPU during the session.</summary>
public sealed record BackgroundUsage(string Name, double AverageCpu, double PeakCpu);

/// <summary>Everything recorded during one measuring session.</summary>
public sealed class PerfSession
{
    public string Game { get; set; } = "";
    /// <summary>Only used inside CHC: a path under the user's folder would put the Windows user name in shared reports.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string GamePath { get; set; } = "";
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    public TimeSpan Duration => Ended - Started;

    /// <summary>False when frame times could not be captured (no administrator rights, or a non-DirectX game).</summary>
    public bool FpsMeasured { get; set; }
    public int Frames { get; set; }
    public double AverageFps { get; set; }
    public double OnePercentLowFps { get; set; }
    public double PointOnePercentLowFps { get; set; }
    public double MedianFrameMs { get; set; }
    public List<DateTime> StutterTimes { get; set; } = [];

    public int RefreshRateHz { get; set; }
    public List<PerfSample> Samples { get; set; } = [];
    public List<BackgroundUsage> Background { get; set; } = [];

    /// <summary>The game talked to servers on the internet, so the connection was measured too.</summary>
    public bool Online { get; set; }

    /// <summary>The address the game exchanged the most data with (needs administrator rights), empty if unknown.</summary>
    public string ServerAddress { get; set; } = "";

    /// <summary>Bytes the game itself sent and received (needs administrator rights).</summary>
    public ulong GameBytes { get; set; }

    /// <summary>Ping, jitter and packet loss measured while the online game ran.</summary>
    public Network.NetSession? Network { get; set; }

    /// <summary>The game's own graphics settings, read from its settings file after the session; null if unknown.</summary>
    public GameSettings? Settings { get; set; }

    /// <summary>The game's window and the monitor's mode while the game was in front; null if never seen.</summary>
    public GameDisplay? Display { get; set; }

    /// <summary>The PC has a battery (a laptop): its state was recorded every second.</summary>
    public bool HasBattery => Samples.Any(x => x.OnBattery is not null);
}

/// <summary>
/// Power drawn during a session. Only the processor and the graphics card report it; the rest of the PC
/// (motherboard, memory, disks, fans) is an estimate. Peaks are the 98th percentile of the per-second values.
/// </summary>
public sealed record PerfPower(double CpuAverage, double CpuPeak, double? GpuAverage, double? GpuPeak, double Rest)
{
    public double Average => CpuAverage + (GpuAverage ?? 0) + Rest;
    public double Peak => CpuPeak + (GpuPeak ?? 0) + Rest;

    /// <summary>
    /// Power supply size for a desktop: the peak plus 50 % for heavier games, the graphics card's short spikes
    /// and ageing, rounded up to the next 50 W. It also keeps the supply in its efficient middle range.
    /// </summary>
    public int RecommendedPsuW => Math.Max(400, (int)Math.Ceiling(Peak * 1.5 / 50) * 50);
}

/// <summary>Share of the session (0-100) spent limited by each part.</summary>
public sealed record PerfBreakdown(double Gpu, double Cpu, double Capped, double Other);
