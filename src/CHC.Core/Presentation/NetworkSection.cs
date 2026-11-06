using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Network;

namespace CHC.Core.Presentation;

/// <summary>Display model for the network page and the report section.</summary>
public static class NetworkSection
{
    public static ComponentSection Build(SystemSnapshot s, NetSession? session)
    {
        var findings = NetworkAnalyzer.Analyze(s, session);
        var health = new ComponentHealth(ComponentKind.Network, findings, session is not null);
        var section = new ComponentSection
        {
            Kind = ComponentKind.Network,
            Title = Loc.T("component.network"),
            Summary = session is null ? Loc.T("net.summary.none") : Loc.T("summary." + health.Severity.ToString().ToLowerInvariant()),
            Severity = health.Severity,
            Score = health.Score,
            Findings = findings.OrderByDescending(f => f.Severity).ToList(),
        };
        if (session is not null) section.Devices.Add(SessionDevice(session, Loc.T("net.session.title")));
        if (s.Network.Physical is { } adapter) section.Devices.Add(Connection(s.Network, adapter));
        foreach (var device in section.Devices) device.Specs.RemoveAll(g => g.Rows.Count == 0);
        return section;
    }

    /// <summary>The results of a test: also used for the connection measured during an online game.</summary>
    public static DeviceSection SessionDevice(NetSession n, string title)
    {
        var internet = n.InternetStats;
        var gateway = n.GatewayStats;
        var server = n.ServerStats;
        var d = new DeviceSection
        {
            Title = title,
            Subtitle = Loc.T("net.session.subtitle", Fmt.DateAndTime(n.Started), Fmt.Elapsed(n.Duration)),
        };
        d.Highlights.Add(new(Loc.T("label.ping"), internet.HasData ? Fmt.Ms(internet.Median) : Fmt.Dash));
        d.Highlights.Add(new(Loc.T("label.jitter"), internet.HasData ? Fmt.Ms(internet.Jitter) : Fmt.Dash));
        d.Highlights.Add(new(Loc.T("label.packetloss"), Fmt.Percent(internet.LossPercent, 1)));
        d.Highlights.Add(server.HasData
            ? new(Loc.T("label.serverping"), Fmt.Ms(server.Median))
            : new(Loc.T("label.routerping"), gateway.HasData ? Fmt.Ms(gateway.Median) : Fmt.Dash));

        if (server.Sent > 0) d.Specs.Add(Stats(Loc.T("net.group.server", n.ServerAddress), server));
        d.Specs.Add(Stats(Loc.T("net.group.internet", n.InternetTarget), internet));
        if (gateway.Sent > 0) d.Specs.Add(Stats(Loc.T("net.group.router", Fmt.Or(n.GatewayAddress)), gateway));
        var secondary = n.SecondaryStats;
        var dns = n.DnsStats;
        var peakDown = n.Samples.Select(x => x.DownMbps).DefaultIfEmpty(0).Max();
        var peakUp = n.Samples.Select(x => x.UpMbps).DefaultIfEmpty(0).Max();
        d.Specs.Add(Group(Loc.T("net.group.other"),
            Row("net.spec.secondary", secondary.Sent > 0 ? Loc.T("net.spec.secondary.value", n.SecondaryTarget, Fmt.Ms(secondary.Median), Fmt.Percent(secondary.LossPercent, 1)) : ""),
            Row("net.spec.dns", dns.HasData ? Fmt.Ms(dns.Median) : ""),
            Row("net.spec.dnsfailed", dns.Sent > 0 ? $"{dns.Lost} / {dns.Sent}" : ""),
            Row("net.spec.downloaded", Fmt.Bytes(n.BytesDown)),
            Row("net.spec.uploaded", Fmt.Bytes(n.BytesUp)),
            Row("net.spec.peakdown", Mbps(peakDown)),
            Row("net.spec.peakup", Mbps(peakUp))));

        if (n.Programs.Count > 0)
        {
            d.Tables.Add(new TableBlock(Loc.T("net.table.programs"),
                [Loc.T("col.program"), Loc.T("net.col.down"), Loc.T("net.col.up")],
                n.Programs.Select(p => (IReadOnlyList<string>)[p.Name, Fmt.Bytes(p.BytesDown), Fmt.Bytes(p.BytesUp)]).ToList()));
        }
        return d;
    }

