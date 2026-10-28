using System.Diagnostics.Eventing.Reader;
using CHC.Core.Models;

namespace CHC.Core.Collection;

/// <summary>Counts hardware related problems in the Windows System event log.</summary>
internal static class StabilityCollector
{
    private const int MaxRecentEvents = 30;

    private sealed record Category(string Key, string ProviderFilter, string? Extra = null);

    private static readonly Category Shutdown = new("shutdown", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)");
    private static readonly Category BlueScreen = new("bluescreen", "(Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] or Provider[@Name='BugCheck']) and (EventID=1001)");
    private static readonly Category WheaError = new("whea", "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=1 or Level=2)");
    private static readonly Category WheaCorrected = new("whea-corrected", "Provider[@Name='Microsoft-Windows-WHEA-Logger'] and (Level=3)");
    private static readonly Category Disk = new("disk", "Provider[@Name='disk'] and (EventID=7 or EventID=11 or EventID=51 or EventID=153)");
    private static readonly Category FileSystem = new("ntfs", "(Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs']) and (EventID=55)");

    public static StabilityInfo Collect()
    {
        var info = new StabilityInfo();
        var recent = new List<EventEntry>();
        try
        {
            info.UnexpectedShutdowns = Count(Shutdown, recent);
            info.BlueScreens = Count(BlueScreen, recent);
            info.WheaErrors = Count(WheaError, recent);
            info.WheaCorrected = Count(WheaCorrected, recent);
            info.DiskErrors = Count(Disk, recent);
            info.FileSystemErrors = Count(FileSystem, recent);
            info.Available = true;
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException)
        {
            info.Available = false;
        }
        info.RecentEvents = recent.OrderByDescending(e => e.Time).Take(MaxRecentEvents).ToList();
        return info;
    }

    private static int Count(Category category, List<EventEntry> recent)
    {
        var milliseconds = (long)TimeSpan.FromDays(StabilityInfo.PeriodDays).TotalMilliseconds;
        var xpath = $"*[System[{category.ProviderFilter} and TimeCreated[timediff(@SystemTime) <= {milliseconds}]]]";
        var query = new EventLogQuery("System", PathType.LogName, xpath) { ReverseDirection = true };

        var count = 0;
        using var reader = new EventLogReader(query);
        for (var record = reader.ReadEvent(); record is not null; record = reader.ReadEvent())
        {
            using (record)
            {
                count++;
                if (count <= MaxRecentEvents)
                {
                    recent.Add(new EventEntry
                    {
                        Time = record.TimeCreated ?? DateTime.MinValue,
                        Source = record.ProviderName ?? "",
                        EventId = record.Id,
                        Level = record.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", 4 => "Information", _ => "" },
                        Category = category.Key,
                    });
                }
            }
        }
        return count;
    }
}
