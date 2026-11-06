using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CHC.Core.Devices;
using CHC.Core.Health;
using CHC.Core.History;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Past;
using CHC.Core.Performance;
using CHC.Core.Presentation;

namespace CHC.Core.Reporting;

/// <param name="IncludeUsage">Which games and programs are used: not everyone wants that in what they share.</param>
public sealed record ReportOptions(bool IncludeSerials = false, bool IncludeSensors = true, bool IncludeUsage = true);

/// <summary>
/// Exports a scan as a self-contained HTML page (also the template for the PDF report), JSON or text. The report is meant to read
/// like a document from a service desk: what is wrong and what to do first, then the rest for whoever wants the details.
/// </summary>
public static class ReportWriter
{
    private const int FirstThings = 3;
    private const int ShortWord = 16;

    public static string Html(SystemSnapshot s, HealthReport health, ReportOptions options, ReportUsage? usage = null)
    {
        var sections = SectionBuilder.Build(s, health, options.IncludeSerials);
        var sb = new StringBuilder(64 * 1024);
        sb.Append($"<!DOCTYPE html><html lang=\"{Loc.Instance.CurrentLanguage}\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{E(Loc.T("report.title"))} — {E(Fmt.DateAndTime(s.TakenAt))}</title><style>{Stylesheet}</style></head><body><main>");

        Front(sb, s, health, sections);
        Problems(sb, s, health, options);
        if (usage is not null)
        {
            Games(sb, usage);
            Programs(sb, usage);
        }
        Devices(sb, s);
        if (usage is not null) Use(sb, usage);
        Details(sb, s, sections, options);

        sb.Append("<p class=\"end soft\">").Append(E(Loc.T("report.footer", s.AppVersion))).Append("</p></main></body></html>");
        return sb.ToString();
    }

    // ---- first page: the answer, then what to do about it

    private static void Front(StringBuilder sb, SystemSnapshot s, HealthReport health, IReadOnlyList<ComponentSection> sections)
    {
        var summary = SystemSummary(s).ToList();
        sb.Append("<header><div class=\"id\">").Append(LogoSvg).Append("<div><h1>").Append(E(Loc.T("report.title"))).Append("</h1><p class=\"soft\">")
          .Append(E(summary[0].Value)).Append("</p></div></div><p class=\"when soft\">").Append(E(Fmt.DateAndTime(s.TakenAt))).Append("<br>CHC ").Append(E(s.AppVersion)).Append("</p></header>");

        sb.Append("<div class=\"verdict\"><span class=\"n\">").Append(health.Score).Append("</span><span class=\"of\">/ 100</span><span class=\"rating ")
          .Append(Css(health.RatingSeverity)).Append("\">").Append(E(Loc.T(health.RatingKey))).Append("</span></div>");
        sb.Append("<p class=\"lead\">").Append(E(Verdict(health))).Append("</p>");
        if (!s.IsElevated) sb.Append("<p class=\"soft\">").Append(E(Loc.T("report.notelevated"))).Append("</p>");

        var first = health.Noteworthy.Where(f => f.Severity >= Severity.Warning).Take(FirstThings).ToList();
        if (first.Count > 0)
        {
            sb.Append("<h2>").Append(E(Loc.T("report.first"))).Append("</h2><ol class=\"first\">");
            foreach (var f in first)
            {
                sb.Append("<li><b>").Append(E(f.Title)).Append("</b>");
                if (f.HasRecommendation) sb.Append("<br>").Append(E(f.Recommendation));
                sb.Append("</li>");
            }
            sb.Append("</ol>");
        }

        sb.Append("<h2>").Append(E(Loc.T("report.parts"))).Append("</h2><table><thead><tr><th>").Append(E(Loc.T("report.col.part"))).Append("</th><th>").Append(E(Loc.T("report.col.status")))
          .Append("</th><th class=\"num\">").Append(E(Loc.T("report.col.score"))).Append("</th><th>").Append(E(Loc.T("report.col.short"))).Append("</th></tr></thead><tbody>");
        foreach (var c in sections)
        {
            sb.Append("<tr><td class=\"name\">").Append(E(c.Title)).Append("</td><td>").Append(Status(c.Severity)).Append("</td><td class=\"num\">")
              .Append(c.HasScore ? c.Score.ToString(CultureInfo.InvariantCulture) : Fmt.Dash).Append("</td><td>").Append(E(c.Summary)).Append("</td></tr>");
        }
        sb.Append("</tbody></table>");

        sb.Append("<h2>").Append(E(Loc.T("report.pc"))).Append("</h2><table class=\"kv\"><tbody>");
        foreach (var (k, v) in summary) sb.Append("<tr><th>").Append(E(k)).Append("</th><td>").Append(E(v)).Append("</td></tr>");
        sb.Append("</tbody></table>");
    }

