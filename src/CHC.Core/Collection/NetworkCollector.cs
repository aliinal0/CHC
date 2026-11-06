using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using CHC.Core.Network;
using Microsoft.Win32;

namespace CHC.Core.Collection;

/// <summary>Finds the adapter the internet goes through and reads what affects ping: Wi-Fi or cable, link speed, VPN, power saving.</summary>
internal static partial class NetworkCollector
{
    private static readonly Guid WifiGroup = new("19cbb8fa-5279-450e-9fac-8a3d5fedd0c1");
    private static readonly Guid WifiPowerSaving = new("12bbebe6-58d6-4636-95bb-3217ef867c1a");
    private const string NetClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    /// <summary>Driver options that save energy on the link and are known to cause drops and resets.</summary>
    private static readonly string[] PowerSavingKeywords = ["*EEE", "EEE", "EnableGreenEthernet", "AdvancedEEE", "EEELinkAdvertisement", "PowerSavingMode", "ReduceSpeedOnPowerDown"];

    public static NetworkInfo Collect()
    {
        var info = new NetworkInfo();
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Where(n => Gateway(n) is not null)
            .ToList();
        if (adapters.Count == 0) return info;

        // The adapter Windows would send internet traffic through.
        var best = Native.BestInterfaceIndex(IPAddress.Parse("1.1.1.1"));
        var primary = adapters.FirstOrDefault(n => Index(n) == best) ?? adapters[0];
        var physical = IsVpn(primary) ? adapters.FirstOrDefault(n => !IsVpn(n)) ?? primary : primary;

        info.Primary = Describe(primary);
        info.Physical = physical == primary ? info.Primary : Describe(physical);
        if (Native.ActivePowerScheme() is { } scheme)
            info.WifiPowerSaving = Native.PowerAcValue(scheme, WifiGroup, WifiPowerSaving);
        return info;
    }

    private static NetAdapterInfo Describe(NetworkInterface n)
    {
        var props = n.GetIPProperties();
        var a = new NetAdapterInfo
        {
            Id = n.Id,
            Name = n.Name,
            Description = n.Description,
            IsWireless = n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
            IsVpn = IsVpn(n),
            LinkMbps = Math.Max(0, n.Speed) / 1_000_000,
            Gateway = Gateway(n)?.ToString() ?? "",
            DnsServers = props.DnsAddresses.Where(d => d.AddressFamily == AddressFamily.InterNetwork).Select(d => d.ToString()).Distinct().ToList(),
        };
        if (!a.IsVpn) a.MaxMbps = MaxSpeed(n.Description);

        // MSFT_NetAdapter has the duplex mode and the driver; DeviceID is the same GUID as NetworkInterface.Id.
        var id = n.Id.Replace("'", "");
        if (Wmi.First($"SELECT MediaDuplexState, DriverVersionString, DriverDate, NdisPhysicalMedium FROM MSFT_NetAdapter WHERE DeviceID = '{id}'", Wmi.StandardCimv2) is { } m)
        {
            a.FullDuplex = m.I32("MediaDuplexState") switch { 1 => false, 2 => true, _ => null };
            a.DriverVersion = m.Str("DriverVersionString");
            if (DateTime.TryParse(m.Str("DriverDate"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) a.DriverDate = date;
            if (m.I32("NdisPhysicalMedium") == 9) a.IsWireless = true;
        }

        if (!a.IsVpn) a.PowerSaving = DriverPowerSaving(n.Id);
        if (a.IsWireless && Wlan.Query(n.Id) is { } wifi)
        {
            a.SignalPercent = wifi.SignalPercent;
            a.RssiDbm = wifi.RssiDbm;
            a.Channel = wifi.Channel;
            a.BandGHz = wifi.BandGHz;
            a.WifiStandard = wifi.Standard;
        }
        return a;
    }

    private static IPAddress? Gateway(NetworkInterface n)
    {
        try
        {
            return n.GetIPProperties().GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any));
        }
        catch (NetworkInformationException) { return null; }
    }

    private static int Index(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; }
        catch (NetworkInformationException) { return -1; }
    }

    private static bool IsVpn(NetworkInterface n) =>
        n.NetworkInterfaceType is NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel || VpnPattern().IsMatch(n.Description) || VpnPattern().IsMatch(n.Name);

    [GeneratedRegex(@"VPN|TAP-|WireGuard|Wintun|OpenVPN|NordLynx|ZeroTier|Tailscale|Cloudflare WARP|Hamachi|Radmin|AnyConnect|GlobalProtect|PANGP|Fortinet|Proton|Mullvad|Surfshark|ExpressVPN|Virtual Private", RegexOptions.IgnoreCase)]
    private static partial Regex VpnPattern();

    /// <summary>Most adapter names say what they can do: "2.5GbE", "Gigabit", "I225-V", "10Gbit"...</summary>
    public static long? MaxSpeed(string description)
    {
        var d = description.ToUpperInvariant();
        if (Regex.IsMatch(d, @"2\.5\s?G|I22[56]|E3[01]00|RTL8125")) return 2_500;
        if (Regex.IsMatch(d, @"\b10\s?G(BE|BIT|IGABIT)?\b|10GBE|AQTION")) return 10_000;
        if (Regex.IsMatch(d, @"(?<!\.)\b5\s?G(BE|BIT)\b")) return 5_000;
        if (Regex.IsMatch(d, @"GIGABIT|\bGBE\b|GBIT|\bI21[0179]|82579|8257[0-9]|E2[456]00|RTL8111|RTL8168")) return 1_000;
        if (Regex.IsMatch(d, @"\bFE\b|FAST ETHERNET|10/100\b")) return 100;
        return null;
    }

    /// <summary>Reads the adapter's advanced driver options from its device class key.</summary>
    private static bool? DriverPowerSaving(string adapterId)
    {
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(NetClass);
            if (root is null) return null;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name);
                if (sub?.GetValue("NetCfgInstanceId") is not string id || !id.Equals(adapterId, StringComparison.OrdinalIgnoreCase)) continue;
                var found = false;
                foreach (var keyword in PowerSavingKeywords)
                {
                    if (sub.GetValue(keyword) is not { } value) continue;
                    found = true;
                    if (value.ToString() is { } text && int.TryParse(text, out var on) && on != 0) return true;
                }
                return found ? false : null;
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return null;
    }
}
