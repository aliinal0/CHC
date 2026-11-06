namespace CHC.Core.History;

/// <summary>One minute of the PC's life as the recorder keeps it: averages and peaks of the readings taken in it.</summary>
public sealed class MinuteRecord
{
    /// <summary>Start of the minute, in UTC.</summary>
    public DateTime Start { get; set; }
    public int Samples { get; set; }

    public double? CpuTemp { get; set; }
    public double? CpuTempMax { get; set; }
    public double? GpuTemp { get; set; }
    public double? GpuTempMax { get; set; }
    public double? CpuLoad { get; set; }
    public double? CpuLoadMax { get; set; }
    public double? GpuLoad { get; set; }
    public double? GpuLoadMax { get; set; }
    public double? RamLoad { get; set; }

    public double? CpuPower { get; set; }
    public double? CpuPowerMax { get; set; }
    public double? GpuPower { get; set; }
    public double? GpuPowerMax { get; set; }

    /// <summary>Whole PC: the power supply's own reading when it reports one, else processor + graphics card + an allowance for the rest.</summary>
    public double? SystemPower { get; set; }
    public double? SystemPowerMax { get; set; }
    public bool SystemPowerMeasured { get; set; }

    /// <summary>The motherboard's reading of the power supply's 12 V rail; the lowest one shows how it holds up under load.</summary>
    public double? Volt12 { get; set; }
    public double? Volt12Min { get; set; }
    public double? Volt5Min { get; set; }
    public double? Volt33Min { get; set; }

    public double? FanMax { get; set; }
    public double? DiskTempMax { get; set; }

    /// <summary>Bytes received and sent by all network adapters in the minute.</summary>
    public long NetDown { get; set; }
    public long NetUp { get; set; }

    /// <summary>The program in front for most of the minute (only its file name, never a window title).</summary>
    public string? App { get; set; }

    /// <summary>The program's own name from its file details ("Google Chrome" for chrome.exe), if it has one.</summary>
    public string? AppTitle { get; set; }

    public bool Fullscreen { get; set; }

    /// <summary>Where the exe of a program running fullscreen is, so a game's own settings file can be found.</summary>
    public string? AppPath { get; set; }

    /// <summary>Frame rate while a program ran fullscreen (needs administrator rights), and that of its slowest 1% of frames.</summary>
    public double? Fps { get; set; }
    public double? FpsLow { get; set; }

    public bool? OnBattery { get; set; }
    public double? BatteryPercent { get; set; }
}

/// <summary>A value the history can chart.</summary>
public enum HistoryMetric
{
    CpuTemp,
    GpuTemp,
    CpuLoad,
    GpuLoad,
    RamLoad,
    CpuPower,
    GpuPower,
    SystemPower,
    Volt12,
    Volt5,
    Volt33,
    Fan,
    DiskTemp,
    NetDown,
    NetUp,
}

/// <summary>One point of a chart: the average, lowest and highest value in its time slot.</summary>
public sealed record HistoryPoint(DateTime Time, double? Average, double? Min, double? Max);

/// <summary>How long a program was in front in a period, and how warm the PC ran meanwhile.</summary>
public sealed record AppUsage(string Name, string? Title, int Minutes, int FullscreenMinutes, double? CpuTempAverage, double? CpuTempMax, double? GpuTempAverage, double? GpuTempMax, double? SystemPowerMax);

/// <summary>A program that ran fullscreen in a period: how long, how fast, and how hard it worked the PC.</summary>
/// <param name="FpsBest">The fastest minute: a game held by a frame limit never gets past it.</param>
/// <param name="GpuBusyShare">Percent of the minutes with the graphics card at 90% load or more.</param>
public sealed record GameUsage(string Name, string? Title, string? Path, int Minutes, DateTime LastPlayed, double? Fps, double? FpsLow, double? FpsBest, int FpsMinutes,
    double? GpuLoad, double GpuBusyShare, double? CpuLoad, double? GpuTempMax, double? CpuTempMax, double? RamLoad);

/// <summary>One program's use in one hour, separately for the time a game was being played. The sums are value × seconds.</summary>
public sealed class ProgramHour
{
    /// <summary>Start of the hour, in UTC.</summary>
    public DateTime Start { get; set; }
    public string App { get; set; } = "";
    public string? AppTitle { get; set; }

    /// <summary>The game being played meanwhile, or null.</summary>
    public string? Game { get; set; }
    public double Seconds { get; set; }
    public double FrontSeconds { get; set; }
    public double CpuSum { get; set; }
    public double CpuMax { get; set; }
    public double MemorySum { get; set; }
    public double MemoryMax { get; set; }
    public long ReadBytes { get; set; }
    public long WriteBytes { get; set; }
    public double GpuSum { get; set; }
    public double GpuMax { get; set; }
    public double VideoSum { get; set; }
    public double ComputeSum { get; set; }
    public double VramMax { get; set; }
    public string? Adapter { get; set; }
}

/// <summary>What a program used over a period; percentages of the whole processor or graphics engine, memory in MB.</summary>
public sealed record ProgramUsage(string Name, string? Title, double Seconds, double FrontSeconds, double CpuAverage, double CpuMax, double MemoryAverageMb, double MemoryMaxMb,
    long ReadBytes, long WriteBytes, double GpuAverage, double GpuMax, double VideoAverage, double ComputeAverage, double VramMaxMb, string? Adapter);

/// <summary>The highest value of a metric in a period, when it happened and what was in front then.</summary>
public sealed record HistoryPeak(HistoryMetric Metric, double Value, DateTime Time, string? App);

/// <summary>The spans shown on the history pages, each with the slot one chart point covers.</summary>
public enum HistoryRange
{
    Day,
    Week,
    Month,
    Year,
}

public static class HistoryRanges
{
    public static TimeSpan Length(this HistoryRange range) => range switch
    {
        HistoryRange.Day => TimeSpan.FromDays(1),
        HistoryRange.Week => TimeSpan.FromDays(7),
        HistoryRange.Month => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(365),
    };

    /// <summary>
    /// Five minutes in a day and half hours in a week show the rhythm of each day; a month and a year show one point
    /// per day, so the trend isn't lost in every evening's game.
    /// </summary>
    public static TimeSpan Slot(this HistoryRange range) => range switch
    {
        HistoryRange.Day => TimeSpan.FromMinutes(5),
        HistoryRange.Week => TimeSpan.FromMinutes(30),
        _ => TimeSpan.FromDays(1),
    };
}
