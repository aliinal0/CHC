using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;

namespace CHC.Core.Reporting;

/// <param name="ForPrint">Light colours, expanded tables and page-break rules for the PDF export.</param>
public sealed record ReportOptions(bool IncludeSerials = false, bool IncludeSensors = true, bool ForPrint = false);

/// <summary>Exports a scan as a self-contained HTML page (also the template for the PDF report), JSON or text.</summary>
public static class ReportWriter
{
    public static string Html(SystemSnapshot s, HealthReport health, ReportOptions options)
    {
        var sections = SectionBuilder.Build(s, health, options.IncludeSerials);
        var sb = new StringBuilder(64 * 1024);
        var lang = Loc.Instance.CurrentLanguage;

        sb.Append($"<!DOCTYPE html><html lang=\"{lang}\"{(options.ForPrint ? " class=\"print\"" : "")}><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.Append($"<title>{E(Loc.T("report.title"))} — {E(Fmt.DateAndTime(s.TakenAt))}</title><style>{Stylesheet}</style></head><body><main>");

        // Header
        sb.Append("<header class=\"top\"><div class=\"brand\">").Append(LogoSvg).Append("<div><h1>").Append(E(Loc.T("report.title"))).Append("</h1>");
        sb.Append("<p class=\"muted\">").Append(E(Loc.T("report.generated", Fmt.DateAndTime(s.TakenAt), s.AppVersion))).Append("</p></div></div></header>");

        // Overview
        var sevClass = Css(health.Severity);
        sb.Append("<section class=\"card overview\"><div class=\"score ").Append(sevClass).Append("\">").Append(ScoreSvg(health.Score)).Append("<div class=\"score-text\"><b>")
          .Append(health.Score).Append("</b><span>").Append(E(Loc.T(health.RatingKey))).Append("</span></div></div><div class=\"ov-body\">");
        sb.Append("<h2>").Append(E(Loc.T("report.overview"))).Append("</h2><p>").Append(E(Loc.T("report.counts", health.CriticalCount, health.WarningCount, health.InfoCount))).Append("</p>");
        sb.Append("<table class=\"kv\">");
        foreach (var (k, v) in SystemSummary(s)) sb.Append("<tr><th>").Append(E(k)).Append("</th><td>").Append(E(v)).Append("</td></tr>");
        sb.Append("</table>");
        if (!s.IsElevated) sb.Append("<p class=\"note\">").Append(E(Loc.T("report.notelevated"))).Append("</p>");
        sb.Append("</div></section>");

        // Component grid
        sb.Append("<section class=\"grid\">");
        foreach (var c in sections)
        {
            sb.Append("<a class=\"card mini\" href=\"#").Append(c.Kind).Append("\"><span class=\"dot ").Append(Css(c.Severity)).Append("\"></span><div><b>").Append(E(c.Title))
              .Append("</b><span class=\"muted\">").Append(E(c.Devices.FirstOrDefault()?.Title ?? "")).Append("</span></div><span class=\"pill ").Append(Css(c.Severity)).Append("\">")
              .Append(E(SeverityName(c.Severity))).Append("</span></a>");
        }
        sb.Append("</section>");

        // Issues
        sb.Append("<section class=\"card\"><h2>").Append(E(Loc.T("report.issues"))).Append("</h2>");
        if (health.Issues.Count == 0) sb.Append("<p class=\"muted\">").Append(E(Loc.T("dashboard.noissues"))).Append("</p>");
        else AppendFindings(sb, health.Issues, showComponent: true);
        sb.Append("</section>");