    private static string Verdict(HealthReport health)
    {
        // Here an urgent update counts as what it is: a warning.
        var criticals = health.Noteworthy.Count(f => f.Severity == Severity.Critical);
        var warnings = health.Noteworthy.Count(f => f.Severity == Severity.Warning);
        var tips = health.InfoCount;
        if (criticals > 0) return criticals == 1 ? Loc.T("report.verdict.critical.one") : Loc.T("report.verdict.critical", criticals);
        if (warnings > 0) return warnings == 1 ? Loc.T("report.verdict.warning.one") : Loc.T("report.verdict.warning", warnings);
        if (health.UpdateCount > 0) return health.UpdateCount == 1 ? Loc.T("report.verdict.updates.one") : Loc.T("report.verdict.updates");
        return tips switch
        {
            0 => Loc.T("report.verdict.clean"),
            1 => Loc.T("report.verdict.tips.one"),
            _ => Loc.T("report.verdict.tips", tips),
        };
    }

    // ---- what is wrong, in full

    private static void Problems(StringBuilder sb, SystemSnapshot s, HealthReport health, ReportOptions options)
    {
        var problems = health.Issues.Where(f => f.Severity >= Severity.Warning).ToList();
        if (problems.Count > 0)
        {
            sb.Append("<h2 class=\"page\">").Append(E(Loc.T("report.problems"))).Append("</h2>");
            foreach (var f in problems) Item(sb, f, withPart: true, full: true);
        }
        // Drivers and Windows first. The programs are suggestions, and they go when the report leaves out what programs are used.
        var updates = health.Updates;
        List<Updates.AvailableUpdate> suggestions = options.IncludeUsage ? [.. s.Updates.Runtimes, .. s.Updates.Programs] : [];
        if (updates.Count > 0 || suggestions.Count > 0)
        {
            sb.Append("<h2>").Append(E(Loc.T("report.updates"))).Append("</h2><p class=\"soft\">").Append(E(Loc.T("report.updates.text"))).Append("</p>");
            foreach (var f in updates) Item(sb, f, withPart: true, full: true);
            if (suggestions.Count > 0)
                Table(sb, Loc.T("report.updates.suggested"), [Loc.T("programs.col.program"), Loc.T("report.col.installed"), Loc.T("report.col.available")],
                    suggestions.Select(u => (IReadOnlyList<string>)[u.Name, u.Installed, u.Latest]).ToList());
        }
        var tips = health.Issues.Where(f => f.Severity == Severity.Info).ToList();
        if (tips.Count > 0)
        {
            sb.Append("<h2>").Append(E(Loc.T("report.tips"))).Append("</h2>");
            foreach (var f in tips) Item(sb, f, withPart: true, full: false);
        }
    }

    private static void Item(StringBuilder sb, Finding f, bool withPart, bool full)
    {
        sb.Append("<div class=\"item\"><p class=\"head\">").Append(f.IsUpdate ? Badge(f.Severity, Severity.Update) : Status(f.Severity)).Append(" <b>").Append(E(f.Title)).Append("</b>");
        if (withPart) sb.Append(" <span class=\"soft\">· ").Append(E(Loc.T(SectionBuilder.TitleKey(f.Component)))).Append("</span>");
        if (f.Key.StartsWith("game.past.", StringComparison.Ordinal)) sb.Append(" <span class=\"tag\">").Append(E(Loc.T("source.nonchc"))).Append("</span>");
        sb.Append("</p>");
        if (f.Detail.Length > 0) sb.Append("<p class=\"soft\">").Append(E(f.Detail)).Append("</p>");
        if (f.HasRecommendation) sb.Append("<p><b>").Append(E(Loc.T("report.todo"))).Append("</b> ").Append(E(f.Recommendation)).Append("</p>");
        if (f.ActionUrl is { } url && url.StartsWith("https://", StringComparison.Ordinal)) sb.Append("<p><a href=\"").Append(E(url)).Append("\">").Append(E(f.ActionText)).Append("</a></p>");
        if (full && f.Steps is { Count: > 0 } steps)
        {
            sb.Append("<ol>");
            foreach (var step in steps) sb.Append("<li>").Append(E(step)).Append("</li>");
            sb.Append("</ol>");
        }
        if (full && f.Why.Length > 0) sb.Append("<p class=\"soft\"><b>").Append(E(Loc.T("report.why"))).Append("</b> ").Append(E(f.Why)).Append("</p>");
        sb.Append("</div>");
    }

