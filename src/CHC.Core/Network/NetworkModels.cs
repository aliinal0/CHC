namespace CHC.Core.Network;

/// <summary>How the computer is connected to the internet, read during the scan.</summary>
public sealed class NetworkInfo
{
    /// <summary>The adapter internet traffic leaves through (a VPN adapter when one routes everything).</summary>
    public NetAdapterInfo? Primary { get; set; }

    /// <summary>The physical adapter behind <see cref="Primary"/> (the same one when no VPN is in between).</summary>
    public NetAdapterInfo? Physical { get; set; }

    /// <summary>Wireless "Power Saving Mode" of the power plan: 0 maximum performance ... 3 maximum power saving.</summary>
    public uint? WifiPowerSaving { get; set; }

    public bool ViaVpn => Primary is not null && Physical is not null && Primary.Id != Physical.Id;
}

public sealed class NetAdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsWireless { get; set; }
    public bool IsVpn { get; set; }

    /// <summary>Negotiated link speed in Mbit/s (the transmit rate on Wi-Fi).</summary>
    public long LinkMbps { get; set; }

    /// <summary>Fastest speed the adapter supports, worked out from its name; null if the name doesn't say.</summary>
    public long? MaxMbps { get; set; }

    /// <summary>False for half duplex, which loses packets on a modern network.</summary>
    public bool? FullDuplex { get; set; }

    public string DriverVersion { get; set; } = "";
    public DateTime? DriverDate { get; set; }
    public string Gateway { get; set; } = "";
    public List<string> DnsServers { get; set; } = [];

    /// <summary>Energy-Efficient Ethernet, Green Ethernet or a similar power-saving option is turned on in the driver.</summary>
    public bool? PowerSaving { get; set; }

    // Wi-Fi only (Windows may hide these unless location access is allowed).
    public int? SignalPercent { get; set; }
    public int? RssiDbm { get; set; }
    public int? Channel { get; set; }
    public double? BandGHz { get; set; }
    public string WifiStandard { get; set; } = "";
}

/// <summary>One second of a network test.</summary>
public sealed class NetSample
{
    public double Second { get; set; }

    /// <summary>Average round trip in this second; null if every ping was lost or none was sent.</summary>
    public double? GatewayMs { get; set; }
    public double? InternetMs { get; set; }
    public double? ServerMs { get; set; }
    public int GatewayLost { get; set; }
    public int InternetLost { get; set; }
    public double DownMbps { get; set; }
    public double UpMbps { get; set; }
}

/// <summary>A program that sent or received data while the network test ran.</summary>
public sealed record NetProgramUsage(string Name, ulong BytesDown, ulong BytesUp);

/// <summary>Everything recorded during one network test.</summary>
public sealed class NetSession
{
    public DateTime Started { get; set; }
    public DateTime Ended { get; set; }
    public TimeSpan Duration => Ended - Started;

    public string Adapter { get; set; } = "";
    public bool Wireless { get; set; }
    public string GatewayAddress { get; set; } = "";
    public string InternetTarget { get; set; } = "";

    /// <summary>Round trips in order; null is a lost ping.</summary>
    public List<double?> GatewayPings { get; set; } = [];
    public List<double?> InternetPings { get; set; } = [];

    /// <summary>A second public server, to tell a problem with one server from a problem with the connection.</summary>
    public List<double?> SecondaryPings { get; set; } = [];
    public string SecondaryTarget { get; set; } = "";

    /// <summary>The game server, when the test ran during an online game (it may ignore pings).</summary>
    public List<double?> ServerPings { get; set; } = [];
    public string ServerAddress { get; set; } = "";

    /// <summary>DNS lookup times; null is a lookup that failed or timed out.</summary>
    public List<double?> DnsLookups { get; set; } = [];

    public List<NetSample> Samples { get; set; } = [];

    /// <summary>Per-program traffic (needs administrator rights); empty when it could not be measured.</summary>
    public List<NetProgramUsage> Programs { get; set; } = [];
    public bool ProgramsMeasured { get; set; }

    public ulong BytesDown { get; set; }
    public ulong BytesUp { get; set; }

    public NetStats GatewayStats => NetStats.From(GatewayPings);
    public NetStats InternetStats => NetStats.From(InternetPings);
    public NetStats SecondaryStats => NetStats.From(SecondaryPings);
    public NetStats ServerStats => NetStats.From(ServerPings);
    public NetStats DnsStats => NetStats.From(DnsLookups);
}

/// <summary>Latency statistics of one ping target. <see cref="Spikes"/> counts episodes: a run of slow pings is one spike.</summary>
public sealed record NetStats(int Sent, int Lost, double Min, double Median, double Average, double P95, double Max, double Jitter, int Spikes)
{
    public int Received => Sent - Lost;
    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    public bool HasData => Received > 0;

    /// <summary>A round trip far above the usual one: felt in games as a lag spike.</summary>
    public static bool IsSpike(double ms, double median) => ms > Math.Max(median * 2, median + 30);

    public static NetStats From(IReadOnlyList<double?> results)
    {
        var ok = results.Where(r => r is not null).Select(r => r!.Value).ToList();
        if (ok.Count == 0) return new(results.Count, results.Count, 0, 0, 0, 0, 0, 0, 0);
        var sorted = ok.Order().ToList();
        double Pct(double p) => sorted[(int)Math.Clamp(Math.Round(p / 100 * (sorted.Count - 1)), 0, sorted.Count - 1)];
        var median = Pct(50);
        // Jitter: average change between one round trip and the next (RFC 3550 style, without smoothing).
        var jitter = ok.Count < 2 ? 0 : ok.Zip(ok.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        var spikes = 0;
        var inSpike = false;
        foreach (var ms in ok)
        {
            var slow = IsSpike(ms, median);
            if (slow && !inSpike) spikes++;
            inSpike = slow;
        }
        return new(results.Count, results.Count - ok.Count, sorted[0], median, ok.Average(), Pct(95), sorted[^1], jitter, spikes);
    }
}
