using System.Text.RegularExpressions;
using CHC.Core.History;
using CHC.Core.Sensors;

namespace CHC.Core.Power;

/// <summary>The power supply's 80 PLUS certificate; it sets how much of the power drawn from the wall is lost as heat.</summary>
public enum PsuRating
{
    Unknown,
    Standard,
    White,
    Bronze,
    Silver,
    Gold,
    Platinum,
    Titanium,
}

/// <summary>One of the supply's outputs (12 V, 5 V, 3.3 V): now, at its lowest lately, and what the ATX standard allows (±5 %).</summary>
public sealed record RailReading(string Name, double Nominal, double? Now, double? Lowest, bool FromSupply)
{
    public double LowLimit => Nominal * 0.95;
    public double HighLimit => Nominal * 1.05;

    /// <summary>False when the lowest reading fell below what the standard allows.</summary>
    public bool? InSpec => Lowest is { } low ? low >= LowLimit : Now is { } now ? now >= LowLimit && now <= HighLimit : null;
}

/// <summary>
/// The power supply as far as CHC can know it. Ordinary supplies don't talk to the PC at all, so their wattage comes
/// from the user; a few (Corsair HXi/RMi/AXi, some MSI models) report their own power, temperature and rails over USB.
/// The voltages otherwise come from the motherboard, which measures them only roughly.
/// </summary>
public sealed class PowerSupplyInfo
{
    public string? Model { get; set; }
    public int? RatedWatts { get; set; }
    public PsuRating Rating { get; set; }

    /// <summary>The supply reports its own readings (rather than the user typing in its wattage).</summary>
    public bool Reports { get; set; }

    public double? OutputW { get; set; }
    public double? InputW { get; set; }
    public double? Temperature { get; set; }
    public double? FanRpm { get; set; }
    public List<RailReading> Rails { get; set; } = [];

    /// <summary>The most the PC drew in the last 30 days (the supply's own figure if it reports one, else an estimate).</summary>
    public double? PeakW { get; set; }
    public DateTime? PeakTime { get; set; }

    /// <summary>What was in front at the peak; kept out of reports.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PeakApp { get; set; }

    /// <summary>A size that leaves room for the graphics card's spikes, heavier games and ageing (as for the game test).</summary>
    public int? RecommendedWatts => PeakW is { } peak ? PowerSupplyMath.Recommended(peak) : null;

    /// <summary>Peak as a share of the rated wattage (0-1+).</summary>
    public double? PeakLoad => PeakW is { } peak && RatedWatts is > 0 and var rated ? peak / rated : null;
}

public static partial class PowerSupplyMath
{
    /// <summary>The peak plus 50 %, rounded up to the next 50 W, at least 400 W.</summary>
    public static int Recommended(double peakW) => Math.Max(400, (int)Math.Ceiling(peakW * 1.5 / 50) * 50);

    /// <summary>
    /// Efficiency (0-1) at a share of the rated load, from the 80 PLUS minimums for 115 V (20, 50 and 100 % load);
    /// real supplies usually do a little better, and on 230 V a little better still.
    /// </summary>
    public static double Efficiency(PsuRating rating, double load)
    {
        var (at20, at50, at100) = rating switch
        {
            PsuRating.White => (0.80, 0.80, 0.80),
            PsuRating.Bronze => (0.82, 0.85, 0.82),
            PsuRating.Silver => (0.85, 0.88, 0.85),
            PsuRating.Gold => (0.87, 0.90, 0.87),
            PsuRating.Platinum => (0.90, 0.92, 0.89),
            PsuRating.Titanium => (0.92, 0.94, 0.90),
            _ => (0.75, 0.78, 0.75),
        };
        load = Math.Clamp(load, 0.05, 1.1);
        if (load < 0.2) return at20 - (0.2 - load) / 0.15 * (rating == PsuRating.Titanium ? 0.02 : 0.06);
        if (load <= 0.5) return at20 + (load - 0.2) / 0.3 * (at50 - at20);
        return at50 + (Math.Min(load, 1) - 0.5) / 0.5 * (at100 - at50);
    }