    private static SpecGroup Stats(string title, NetStats s) => Group(title,
        Row("net.spec.median", s.HasData ? Fmt.Ms(s.Median) : ""),
        Row("net.spec.average", s.HasData ? Fmt.Ms(s.Average) : ""),
        Row("net.spec.minmax", s.HasData ? $"{Fmt.Ms(s.Min)} / {Fmt.Ms(s.Max)}" : ""),
        Row("net.spec.p95", s.HasData ? Fmt.Ms(s.P95) : ""),
        Row("label.jitter", s.HasData ? Fmt.Ms(s.Jitter) : ""),
        Row("label.packetloss", s.Sent > 0 ? $"{Fmt.Percent(s.LossPercent, 1)} ({s.Lost} / {s.Sent})" : ""),
        Row("net.spec.spikes", s.HasData ? s.Spikes.ToString() : ""));

    private static DeviceSection Connection(NetworkInfo net, NetAdapterInfo a)
    {
        var type = Loc.T(a.IsWireless ? "net.type.wifi" : "net.type.wired");
        var d = new DeviceSection
        {
            Title = Fmt.Or(a.Description),
            Subtitle = string.Join(" · ", new[] { type, Fmt.LinkSpeed(a.LinkMbps), a.BandGHz is { } ghz ? Band(ghz) : "" }.Where(x => x.Length > 0 && x != Fmt.Dash)),
        };
        d.Highlights.Add(new(Loc.T("label.connection"), type));
        d.Highlights.Add(new(Loc.T("label.linkspeed"), Fmt.LinkSpeed(a.LinkMbps)));
        if (a.IsWireless)
        {
            d.Highlights.Add(new(Loc.T("label.signal"), Fmt.Percent(a.SignalPercent)));
            d.Highlights.Add(new(Loc.T("label.band"), a.BandGHz is { } band ? Band(band) : Fmt.Dash));
        }
        else
        {
            d.Highlights.Add(new(Loc.T("label.duplex"), a.FullDuplex switch { true => Loc.T("net.duplex.full"), false => Loc.T("net.duplex.half"), _ => Fmt.Dash }));
            d.Highlights.Add(new(Loc.T("label.vpn"), Fmt.YesNo(net.ViaVpn)));
        }

        d.Specs.Add(Group(Loc.T("net.group.adapter"),
            Row("spec.name", a.Name),
            Row("spec.model", a.Description),
            Row("net.spec.type", type),
            Row("net.spec.linkspeed", Fmt.LinkSpeed(a.LinkMbps)),
            Row("net.spec.maxspeed", a.MaxMbps is { } max ? Fmt.LinkSpeed(max) : ""),
            Row("net.spec.duplex", a.FullDuplex switch { true => Loc.T("net.duplex.full"), false => Loc.T("net.duplex.half"), _ => "" }),
            Row("net.spec.powersaving", a.IsWireless ? "" : Fmt.OnOff(a.PowerSaving)),
            Row("spec.driverversion", a.DriverVersion),
            Row("spec.driverdate", a.DriverDate is { } date ? Fmt.Date(date) : "")));
        if (a.IsWireless)
        {
            d.Specs.Add(Group(Loc.T("net.group.wifi"),
                Row("label.signal", a.SignalPercent is { } signal ? Fmt.Percent(signal) : ""),
                Row("net.spec.rssi", a.RssiDbm is { } rssi ? $"{rssi} dBm" : ""),
                Row("label.band", a.BandGHz is { } wifiBand ? Band(wifiBand) : ""),
                Row("net.spec.channel", a.Channel?.ToString()),
                Row("net.spec.standard", a.WifiStandard),
                Row("net.spec.wifipowersave", net.WifiPowerSaving switch { 0 => Loc.T("common.off"), > 0 => Loc.T("common.on"), _ => "" })));
        }
        d.Specs.Add(Group(Loc.T("net.group.routing"),
            Row("net.spec.gateway", a.Gateway),
            Row("net.spec.dnsservers", string.Join(", ", a.DnsServers)),
            Row("label.vpn", net.ViaVpn ? net.Primary!.Description : Loc.T("common.no"))));
        return d;
    }

    private static string Band(double ghz) => ghz.ToString("0.#", Loc.Instance.Culture) + " GHz";

    private static string Mbps(double value) => value > 0 ? value.ToString(value < 10 ? "0.#" : "0", Loc.Instance.Culture) + " Mbps" : "";

    private static SpecRow? Row(string key, string? value) =>
        string.IsNullOrWhiteSpace(value) || value == Fmt.Dash ? null : new SpecRow(Loc.T(key), value.Trim());

    private static SpecGroup Group(string title, params SpecRow?[] rows) => new(title, rows.OfType<SpecRow>().ToList());
}