    // ---- games

    private static void Games(StringBuilder sb, ReportUsage usage)
    {
        if (usage.Games.Count == 0) return;
        sb.Append("<h2>").Append(E(Loc.T("report.games"))).Append("</h2>");
        sb.Append("<p class=\"soft\">").Append(E(Loc.T("report.games.text"))).Append("</p>");
        Table(sb, null,
            [Loc.T("report.col.game"), Loc.T("report.col.time"), Loc.T("report.col.fps"), Loc.T("report.col.low"), Loc.T("perf.games.tile.limit"), Loc.T("lookback.col.source")],
            usage.Games.Select(GameRow).ToList());
        foreach (var game in usage.Games)
        {
            var items = game.Findings.Where(f => f.Severity != Severity.Good).ToList();
            if (items.Count == 0) continue;
            sb.Append("<h3>").Append(E(game.Title)).Append("</h3>");
            foreach (var f in items) Item(sb, f, withPart: false, full: false);
        }
    }

    private static IReadOnlyList<string> GameRow(GameReport r)
    {
        var nonChc = r.Past is { } past ? PastAnalyzer.BeyondMeasured(past.Days, r.MeasuredMinutes).Sum(d => d.Minutes) : 0;
        var measured = r.Past is null ? r.Usage.Minutes : r.MeasuredMinutes.Sum(d => d.Minutes);
        var source = !r.Measured ? "source.nonchc" : nonChc >= 1 ? "report.source.both" : "source.chc";
        return
        [
            r.Title,
            Fmt.Duration(TimeSpan.FromMinutes(measured + nonChc)),
            r.Usage.Fps is { } fps ? fps.ToString("0", Loc.Instance.Culture) : Fmt.Dash,
            r.Usage.FpsLow is { } low ? low.ToString("0", Loc.Instance.Culture) : Fmt.Dash,
            r.Measured ? Loc.T(r.LimitKey) : Fmt.Dash,
            Loc.T(source),
        ];
    }

    // ---- programs

    private static void Programs(StringBuilder sb, ReportUsage usage)
    {
        if (usage.Programs.Count + usage.NonChcPrograms.Count == 0) return;
        sb.Append("<h2>").Append(E(Loc.T("report.programs", ReportUsage.ProgramDays))).Append("</h2>");
        foreach (var f in usage.ProgramFindings.Where(f => f.Severity != Severity.Good)) Item(sb, f, withPart: false, full: false);

        static string Size(double mb) => Fmt.Bytes((ulong)(mb * 1024 * 1024), mb >= 1024 ? 1 : 0);
        var rows = usage.Programs.Select(p => (IReadOnlyList<string>)
        [
            ProgramAdvisor.NameOf(p),
            Loc.T(ProgramAdvisor.RoleKey(p)),
            Fmt.Percent(p.CpuAverage, 1),
            Size(p.MemoryAverageMb),
            Fmt.Bytes((ulong)(p.ReadBytes + p.WriteBytes), 1),
            Loc.T("source.chc"),
        ]).Concat(usage.NonChcPrograms.Select(r => (IReadOnlyList<string>)
        [
            PastAnalyzer.NameOf(r.Exe),
            Loc.T("report.program.ran", Fmt.Duration(TimeSpan.FromSeconds(r.Running))),
            r.CpuWhileRunning is { } cpu ? Fmt.Percent(cpu, 1) : Fmt.Dash,
            Fmt.Dash,
            r.DiskBytes > 0 ? Fmt.Bytes((ulong)r.DiskBytes, 1) : Fmt.Dash,
            Loc.T("source.nonchc"),
        ])).ToList();
        Table(sb, null, [Loc.T("programs.col.program"), Loc.T("report.col.where"), Loc.T("programs.sort.processor"), Loc.T("programs.sort.memory"), Loc.T("lookback.col.disk"), Loc.T("lookback.col.source")], rows);
    }

    // ---- what is plugged in