        // Details
        sb.Append("<h2 class=\"section-title\">").Append(E(Loc.T("report.details"))).Append("</h2>");
        foreach (var c in sections)
        {
            sb.Append("<section class=\"card component\" id=\"").Append(c.Kind).Append("\"><div class=\"comp-head\"><h2>").Append(E(c.Title)).Append("</h2><span class=\"pill ")
              .Append(Css(c.Severity)).Append("\">").Append(E(SeverityName(c.Severity))).Append(" · ").Append(c.Score).Append("/100</span></div>");
            sb.Append("<p class=\"muted\">").Append(E(c.Summary)).Append("</p>");
            AppendFindings(sb, c.Findings, showComponent: false);

            foreach (var d in c.Devices)
            {
                sb.Append("<div class=\"device\"><h3>").Append(E(d.Title)).Append("</h3>");
                if (d.Subtitle.Length > 0) sb.Append("<p class=\"muted\">").Append(E(d.Subtitle)).Append("</p>");
                if (d.Highlights.Count > 0)
                {
                    sb.Append("<div class=\"tiles\">");
                    foreach (var h in d.Highlights) sb.Append("<div class=\"tile\"><span>").Append(E(h.Label)).Append("</span><b>").Append(E(h.Value)).Append("</b></div>");
                    sb.Append("</div>");
                }
                foreach (var m in d.Meters)
                {
                    var pct = Math.Clamp(m.Percent, 0, 100).ToString("0.#", CultureInfo.InvariantCulture);
                    sb.Append("<div class=\"meter\"><div class=\"meter-label\"><span>").Append(E(m.Label)).Append("</span><span class=\"muted\">").Append(E(m.Caption))
                      .Append("</span></div><div class=\"bar\"><i class=\"").Append(Css(m.Severity)).Append("\" style=\"width:").Append(pct).Append("%\"></i></div></div>");
                }
                if (d.Specs.Count > 0)
                {
                    sb.Append("<div class=\"specs\">");
                    foreach (var g in d.Specs)
                    {
                        sb.Append("<table class=\"kv\"><caption>").Append(E(g.Title)).Append("</caption>");
                        foreach (var r in g.Rows) sb.Append("<tr><th>").Append(E(r.Key)).Append("</th><td>").Append(E(r.Value)).Append("</td></tr>");
                        sb.Append("</table>");
                    }
                    sb.Append("</div>");
                }
                foreach (var t in d.Tables) AppendTable(sb, t);

                if (options.IncludeSensors)
                {
                    var sensors = d.SensorDeviceIds.Select(s.Sensors.Device).OfType<Sensors.SensorDevice>().SelectMany(x => x.Sensors).ToList();
                    if (sensors.Count > 0)
                    {
                        AppendTable(sb, new TableBlock(Loc.T("report.sensors"),
                            [Loc.T("col.sensor"), Loc.T("col.type"), Loc.T("col.value"), Loc.T("col.min"), Loc.T("col.max")],
                            sensors.Select(x => (IReadOnlyList<string>)[x.Name, Loc.T("sensortype." + x.Type), Fmt.Sensor(x.Type, x.Value), Fmt.Sensor(x.Type, x.Min), Fmt.Sensor(x.Type, x.Max)]).ToList()),
                            collapsed: !options.ForPrint);
                    }
                }
                sb.Append("</div>");
            }
            sb.Append("</section>");
        }

