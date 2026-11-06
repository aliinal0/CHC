using CHC.Core.History;

namespace CHC.Core.Demo;

/// <summary>
/// Two months in the life of the demo PC for the history pages: office hours in a browser and an editor, evenings
/// of VALORANT, Fortnite and Cyberpunk 2077, a heat wave, a big Steam download, the PC off at night. Made up and
/// the same on every run; nothing comes from the computer CHC runs on.
/// </summary>
public static class DemoHistory
{
    private sealed record Activity(string? App, string? Title, double Cpu, double Gpu, double CpuW, double GpuW, double Ram, double NetMb, bool Fullscreen, double Fps = 0, string? Path = null);

    private static readonly Activity Away = new(null, null, 4, 2, 28, 14, 44, 0.05, false);
    private static readonly Activity Browser = new("chrome.exe", "Google Chrome", 12, 8, 38, 22, 52, 6, false);
    private static readonly Activity Video = new("chrome.exe", "Google Chrome", 9, 18, 34, 38, 50, 25, false);
    private static readonly Activity Editor = new("Code.exe", "Visual Studio Code", 16, 4, 42, 16, 58, 0.5, false);
    private static readonly Activity Chat = new("Discord.exe", "Discord", 7, 6, 32, 20, 48, 1.5, false);
    private static readonly Activity Music = new("Spotify.exe", "Spotify", 5, 3, 30, 15, 46, 3, false);
    private static readonly Activity Files = new("explorer.exe", "Windows Explorer", 6, 3, 30, 15, 45, 0.2, false);
    private static readonly Activity Valorant = new("VALORANT-Win64-Shipping.exe", "VALORANT", 38, 58, 62, 135, 61, 1.6, true, 345, @"C:\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe");
    private static readonly Activity Fortnite = new("FortniteClient-Win64-Shipping.exe", "Fortnite", 34, 86, 66, 190, 66, 2.2, true, 124, @"C:\Program Files\Epic Games\Fortnite\FortniteGame\Binaries\Win64\FortniteClient-Win64-Shipping.exe");
    private static readonly Activity Cyberpunk = new("Cyberpunk2077.exe", "Cyberpunk 2077", 42, 98, 74, 218, 72, 0.3, true, 76, @"C:\Program Files (x86)\Steam\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe");

    public static List<MinuteRecord> Create(bool laptop, int days = 60)
    {
        var random = new Random(2026);
        // Its own generator: frame rates don't change the temperatures the rest of the demo shows.
        var frames = new Random(2027);
        var list = new List<MinuteRecord>();
        var now = DateTime.Now;
        double cpuTemp = 44, gpuTemp = 38;
        for (var d = days - 1; d >= 0; d--)
        {
            var day = now.Date.AddDays(-d);
            // The PC was off overnight: it starts the day cold.
            cpuTemp = 36;
            gpuTemp = 32;
            var weekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            // A heat wave three weeks ago: the room is warmer, so is everything in the case.
            var room = d is >= 19 and <= 24 ? 4.5 : 0;
            // One Saturday a big game update comes down.
            var download = weekend && d is >= 15 and <= 21 && day.DayOfWeek == DayOfWeek.Saturday;
            // The demo's freeze and blue screen (see DemoData) come during Cyberpunk, whichever day the demo is opened on.
            foreach (var (start, end, activity) in d is 1 or 3 ? WithCyberpunkEvening(Schedule(random, weekend)) : Schedule(random, weekend))
            {
                for (var minute = start; minute < end; minute++)
                {
                    var t = day.AddMinutes(minute);
                    if (t > now) break;
                    var a = activity;
                    var noise = random.NextDouble() - 0.5;
                    var load = a.Cpu * (0.85 + random.NextDouble() * 0.3);
                    var gpuLoad = Math.Min(99, a.Gpu * (0.9 + random.NextDouble() * 0.2));
                    // Temperatures follow the load with a lag, like a cooler warming up.
                    cpuTemp += (41 + room + load * 0.72 - cpuTemp) * 0.35 + noise * 1.2;
                    gpuTemp += (34 + room + gpuLoad * 0.42 - gpuTemp) * 0.3 + noise;
                    var cpuW = (laptop ? a.CpuW * 0.45 : a.CpuW) * (0.92 + random.NextDouble() * 0.16);
                    var gpuW = (laptop ? a.GpuW * 0.5 : a.GpuW) * (0.9 + random.NextDouble() * 0.2);
                    var rest = laptop ? 15 : 60;
                    var net = a.NetMb * (0.5 + random.NextDouble());
                    if (download && minute is >= 14 * 60 and < 14 * 60 + 14) net += 4200 + random.NextDouble() * 900;
                    var gaming = a.Fullscreen;
                    // The driver update on the demo's timeline (four days ago, 20:05) cost Cyberpunk about a fifth of its FPS.
                    var slower = a == Cyberpunk && t >= now.Date.AddDays(-4).AddHours(20) ? 0.8 : 1;
                    var fps = a.Fps * slower * (laptop ? 0.6 : 1) * (0.93 + frames.NextDouble() * 0.14);
                    list.Add(new MinuteRecord
                    {
                        Start = t.ToUniversalTime(),
                        Samples = 20,
                        CpuTemp = cpuTemp,
                        CpuTempMax = cpuTemp + 2 + random.NextDouble() * (gaming ? 6 : 4),
                        GpuTemp = gpuTemp,
                        GpuTempMax = gpuTemp + 1 + random.NextDouble() * 2,
                        CpuLoad = load,
                        CpuLoadMax = Math.Min(100, load * 1.6 + 5),
                        GpuLoad = gpuLoad,
                        GpuLoadMax = Math.Min(100, gpuLoad + 6),
                        RamLoad = a.Ram + noise * 4,
                        CpuPower = cpuW,
                        CpuPowerMax = cpuW * 1.18,
                        GpuPower = gpuW,
                        GpuPowerMax = gpuW * 1.12,
                        SystemPower = cpuW + gpuW + rest,
                        SystemPowerMax = (cpuW + gpuW) * 1.15 + rest,
                        Volt12 = 12.07 - gpuW * 0.00045 + noise * 0.01,
                        Volt12Min = 12.05 - gpuW * 0.0006 - random.NextDouble() * 0.02,
                        Volt5Min = 5.03 - random.NextDouble() * 0.02,
                        Volt33Min = 3.31 - random.NextDouble() * 0.02,
                        FanMax = 780 + gpuLoad * 9 + (cpuTemp - 40) * 6 + noise * 40,
                        DiskTempMax = 35 + room * 0.6 + (gaming ? 4 : 0) + noise,
                        NetDown = (long)(net * 1_000_000),
                        NetUp = (long)(net * 1_000_000 * (gaming ? 0.4 : 0.06)),
                        App = a.App,
                        AppTitle = a.Title,
                        Fullscreen = a.Fullscreen,
                        AppPath = a.Path,
                        Fps = gaming ? fps : null,
                        FpsLow = gaming ? fps * (0.62 + frames.NextDouble() * 0.1) : null,
                        OnBattery = laptop ? !gaming && minute < 13 * 60 : null,
                        BatteryPercent = laptop ? Math.Max(20, 100 - (minute % 300) / 4.0) : null,
                    });
                }
            }
        }
        return list;
    }