    private static void Devices(StringBuilder sb, SystemSnapshot s)
    {
        if (s.Devices.Count == 0) return;
        sb.Append("<h2>").Append(E(Loc.T("report.devices"))).Append("</h2><table><thead><tr><th>").Append(E(Loc.T("col.device"))).Append("</th><th>").Append(E(Loc.T("col.devicekind")))
          .Append("</th><th>").Append(E(Loc.T("col.connection"))).Append("</th><th>").Append(E(Loc.T("report.col.status"))).Append("</th></tr></thead><tbody>");
        foreach (var d in s.Devices.OrderBy(d => d.Kind).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var problem = d.UsbPortStatus != 0 || d.ProblemCode is not (0 or 22);
            var connection = DeviceAdvisor.ConnectionOf(d);
            if (d.BatteryPercent is { } battery) connection += " · " + Loc.T("report.device.battery", Fmt.Percent(battery));
            sb.Append("<tr><td class=\"name\">").Append(E(d.Name.Length > 0 ? d.Name : Loc.T("device.unknown"))).Append("</td><td>").Append(E(Loc.T("device.kind." + d.Kind.ToString().ToLowerInvariant())))
              .Append("</td><td>").Append(E(connection)).Append("</td><td><span class=\"sev ").Append(problem ? "warning" : d.ProblemCode == 22 ? "info" : "good").Append("\">")
              .Append(E(Loc.T(problem ? "device.status.problem" : d.ProblemCode == 22 ? "device.status.disabled" : "device.status.ok"))).Append("</span></td></tr>");
        }
        sb.Append("</tbody></table>");
        if (s.Wifi is { } wifi)
            sb.Append("<p class=\"soft\">").Append(E(Loc.T("report.wifi", Fmt.Percent(wifi.AverageSignal), Fmt.Percent(wifi.LowestSignal), s.DeviceLogs.WifiDrops))).Append("</p>");
    }

    // ---- when it was on, and what changed

    private static void Use(StringBuilder sb, ReportUsage usage)
    {
        if (usage.OnMinutes.Count > 0)
        {
            var on = usage.OnMinutes.Sum(d => d.Minutes);
            var recorded = usage.RecordedMinutes.Sum(d => d.Minutes);
            sb.Append("<h2>").Append(E(Loc.T("report.use"))).Append("</h2><p>")
              .Append(E(Loc.T("report.use.text", ReportUsage.Days, Fmt.Duration(TimeSpan.FromMinutes(on)), usage.OnMinutes.Count, Fmt.Duration(TimeSpan.FromMinutes(Math.Min(recorded, on))))))
              .Append("</p>");
            sb.Append("<div class=\"chart\">").Append(UseChart(usage)).Append("</div><p class=\"legend soft\">").Append(Swatch("#1f6fb8")).Append(E(Loc.T("source.chc.recorded")))
              .Append(Swatch("url(#h)")).Append(E(Loc.T("source.nonchc"))).Append("</p>");
        }
        if (usage.Changes.Count > 0)
        {
            Table(sb, Loc.T("report.changes"), [Loc.T("report.col.date"), Loc.T("report.col.change"), Loc.T("report.col.detail")],
                usage.Changes.Select(c => (IReadOnlyList<string>)[Fmt.Date(c.Time), ChangeTexts.Title(c), ChangeTexts.Detail(c)]).ToList(), firstColumnWhole: true);
        }
    }

    /// <summary>A sample of what a bar in the chart is filled with; the pattern is the chart's own.</summary>
    private static string Swatch(string fill) => $"<svg class=\"k\" viewBox=\"0 0 12 9\"><rect width=\"12\" height=\"9\" fill=\"{fill}\"/></svg>";

