using CHC.Core.Health;
using CHC.Core.Localization;
using CHC.Core.Models;
using CHC.Core.Presentation;

namespace CHC.Core.Network;

/// <summary>
/// Explains ping and connection problems: the connection itself (Wi-Fi, cable, VPN, power saving)
/// and, after a test, where lag and packet loss come from. Findings use <see cref="ComponentKind.Network"/>.
/// </summary>
public static class NetworkAnalyzer
{
    private const ComponentKind K = ComponentKind.Network;

    /// <summary>Traffic counts as heavy from 5 Mbit/s down or 2 Mbit/s up (an online game itself uses far less).</summary>
    private const double BusyDownMbps = 5, BusyUpMbps = 2;

    /// <summary>The first seconds are spent starting up; ignore them.</summary>
    private static List<NetSample> Usable(NetSession s) => s.Samples.Where(x => x.Second >= 2).ToList();

    public static List<Finding> Analyze(SystemSnapshot snapshot, NetSession? session)
    {
        var list = new List<Finding>();
        var net = snapshot.Network;
        var adapter = net.Physical;
        if (net.Primary is null || adapter is null)
        {
            list.Add(new(K, Severity.Warning, "net.offline"));
            return list;
        }

        Connection(snapshot, net, adapter, list);
        if (session is not null) Session(session, list);
        return list;
    }

    /// <summary>
    /// What a test found: where packets are lost, where the ping fluctuates, lag spikes, bufferbloat and DNS.
    /// <paramref name="summary"/> adds the "connection is stable" line when nothing in <paramref name="list"/> is worse than a tip.
    /// </summary>
    public static void Session(NetSession session, List<Finding> list, bool summary = true)
    {
        if (Usable(session).Count < 15)
        {
            list.Add(new(K, Severity.Info, "net.tooshort"));
            return;
        }

        var internet = session.InternetStats;
        var secondary = session.SecondaryStats;
        var gateway = session.GatewayStats;
        if (!internet.HasData && !secondary.HasData)
        {
            list.Add(new(K, Severity.Warning, "net.nointernet"));
            return;
        }

        // Some routers ignore pings; then the home network simply can't be measured separately.
        var gatewayKnown = gateway.Sent >= 10 && gateway.HasData;
        if (gateway.Sent >= 10 && !gateway.HasData)
            list.Add(new(K, Severity.Info, "net.gateway.silent"));

        // Pings lost while a download filled the line are explained by the bufferbloat finding, not by the provider.
        var bloated = Bufferbloat(session, list);
        var samples = Usable(session);
        var lostWhileBusy = samples.Where(Busy).Sum(x => x.InternetLost) * 2 >= Math.Max(1, samples.Sum(x => x.InternetLost));

        var localLoss = gatewayKnown && gateway.LossPercent >= 1;
        if (localLoss)
            list.Add(new(K, gateway.LossPercent >= 5 ? Severity.Critical : Severity.Warning, "net.loss.local", gateway.LossPercent));
        // Loss to both public servers rules out one busy server.
        else if (internet.LossPercent >= 1 && (!secondary.HasData || secondary.LossPercent >= 1) && !(bloated && lostWhileBusy))
            list.Add(new(K, internet.LossPercent >= 5 ? Severity.Critical : Severity.Warning, "net.loss.internet", internet.LossPercent));

        var localUnstable = gatewayKnown && (gateway.Jitter >= 5 || gateway.P95 >= Math.Max(20, gateway.Median + 15));
        if (localUnstable)
            list.Add(new(K, Severity.Warning, session.Wireless ? "net.jitter.local.wifi" : "net.jitter.local", gateway.Jitter, gateway.P95));
        else if (internet.Jitter >= 8)
            list.Add(new(K, internet.Jitter >= 15 ? Severity.Warning : Severity.Info, "net.jitter.internet", internet.Jitter));

        Spikes(session, internet, list);

        if (internet.HasData && internet.Median >= 60)
            list.Add(new(K, Severity.Info, "net.latency.high", internet.Median, session.InternetTarget));

        var dns = session.DnsStats;
        if (dns.Lost >= 2)
            list.Add(new(K, Severity.Warning, "net.dns.failed", dns.Lost, dns.Sent));
        else if (dns.HasData && dns.Median >= 100)
            list.Add(new(K, Severity.Info, "net.dns.slow", dns.Median));

        if (session.ProgramsMeasured && session.Programs.FirstOrDefault() is { } top && top.BytesDown + top.BytesUp >= 20 * 1024 * 1024)
            list.Add(new(K, Severity.Info, "net.programs", string.Join(", ", session.Programs.Take(3).Where(p => p.BytesDown + p.BytesUp >= 5 * 1024 * 1024).Select(p => $"{p.Name} ({Fmt.Bytes(p.BytesDown + p.BytesUp)})"))));

        if (summary && list.All(f => f.Severity < Severity.Warning) && internet.HasData)
            list.Add(new(K, Severity.Good, "net.good", internet.Median, internet.Jitter, internet.LossPercent));
    }