    /// <summary>What a program of the demo PC typically uses: load in percent, memory and video memory in MB, data in MB per hour.</summary>
    private sealed record Profile(string App, string Title, double Cpu, double MemoryMb, double ReadMb, double WriteMb, double Gpu = 0, double Video = 0, double VramMb = 0);

    private const string DemoCard = "NVIDIA GeForce RTX 4070 SUPER";

    private static readonly Profile[] Profiles =
    [
        new("chrome.exe", "Google Chrome", 4.5, 3400, 420, 260, 2.5, 1.5, 320),
        new("Discord.exe", "Discord", 1.4, 620, 60, 40, 2, 0, 160),
        new("Spotify.exe", "Spotify", 0.8, 340, 90, 30),
        new("Code.exe", "Visual Studio Code", 1.6, 1050, 120, 80),
        new("steam.exe", "Steam", 0.5, 310, 150, 60, 0.3, 0, 60),
        new("OneDrive.exe", "Microsoft OneDrive", 0.3, 190, 90, 110),
        // The demo's one troublemaker: a made-up sync tool that never rests.
        new("PhotoSyncHelper.exe", "Photo Sync Helper", 8.5, 430, 1900, 2600),
        new("MsMpEng.exe", "Antimalware Service Executable", 1.1, 260, 320, 40),
        new("dwm.exe", "Desktop Window Manager", 1.3, 110, 0, 0, 2.5, 0, 700),
        new("svchost.exe", "Host Process for Windows Services", 1.0, 820, 160, 120),
        new("System", "System", 0.9, 1, 500, 380),
        new("explorer.exe", "Windows Explorer", 0.6, 180, 40, 10, 0.2, 0, 90),
        new("CHC.exe", "CHC - Computer Health Check", 0.3, 110, 5, 3),
    ];

    private static readonly Dictionary<string, (double MemoryMb, double VramMb)> GameLoads = new(StringComparer.OrdinalIgnoreCase)
    {
        [Valorant.App!] = (3200, 3400),
        [Fortnite.App!] = (5400, 6800),
        [Cyberpunk.App!] = (9300, 11600),
    };