    private static string UseChart(ReportUsage usage)
    {
        const double W = 720, H = 124, Left = 26, Right = 2, Top = 6, Bottom = 18, Hours = 24;
        var ci = CultureInfo.InvariantCulture;
        var plotHeight = H - Top - Bottom;
        var dayWidth = (W - Left - Right) / ReportUsage.Days;
        double Y(double hours) => Top + plotHeight * (1 - hours / Hours);
        var on = usage.OnMinutes.ToDictionary(d => d.Date, d => d.Minutes / 60);
        var recorded = usage.RecordedMinutes.ToDictionary(d => d.Date, d => d.Minutes / 60);

        var svg = new StringBuilder($"<svg viewBox=\"0 0 {W.ToString(ci)} {H.ToString(ci)}\" role=\"img\"><defs><pattern id=\"h\" width=\"3.5\" height=\"3.5\" patternUnits=\"userSpaceOnUse\" patternTransform=\"rotate(45)\"><rect width=\"3.5\" height=\"3.5\" fill=\"#f6e7d2\"/><line x1=\"0\" y1=\"0\" x2=\"0\" y2=\"3.5\" stroke=\"#b8741a\" stroke-width=\"1.3\"/></pattern></defs>");
        foreach (var hours in new[] { 0.0, 12, 24 })
        {
            var y = Y(hours).ToString("0.#", ci);
            svg.Append($"<line x1=\"{Left.ToString(ci)}\" y1=\"{y}\" x2=\"{(W - Right).ToString(ci)}\" y2=\"{y}\" stroke=\"#d5d9de\" stroke-width=\".6\"/><text x=\"{(Left - 5).ToString(ci)}\" y=\"{(Y(hours) + 3).ToString("0.#", ci)}\" text-anchor=\"end\" font-size=\"8.5\" fill=\"#59616b\">{hours.ToString("0", ci)} h</text>");
        }
        for (var i = 0; i < ReportUsage.Days; i++)
        {
            var day = usage.FirstDay.AddDays(i);
            var x = Left + i * dayWidth;
            var total = Math.Min(on.GetValueOrDefault(day), Hours);
            var own = Math.Min(recorded.GetValueOrDefault(day), Hours);
            total = Math.Max(total, own);
            var barWidth = (dayWidth * 0.74).ToString("0.##", ci);
            if (own > 0) svg.Append($"<rect x=\"{x.ToString("0.##", ci)}\" y=\"{Y(own).ToString("0.##", ci)}\" width=\"{barWidth}\" height=\"{(Y(0) - Y(own)).ToString("0.##", ci)}\" fill=\"#1f6fb8\"/>");
            if (total > own) svg.Append($"<rect x=\"{x.ToString("0.##", ci)}\" y=\"{Y(total).ToString("0.##", ci)}\" width=\"{barWidth}\" height=\"{(Y(own) - Y(total)).ToString("0.##", ci)}\" fill=\"url(#h)\"/>");
            if (day.DayOfWeek == DayOfWeek.Monday)
                svg.Append($"<text x=\"{x.ToString("0.##", ci)}\" y=\"{(H - 5).ToString(ci)}\" font-size=\"8.5\" fill=\"#59616b\">{E(day.ToString("d MMM", Loc.Instance.Culture))}</text>");
        }
        return svg.Append("</svg>").ToString();
    }

    // ---- the full technical part

    private static void Details(StringBuilder sb, SystemSnapshot s, IReadOnlyList<ComponentSection> sections, ReportOptions options)
    {
        sb.Append("<h2 class=\"page\">").Append(E(Loc.T("report.details"))).Append("</h2>");
        foreach (var c in sections)
        {
            sb.Append("<h3 class=\"part\">").Append(E(c.Title)).Append(" <span class=\"sev ").Append(Css(c.Severity)).Append("\">").Append(E(SeverityName(c.Severity))).Append("</span></h3>");
            sb.Append("<p class=\"soft\">").Append(E(c.Summary)).Append("</p>");
            foreach (var d in c.Devices)
            {
                sb.Append("<h4>").Append(E(d.Title)).Append("</h4>");
                if (d.Subtitle.Length > 0) sb.Append("<p class=\"soft\">").Append(E(d.Subtitle)).Append("</p>");
                var rows = d.Highlights.Select(h => (h.Label, h.Value)).Concat(d.Meters.Select(m => (m.Label, m.Caption))).DistinctBy(r => r.Label, StringComparer.OrdinalIgnoreCase).ToList();
                if (rows.Count > 0)
                {
                    sb.Append("<table class=\"kv\"><tbody>");
                    foreach (var (label, value) in rows) sb.Append("<tr><th>").Append(E(label)).Append("</th><td>").Append(E(value)).Append("</td></tr>");
                    sb.Append("</tbody></table>");
                }
                foreach (var g in d.Specs)
                {
                    sb.Append("<p class=\"group\">").Append(E(g.Title)).Append("</p><table class=\"kv\"><tbody>");
                    foreach (var r in g.Rows) sb.Append("<tr><th>").Append(E(r.Key)).Append("</th><td>").Append(E(r.Value)).Append("</td></tr>");
                    sb.Append("</tbody></table>");
                }
                foreach (var t in d.Tables) Table(sb, t.Title, t.Columns, t.Rows);
                if (!options.IncludeSensors) continue;
                var sensors = d.SensorDeviceIds.Select(s.Sensors.Device).OfType<Sensors.SensorDevice>().SelectMany(x => x.Sensors).ToList();
                if (sensors.Count > 0)
                {
                    Table(sb, Loc.T("report.sensors"), [Loc.T("col.sensor"), Loc.T("col.type"), Loc.T("col.value"), Loc.T("col.min"), Loc.T("col.max")],
                        sensors.Select(x => (IReadOnlyList<string>)[x.Name, Loc.T("sensortype." + x.Type), Fmt.Sensor(x.Type, x.Value), Fmt.Sensor(x.Type, x.Min), Fmt.Sensor(x.Type, x.Max)]).ToList());
                }
            }
        }
    }

    private static void Table(StringBuilder sb, string? title, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows, bool firstColumnWhole = false)
    {
        if (title is not null) sb.Append("<p class=\"group\">").Append(E(title)).Append("</p>");
        sb.Append("<table><thead><tr>");
        foreach (var c in columns) sb.Append("<th>").Append(E(c)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            sb.Append("<tr>");
            // A short word like an error code stays in one piece; only long ids may break anywhere.
            for (var i = 0; i < row.Count; i++)
                sb.Append((firstColumnWhole && i == 0) || (row[i].Length <= ShortWord && !row[i].Contains(' ')) ? "<td class=\"whole\">" : "<td>").Append(E(row[i])).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table>");
    }

    private static string Status(Severity severity) => Badge(severity, severity);

    /// <summary>A label in the colour of one severity, named after another: an urgent update looks urgent and still says "Update".</summary>
    private static string Badge(Severity colour, Severity name) =>
        $"<span class=\"sev {Css(colour)}\">{(name == Severity.Update ? UpdateIcon : "")}{E(SeverityName(name))}</span>";

    private const string UpdateIcon = "<svg class=\"update-icon\" viewBox=\"0 0 16 16\" width=\"9.5pt\" height=\"9.5pt\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.7\" stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M8 2v8M4.5 7 8 10.5 11.5 7M3 13.5h10\"/></svg>";

    /// <summary>Short plain-text summary for pasting into forums or chats.</summary>
    public static string Text(SystemSnapshot s, HealthReport health, ReportOptions options)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"== {Loc.T("report.title")} — CHC {s.AppVersion} ==");
        sb.AppendLine(Fmt.DateAndTime(s.TakenAt));
        sb.AppendLine();
        sb.AppendLine($"{Loc.T("report.score")}: {health.Score}/100 ({Loc.T(health.RatingKey)})");
        foreach (var (k, v) in SystemSummary(s)) sb.AppendLine($"{k}: {v}");
        sb.AppendLine();
        foreach (var c in health.Components)
            sb.AppendLine($"[{SeverityName(c.Severity)}] {Loc.T(SectionBuilder.TitleKey(c.Kind))} — {(c.HasData ? $"{c.Score}/100" : Fmt.Dash)}");
        if (health.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.T("report.issues") + ":");
            foreach (var f in health.Issues)
                sb.AppendLine($" - [{SeverityName(f.Severity)}] {Loc.T(SectionBuilder.TitleKey(f.Component))}: {f.Title}");
        }
        if (health.Updates.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.T("report.updates") + ":");
            foreach (var f in health.Updates)
                sb.AppendLine($" - {Loc.T(SectionBuilder.TitleKey(f.Component))}: {f.Title}");
        }
        if (options.IncludeUsage && s.Updates.Runtimes.Count + s.Updates.Programs.Count > 0)
            sb.AppendLine(Loc.T("report.updates.suggested.count", s.Updates.Runtimes.Count + s.Updates.Programs.Count));
        return sb.ToString();
    }

    public static string Json(SystemSnapshot s, HealthReport health, ReportOptions options)
    {
        var json = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Converters = { new JsonStringEnumConverter() },
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        var root = new JsonObject
        {
            ["generator"] = $"CHC {s.AppVersion}",
            ["language"] = Loc.Instance.CurrentLanguage,
            ["health"] = new JsonObject
            {
                ["score"] = health.Score,
                ["rating"] = Loc.T(health.RatingKey),
                ["components"] = new JsonArray(health.Components.Select(c => (JsonNode)new JsonObject
                {
                    ["component"] = c.Kind.ToString(),
                    ["score"] = c.HasData ? c.Score : null,
                    ["severity"] = c.Severity.ToString(),
                    ["findings"] = new JsonArray(c.Findings.Select(f => (JsonNode)new JsonObject
                    {
                        ["severity"] = f.Severity.ToString(),
                        ["isUpdate"] = f.IsUpdate,
                        ["id"] = f.Key,
                        ["title"] = f.Title,
                        ["detail"] = f.Detail,
                        ["recommendation"] = f.Recommendation,
                        ["steps"] = new System.Text.Json.Nodes.JsonArray(f.Steps.Select(x => (System.Text.Json.Nodes.JsonNode?)x).ToArray()),
                        ["why"] = f.Why,
                    }).ToArray()),
                }).ToArray()),
            },
            ["snapshot"] = JsonSerializer.SerializeToNode(s, json),
            ["updates"] = JsonSerializer.SerializeToNode(new
            {
                s.Updates.CheckedAt,
                Items = options.IncludeUsage ? s.Updates.Items : s.Updates.Items.Where(i => i.Kind < Updates.UpdateKind.Runtime).ToList(),
            }, json),
        };
        if (!options.IncludeSensors) root["snapshot"]!.AsObject().Remove("Sensors");
        if (!options.IncludeSerials) MaskSerials(root);
        return root.ToJsonString(json);
    }

    private static void MaskSerials(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (key == "SerialNumber" && obj[key] is JsonValue v && v.TryGetValue<string>(out var serial))
                        obj[key] = Fmt.Mask(serial);
                    else if (key is "PnpDeviceId" or "DeviceId" && obj[key] is JsonValue d && d.TryGetValue<string>(out var id))
                        obj[key] = Fmt.MaskInstance(id, Fmt.Mask);
                    else
                        MaskSerials(obj[key]);
                }
                break;
            case JsonArray arr:
                foreach (var item in arr) MaskSerials(item);
                break;
        }
    }

    public static IEnumerable<(string Key, string Value)> SystemSummary(SystemSnapshot s)
    {
        var board = $"{s.Board.SystemManufacturer} {s.Board.SystemModel}".Trim();
        if (board.Length == 0 || board.Contains("To Be Filled", StringComparison.OrdinalIgnoreCase) || board.Contains("System Product Name", StringComparison.OrdinalIgnoreCase))
            board = $"{s.Board.Manufacturer} {s.Board.Product}".Trim();
        yield return (Loc.T("summary.system"), Fmt.Or(board));
        yield return (Loc.T("summary.cpu"), Fmt.Or(s.Cpu.Name));
        var type = s.Memory.Modules.Select(m => m.Type).FirstOrDefault(t => t.Length > 0) ?? "";
        yield return (Loc.T("summary.memory"), $"{Fmt.Bytes(s.Memory.TotalBytes, 0)} {type}".Trim());
        yield return (Loc.T("summary.gpu"), Fmt.Or(string.Join(", ", s.Gpus.Select(g => g.Name))));
        yield return (Loc.T("summary.storage"), Fmt.Or(string.Join(", ", s.Disks.Select(d => $"{d.Model} ({Fmt.DiskSize(d.SizeBytes)})"))));
        yield return (Loc.T("summary.os"), Fmt.Or($"{s.Os.Name} {s.Os.DisplayVersion}".Trim()));
    }

    public static string SeverityName(Severity severity) => Loc.T("severity." + severity.ToString().ToLowerInvariant());

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Css(Severity s) => s.ToString().ToLowerInvariant();

    private const string LogoSvg =
        "<svg class=\"logo\" viewBox=\"0 0 256 256\" width=\"46\" height=\"46\"><circle cx=\"128\" cy=\"128\" r=\"83\" fill=\"#fff\"/><path d=\"M60.1 195.9A96 96 0 1 1 195.9 195.9\" fill=\"none\" stroke=\"#2B8FE0\" stroke-width=\"30\" stroke-linecap=\"round\"/>" +
        "<path d=\"M86 134L115 163L172 104\" fill=\"none\" stroke=\"#2EB86A\" stroke-width=\"28\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

    /// <summary>A document, not a dashboard: black on white, thin rules, one type family, and colour only where it says something.</summary>
    private const string Stylesheet =
        """
        :root{--text:#1b1e23;--soft:#59616b;--rule:#c9ced4;--faint:#e9ebee;--good:#1b7f45;--info:#1f6fb8;--update:#6a4bc4;--warning:#a8590b;--critical:#b42318}
        *{box-sizing:border-box}
        html{background:#fff;color:var(--text);font:10pt/1.45 "Segoe UI",system-ui,-apple-system,sans-serif;-webkit-print-color-adjust:exact;print-color-adjust:exact}
        body{margin:0}main{max-width:760px;margin:0 auto;padding:32px 20px 48px}
        h1{font-size:18pt;font-weight:600;margin:0;line-height:1.2}
        h2{font-size:12.5pt;font-weight:600;margin:20pt 0 6pt;padding-bottom:3pt;border-bottom:1pt solid var(--text);break-after:avoid}
        h3{font-size:10.5pt;font-weight:600;margin:13pt 0 3pt;break-after:avoid}
        h3.part{margin-top:18pt;padding-bottom:2pt;border-bottom:.5pt solid var(--rule)}
        h3 .sev{font-size:9pt;margin-left:4pt}
        h4{font-size:10pt;font-weight:600;margin:10pt 0 2pt;break-after:avoid}
        p{margin:2pt 0}.soft{color:var(--soft)}
        header{display:flex;justify-content:space-between;align-items:flex-start;gap:16pt;padding-bottom:9pt;border-bottom:1pt solid var(--text)}
        .id{display:flex;gap:10pt;align-items:center}.logo{flex:none}.when{text-align:right;font-size:9pt;white-space:nowrap;margin:0}
        .verdict{display:flex;align-items:baseline;gap:7pt;margin:15pt 0 2pt}.verdict .n{font-size:34pt;font-weight:600;line-height:1}.verdict .of{font-size:12pt;color:var(--soft)}
        .rating{font-size:15pt;font-weight:600;margin-left:9pt}.rating.good{color:var(--good)}.rating.info{color:var(--info)}.rating.warning{color:var(--warning)}.rating.critical{color:var(--critical)}
        .lead{font-size:11.5pt;margin-top:3pt}
        ol{margin:3pt 0;padding-left:16pt}li{margin:1.5pt 0}.first li{margin:5pt 0;break-inside:avoid}
        table{border-collapse:collapse;width:100%;font-size:9pt;margin:3pt 0 6pt}
        th{text-align:left;font-weight:600;color:var(--soft);padding:2.5pt 8pt 2.5pt 0;border-bottom:.75pt solid var(--rule);vertical-align:bottom}
        td{padding:2.5pt 8pt 2.5pt 0;border-bottom:.5pt solid var(--faint);vertical-align:top;overflow-wrap:anywhere}
        th:last-child,td:last-child{padding-right:0}tr{break-inside:avoid}thead{display:table-header-group}
        .whole{white-space:nowrap}.num{text-align:right;white-space:nowrap;font-variant-numeric:tabular-nums}.name{font-weight:500}
        .kv th{width:34%;font-weight:400;color:var(--soft);border-bottom:.5pt solid var(--faint);vertical-align:top}
        .group{font-weight:600;font-size:9pt;color:var(--soft);margin:8pt 0 0;break-after:avoid}
        .sev{font-weight:600;white-space:nowrap}.sev.good{color:var(--good)}.sev.info{color:var(--info)}.sev.update{color:var(--update)}.update-icon{vertical-align:-1.2pt;margin-right:2pt}.sev.warning{color:var(--warning)}.sev.critical{color:var(--critical)}
        .item{margin:8pt 0 9pt;break-inside:avoid}.item .head{margin-bottom:1pt}
        .tag{font-size:8.5pt;color:var(--soft);white-space:nowrap;border-bottom:.75pt dashed var(--soft)}
        .chart svg{display:block;width:100%;height:auto;margin-top:5pt}.legend{font-size:8.5pt;margin-top:1pt}
        .k{display:inline-block;width:12pt;height:9pt;margin:0 4pt 0 0;vertical-align:-1.5pt}.k+.k{margin-left:12pt}.legend .k:not(:first-child){margin-left:12pt}
        .end{margin-top:22pt;padding-top:5pt;border-top:.5pt solid var(--rule);font-size:8.5pt}
        @media print{main{max-width:none;padding:0}h2.page{break-before:page;margin-top:0}.end{break-inside:avoid}}
        """;
}