    /// <summary>Settings and states of the connection that are known to cause lag, drops or slow links.</summary>
    private static void Connection(SystemSnapshot snapshot, NetworkInfo net, NetAdapterInfo a, List<Finding> list)
    {
        if (net.ViaVpn)
            list.Add(new(K, Severity.Info, "net.vpn", net.Primary!.Description));

        if (a.IsWireless)
        {
            list.Add(new(K, Severity.Info, "net.wifi"));
            if (a.SignalPercent is { } signal && signal < 70)
                list.Add(new(K, signal < 50 ? Severity.Warning : Severity.Info, "net.wifi.signal", signal));
            if (a.BandGHz == 2.4)
                list.Add(new(K, Severity.Info, "net.wifi.band24"));
            if (net.WifiPowerSaving is > 0)
                list.Add(new(K, Severity.Info, "net.wifi.powersave"));
        }
        else
        {
            var slow = a.LinkMbps is > 0 and <= 100 && a.MaxMbps >= 1000;
            if (slow)
                list.Add(new(K, Severity.Warning, "net.link.slow", Fmt.LinkSpeed(a.LinkMbps), Fmt.LinkSpeed(a.MaxMbps ?? 0)));
            if (a.FullDuplex == false)
                list.Add(new(K, Severity.Warning, "net.link.halfduplex"));
            var resets = snapshot.Stability.NetworkResets.Count > 0;
            if (a.PowerSaving == true && (slow || resets))
                list.Add(new(K, Severity.Info, "net.powersaving", a.Description));
        }

        // The same adapter resets the Stability page reports, here where people look for lag.
        var st = snapshot.Stability;
        if (st.NetworkResets.GroupBy(n => n.Adapter, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Sum(n => n.Resets)).FirstOrDefault() is { } nic)
        {
            var resets = nic.Sum(n => n.Resets);
            var bursts = nic.Count();
            list.Add(new(K, bursts >= 2 || resets >= 5 ? Severity.Warning : Severity.Info, "stability.network", Fmt.Or(nic.Key), resets, bursts, StabilityInfo.PeriodDays));
        }
        if (st.DnsTimeouts.Count >= 20)
            list.Add(new(K, Severity.Info, "net.dns.timeouts", st.DnsTimeouts.Count, StabilityInfo.PeriodDays));
    }

    /// <summary>Lag spikes, and whether they started at home, on a busy connection or further out.</summary>
    private static void Spikes(NetSession session, NetStats internet, List<Finding> list)
    {
        if (!internet.HasData || internet.Spikes < 3) return;
        var minutes = Math.Max(0.5, session.Duration.TotalMinutes);
        var perMinute = internet.Spikes / minutes;

        var samples = Usable(session);
        var gatewayMedian = session.GatewayStats.Median;
        var spiky = samples.Where(x => x.InternetMs is { } ms && NetStats.IsSpike(ms, internet.Median)).ToList();
        var local = spiky.Count(x => x.GatewayLost > 0 || (x.GatewayMs is { } g && NetStats.IsSpike(g, gatewayMedian)));
        var loaded = spiky.Count(Busy);
        var cause = spiky.Count == 0 ? "isp"
            : local * 2 >= spiky.Count ? "local"
            : loaded * 2 >= spiky.Count ? "busy"
            : "isp";
        list.Add(new(K, perMinute >= 2 ? Severity.Warning : Severity.Info, "net.spikes", internet.Spikes, perMinute, new LocKey("net.spikecause." + cause)));
    }

    /// <summary>Ping that climbs while something downloads or uploads: the router's buffers fill up ("bufferbloat").</summary>
    private static bool Bufferbloat(NetSession session, List<Finding> list)
    {
        var samples = Usable(session).Where(x => x.InternetMs is not null).ToList();
        var loaded = samples.Where(Busy).Select(x => x.InternetMs!.Value).ToList();
        var idle = samples.Where(x => !Busy(x)).Select(x => x.InternetMs!.Value).ToList();
        if (loaded.Count < 5 || idle.Count < 5) return false;
        var idleMs = Median(idle);
        var loadedMs = Median(loaded);
        if (loadedMs - idleMs < 25) return false;
        list.Add(new(K, Severity.Warning, "net.bufferbloat", idleMs, loadedMs));
        return true;
    }

    private static bool Busy(NetSample x) => x.DownMbps >= BusyDownMbps || x.UpMbps >= BusyUpMbps;

    private static double Median(List<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