    /// <summary>What every program used in those minutes, hour by hour: everyday programs behind each game too, as on a real PC.</summary>
    public static List<ProgramHour> Programs(IReadOnlyList<MinuteRecord> minutes, bool laptop)
    {
        var random = new Random(2028);
        var rows = new List<ProgramHour>();
        const double MiB = 1024 * 1024;
        foreach (var hour in minutes.GroupBy(m => new DateTime(m.Start.Ticks / TimeSpan.TicksPerHour * TimeSpan.TicksPerHour, DateTimeKind.Utc)))
        {
            foreach (var part in hour.GroupBy(m => m.Fullscreen ? m.App : null))
            {
                var seconds = part.Count() * 60.0;
                var game = part.Key;
                foreach (var p in Profiles)
                {
                    var noise = 0.8 + random.NextDouble() * 0.4;
                    var share = seconds / 3600;
                    rows.Add(new ProgramHour
                    {
                        Start = hour.Key,
                        App = p.App,
                        AppTitle = p.Title,
                        Game = game,
                        Seconds = seconds,
                        FrontSeconds = part.Count(m => string.Equals(m.App, p.App, StringComparison.OrdinalIgnoreCase)) * 60.0,
                        CpuSum = p.Cpu * noise * seconds,
                        CpuMax = p.Cpu * noise * 3,
                        MemorySum = p.MemoryMb * noise * seconds,
                        MemoryMax = p.MemoryMb * 1.3,
                        ReadBytes = (long)(p.ReadMb * noise * share * MiB),
                        WriteBytes = (long)(p.WriteMb * noise * share * MiB),
                        GpuSum = p.Gpu * noise * seconds,
                        GpuMax = p.Gpu * 2,
                        VideoSum = p.Video * noise * seconds,
                        VramMax = p.VramMb,
                        Adapter = p.Gpu > 0 ? DemoCard : null,
                    });
                }
                if (game is null || !GameLoads.TryGetValue(game, out var load)) continue;
                rows.Add(new ProgramHour
                {
                    Start = hour.Key,
                    App = game,
                    AppTitle = part.First().AppTitle,
                    Game = game,
                    Seconds = seconds,
                    FrontSeconds = seconds,
                    CpuSum = part.Average(m => m.CpuLoad ?? 0) * 0.8 * seconds,
                    CpuMax = part.Max(m => m.CpuLoadMax ?? 0),
                    MemorySum = load.MemoryMb * seconds,
                    MemoryMax = load.MemoryMb * 1.1,
                    ReadBytes = (long)(1400 * seconds / 3600 * MiB),
                    WriteBytes = (long)(120 * seconds / 3600 * MiB),
                    GpuSum = part.Average(m => m.GpuLoad ?? 0) * seconds,
                    GpuMax = part.Max(m => m.GpuLoadMax ?? 0),
                    VramMax = laptop ? load.VramMb * 0.5 : load.VramMb,
                    Adapter = DemoCard,
                });
            }
        }
        return rows;
    }

    /// <summary>One day: when the PC is on and what is in front, in minutes after midnight.</summary>
    private static IEnumerable<(int Start, int End, Activity Activity)> WithCyberpunkEvening(IEnumerable<(int Start, int End, Activity Activity)> day)
    {
        const int from = 20 * 60 + 40, to = 22 * 60 + 20;
        var played = false;
        foreach (var (start, end, activity) in day)
        {
            if (end <= from)
            {
                yield return (start, end, activity);
                continue;
            }
            if (start < from) yield return (start, from, activity);
            if (!played)
            {
                played = true;
                yield return (from, to, Cyberpunk);
            }
            if (end > to) yield return (Math.Max(start, to), end, activity);
        }
    }

    private static IEnumerable<(int Start, int End, Activity Activity)> Schedule(Random r, bool weekend)
    {
        var t = (weekend ? 10 * 60 + 30 : 8 * 60 + 40) + r.Next(0, 60);
        var off = 23 * 60 + 10 + r.Next(0, 80);
        var gameStart = weekend ? 14 * 60 + r.Next(0, 120) : 19 * 60 + 30 + r.Next(0, 60);
        while (t < off)
        {
            Activity a;
            int length;
            if (t >= gameStart && r.NextDouble() < (weekend ? 0.8 : 0.65))
            {
                var pick = r.NextDouble();
                a = pick < 0.5 ? Valorant : pick < 0.8 ? Fortnite : Cyberpunk;
                length = 35 + r.Next(0, 70);
            }
            else if (!weekend && t < 18 * 60)
            {
                var pick = r.NextDouble();
                a = pick < 0.38 ? Browser : pick < 0.68 ? Editor : pick < 0.78 ? Chat : pick < 0.85 ? Music : pick < 0.9 ? Files : Away;
                length = 10 + r.Next(0, 50);
            }
            else
            {
                var pick = r.NextDouble();
                a = pick < 0.35 ? Video : pick < 0.6 ? Browser : pick < 0.75 ? Chat : pick < 0.85 ? Music : Away;
                length = 10 + r.Next(0, 40);
            }
            // Lunch away from the PC.
            if (!weekend && t is >= 12 * 60 + 30 and < 13 * 60 + 15) (a, length) = (Away, 40);
            var end = Math.Min(off, t + length);
            yield return (t, end, a);
            t = end;
        }
    }
}