    /// <summary>The wattage in a supply's name: "RM850x" → 850, "HX1000i" → 1000, "MEG Ai1300P" → 1300.</summary>
    public static int? WattsInName(string? name)
    {
        if (name is null) return null;
        var m = WattsPattern().Match(name);
        return m.Success && int.TryParse(m.Groups[1].Value, out var w) && w is >= 250 and <= 2500 ? w : null;
    }

    [GeneratedRegex(@"(?<!\d)(\d{3,4})(?:\s?W\b|[A-Za-z]*\b)", RegexOptions.IgnoreCase)]
    private static partial Regex WattsPattern();

    /// <summary>
    /// Everything CHC knows about the supply: its own readings if it reports them, else the user's entry; the rails
    /// (from the supply or the motherboard) with their lowest point in the last 30 days; and the peak power drawn.
    /// </summary>
    public static PowerSupplyInfo Build(SensorSnapshot sensors, int? userWatts, PsuRating userRating, string? userModel, HistoryStore? history)
    {
        var info = new PowerSupplyInfo { Model = string.IsNullOrWhiteSpace(userModel) ? null : userModel.Trim(), RatedWatts = userWatts, Rating = userRating };
        var psu = sensors.OfKind("Psu").FirstOrDefault();
        if (psu is not null)
        {
            info.Reports = true;
            info.Model = psu.Name;
            info.RatedWatts ??= WattsInName(psu.Name);
            var power = psu.Sensors.Where(r => r.Type == "Power" && r.Value is > 0 and < 3000).ToList();
            info.InputW = power.FirstOrDefault(r => r.Name.Contains("In", StringComparison.OrdinalIgnoreCase))?.Value;
            info.OutputW = HistoryRecorder.PsuOutput(psu);
            info.Temperature = psu.Sensors.Where(r => r.Type == "Temperature" && r.Value > 0).Select(r => (double?)r.Value).Max();
            info.FanRpm = psu.Sensors.FirstOrDefault(r => r.Type == "Fan" && r.Value > 0)?.Value;
        }

        var now = DateTime.Now;
        IEnumerable<SensorDevice> supply = psu is null ? [] : [psu];
        var boards = sensors.Devices.Where(d => d.Kind is "Motherboard" or "SuperIO").ToList();
        RailReading? Rail(string name, double nominal, Regex pattern, HistoryMetric metric)
        {
            var fromSupply = HistoryRecorder.Rail(supply, pattern, nominal * 0.9, nominal * 1.1);
            var value = fromSupply ?? HistoryRecorder.Rail(boards, pattern, nominal * 0.9, nominal * 1.1);
            var lowest = history?.Series(metric, now.AddDays(-30), now.AddMinutes(1), TimeSpan.FromDays(1)).Select(p => p.Min).OfType<double>().DefaultIfEmpty().Min();
            if (lowest is 0) lowest = null;
            return value is null && lowest is null ? null : new RailReading(name, nominal, value, lowest, fromSupply is not null);
        }
        foreach (var rail in new[]
                 {
                     Rail("12 V", 12, HistoryRecorder.Twelve(), HistoryMetric.Volt12),
                     Rail("5 V", 5, HistoryRecorder.Five(), HistoryMetric.Volt5),
                     Rail("3.3 V", 3.3, HistoryRecorder.ThreeThree(), HistoryMetric.Volt33),
                 })
            if (rail is not null) info.Rails.Add(rail);

        if (history?.Peak(HistoryMetric.SystemPower, now.AddDays(-30), now.AddMinutes(1)) is { } peak)
        {
            info.PeakW = peak.Value;
            info.PeakTime = peak.Time;
            info.PeakApp = peak.App;
        }
        return info;
    }
}