        sb.Append("<footer class=\"muted\">").Append(E(Loc.T("report.footer", s.AppVersion))).Append("</footer></main></body></html>");
        return sb.ToString();
    }

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
            sb.AppendLine($"[{SeverityName(c.Severity)}] {Loc.T(SectionBuilder.TitleKey(c.Kind))} — {c.Score}/100");
        if (health.Issues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Loc.T("report.issues") + ":");
            foreach (var f in health.Issues)
                sb.AppendLine($" - [{SeverityName(f.Severity)}] {Loc.T(SectionBuilder.TitleKey(f.Component))}: {f.Title}");
        }
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
                    ["score"] = c.Score,
                    ["severity"] = c.Severity.ToString(),
                    ["findings"] = new JsonArray(c.Findings.Select(f => (JsonNode)new JsonObject
                    {
                        ["severity"] = f.Severity.ToString(),
                        ["id"] = f.Key,
                        ["title"] = f.Title,
                        ["detail"] = f.Detail,
                        ["recommendation"] = f.Recommendation,
                    }).ToArray()),
                }).ToArray()),
            },
            ["snapshot"] = JsonSerializer.SerializeToNode(s, json),
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

    private static void AppendFindings(StringBuilder sb, IEnumerable<Finding> findings, bool showComponent)
    {
        sb.Append("<ul class=\"findings\">");
        foreach (var f in findings)
        {
            sb.Append("<li class=\"").Append(Css(f.Severity)).Append("\"><span class=\"pill ").Append(Css(f.Severity)).Append("\">").Append(E(SeverityName(f.Severity))).Append("</span><div><b>");
            if (showComponent) sb.Append(E(Loc.T(SectionBuilder.TitleKey(f.Component)))).Append(": ");
            sb.Append(E(f.Title)).Append("</b>");
            if (f.Detail.Length > 0) sb.Append("<p>").Append(E(f.Detail)).Append("</p>");
            if (f.HasRecommendation) sb.Append("<p class=\"fix\">→ ").Append(E(f.Recommendation)).Append("</p>");
            sb.Append("</div></li>");
        }
        sb.Append("</ul>");
    }

    private static void AppendTable(StringBuilder sb, TableBlock t, bool collapsed = false)
    {
        sb.Append(collapsed ? "<details><summary>" : "<div class=\"table\"><h4>").Append(E(t.Title)).Append(collapsed ? "</summary>" : "</h4>");
        sb.Append("<div class=\"scroll\"><table class=\"grid-table\"><thead><tr>");
        foreach (var c in t.Columns) sb.Append("<th>").Append(E(c)).Append("</th>");
        sb.Append("</tr></thead><tbody>");
        foreach (var row in t.Rows)
        {
            sb.Append("<tr>");
            foreach (var cell in row) sb.Append("<td>").Append(E(cell)).Append("</td>");
            sb.Append("</tr>");
        }
        sb.Append("</tbody></table></div>").Append(collapsed ? "</details>" : "</div>");
    }

    private static string E(string? s) => WebUtility.HtmlEncode(s ?? "");

    private static string Css(Severity s) => s.ToString().ToLowerInvariant();

    private static string ScoreSvg(int score)
    {
        const double r = 52, c = 2 * Math.PI * r;
        var dash = (c * Math.Clamp(score, 0, 100) / 100).ToString("0.##", CultureInfo.InvariantCulture);
        var full = c.ToString("0.##", CultureInfo.InvariantCulture);
        return $"<svg viewBox=\"0 0 120 120\" width=\"132\" height=\"132\"><circle cx=\"60\" cy=\"60\" r=\"52\" class=\"track\"/><circle cx=\"60\" cy=\"60\" r=\"52\" class=\"value\" stroke-dasharray=\"{dash} {full}\" transform=\"rotate(-90 60 60)\"/></svg>";
    }

    private const string LogoSvg =
        "<svg class=\"logo\" viewBox=\"0 0 64 64\" width=\"48\" height=\"48\"><defs><linearGradient id=\"g\" x1=\"0\" y1=\"0\" x2=\"1\" y2=\"1\"><stop offset=\"0\" stop-color=\"#22d3ee\"/><stop offset=\"1\" stop-color=\"#6366f1\"/></linearGradient></defs>" +
        "<rect x=\"2\" y=\"2\" width=\"60\" height=\"60\" rx=\"16\" fill=\"url(#g)\"/><path d=\"M10 34h11l5-12 8 22 6-14 3 4h11\" fill=\"none\" stroke=\"#fff\" stroke-width=\"5\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";

    private const string Stylesheet =
        """
        :root{--bg:#f4f6fa;--card:#fff;--text:#111827;--muted:#6b7280;--line:#e5e7eb;--good:#16a34a;--info:#2563eb;--warning:#d97706;--critical:#dc2626;--track:#e5e7eb}
        @media (prefers-color-scheme:dark){:root{--bg:#0f1115;--card:#181b21;--text:#e6e8ec;--muted:#9aa1ad;--line:#2a2f38;--good:#3fb950;--info:#58a6ff;--warning:#e3a008;--critical:#f85149;--track:#2a2f38}}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 "Segoe UI",system-ui,sans-serif}
        main{max-width:1100px;margin:0 auto;padding:24px 16px 48px}h1{margin:0;font-size:24px}h2{margin:0 0 8px;font-size:18px}h3{margin:0;font-size:16px}h4{margin:16px 0 6px;font-size:13px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}
        .muted{color:var(--muted)}.top{margin-bottom:16px}.brand{display:flex;gap:14px;align-items:center}.brand p{margin:2px 0 0}
        .card{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:18px 20px;margin-bottom:14px}
        .overview{display:flex;gap:24px;align-items:center;flex-wrap:wrap}.ov-body{flex:1;min-width:260px}
        .score{position:relative;width:132px;height:132px}.score svg circle{fill:none;stroke-width:10}.score .track{stroke:var(--track)}.score .value{stroke-linecap:round}
        .score.good .value{stroke:var(--good)}.score.info .value{stroke:var(--info)}.score.warning .value{stroke:var(--warning)}.score.critical .value{stroke:var(--critical)}
        .score-text{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center}.score-text b{font-size:34px;line-height:1}.score-text span{color:var(--muted);font-size:12px}
        .kv{border-collapse:collapse;width:100%}.kv caption{text-align:left;font-weight:600;padding:6px 0;color:var(--muted);font-size:12px;text-transform:uppercase;letter-spacing:.04em}
        .kv th{text-align:left;font-weight:500;color:var(--muted);padding:4px 12px 4px 0;width:40%;vertical-align:top}.kv td{padding:4px 0;word-break:break-word;vertical-align:top}.kv tr+tr{border-top:1px solid var(--line)}
        .note{background:color-mix(in srgb,var(--info) 12%,transparent);border-radius:8px;padding:8px 12px}
        .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(240px,1fr));gap:10px;margin-bottom:14px}
        .mini{display:flex;gap:10px;align-items:center;margin:0;text-decoration:none;color:inherit}.mini div{flex:1;min-width:0;display:flex;flex-direction:column}.mini .muted{font-size:12px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
        .dot{width:10px;height:10px;border-radius:50%;flex:none}.dot.good{background:var(--good)}.dot.info{background:var(--info)}.dot.warning{background:var(--warning)}.dot.critical{background:var(--critical)}
        .pill{font-size:11px;font-weight:600;padding:2px 8px;border-radius:999px;white-space:nowrap;flex:none}
        .pill.good{color:var(--good);background:color-mix(in srgb,var(--good) 14%,transparent)}.pill.info{color:var(--info);background:color-mix(in srgb,var(--info) 14%,transparent)}
        .pill.warning{color:var(--warning);background:color-mix(in srgb,var(--warning) 16%,transparent)}.pill.critical{color:var(--critical);background:color-mix(in srgb,var(--critical) 14%,transparent)}
        .findings{list-style:none;margin:8px 0 0;padding:0}.findings li{display:flex;gap:10px;align-items:flex-start;padding:10px 0;border-top:1px solid var(--line)}.findings li:first-child{border-top:0}
        .findings p{margin:2px 0 0;color:var(--muted)}.findings .fix{color:var(--text)}
        .section-title{margin:26px 0 10px}.comp-head{display:flex;justify-content:space-between;align-items:center;gap:10px}
        .device{border-top:1px solid var(--line);margin-top:14px;padding-top:14px}.device>p{margin:2px 0 10px}
        .tiles{display:grid;grid-template-columns:repeat(auto-fill,minmax(150px,1fr));gap:8px;margin:8px 0 12px}
        .tile{border:1px solid var(--line);border-radius:10px;padding:10px 12px}.tile span{display:block;color:var(--muted);font-size:12px}.tile b{font-size:18px}
        .meter{margin:8px 0}.meter-label{display:flex;justify-content:space-between;gap:10px;font-size:13px}.bar{height:8px;border-radius:99px;background:var(--track);overflow:hidden;margin-top:4px}
        .bar i{display:block;height:100%;border-radius:99px}.bar i.good{background:var(--good)}.bar i.info{background:var(--info)}.bar i.warning{background:var(--warning)}.bar i.critical{background:var(--critical)}
        .specs{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:4px 24px;margin-top:8px;align-items:start}
        .scroll{overflow-x:auto}.grid-table{border-collapse:collapse;width:100%;font-size:12.5px}.grid-table th{text-align:left;color:var(--muted);font-weight:600;border-bottom:1px solid var(--line);padding:6px 8px;white-space:nowrap}
        .grid-table td{padding:5px 8px;border-bottom:1px solid var(--line)}details{margin-top:12px}summary{cursor:pointer;color:var(--muted);font-weight:600}
        footer{text-align:center;margin-top:24px;font-size:12px}
        :root.print{color-scheme:light;--bg:#fff;--card:#fff;--text:#111827;--muted:#5b6472;--line:#e3e6eb;--good:#16a34a;--info:#2563eb;--warning:#c27803;--critical:#dc2626;--track:#e8eaee}
        @page{size:A4;margin:12mm 10mm}
        @media print{body{background:#fff;font-size:12.5px;-webkit-print-color-adjust:exact;print-color-adjust:exact}main{max-width:none;padding:0}
        .card{border-color:var(--line)}.overview,.mini,.tile,.meter,.findings li,.kv tr,.grid-table tr,.comp-head{break-inside:avoid}
        h2,h3,h4,summary{break-after:avoid}.section-title{break-before:page}.component{break-before:page}.section-title+.component{break-before:auto}.grid{grid-template-columns:repeat(3,1fr)}a{color:inherit;text-decoration:none}}
        """;
}
