using System.Net;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Radius;

public class RadiusClientRegistryTests
{
    private static readonly Dictionary<string, IPAddress[]> Dns = new()
    {
        ["nps1.contoso.local"] = [IPAddress.Parse("10.0.0.10"), IPAddress.Parse("fe80::1")],
        ["nps-alias.contoso.local"] = [IPAddress.Parse("10.0.0.10")],
        ["v6only.contoso.local"] = [IPAddress.Parse("fe80::2")],
    };

    private static RadiusClientRegistry Build(params string[] hosts) =>
        RadiusClientRegistry.FromOptions(
            new RadiusOptions { Clients = hosts.Select(h => new RadiusClientOptions { Host = h }).ToList() },
            name => name == SecretNames.Radius ? "shared-secret" : null,
            host => Dns.TryGetValue(host, out IPAddress[]? a) ? a : throw new System.Net.Sockets.SocketException(11001));

    [Fact]
    public void Every_radius_client_shares_the_one_secret()
    {
        RadiusClientRegistry registry = Build("192.168.1.5", "nps1.contoso.local");

        Assert.True(registry.TryGetSecret(IPAddress.Parse("192.168.1.5"), out byte[]? literal));
        Assert.Equal("shared-secret"u8.ToArray(), literal);
        Assert.True(registry.TryGetSecret(IPAddress.Parse("10.0.0.10"), out byte[]? resolved));
        Assert.Equal("shared-secret"u8.ToArray(), resolved);
        Assert.True(registry.TryGetSecret(IPAddress.Parse("::ffff:10.0.0.10"), out _)); // IPv4-mapped
        Assert.False(registry.TryGetSecret(IPAddress.Parse("fe80::1"), out _));         // IPv6 isn't registered
        Assert.False(registry.TryGetSecret(IPAddress.Parse("10.0.0.99"), out _));
    }

    [Fact]
    public void Two_names_for_the_same_address_are_fine()
    {
        RadiusClientRegistry registry = Build("nps1.contoso.local", "nps-alias.contoso.local");

        Assert.True(registry.TryGetSecret(IPAddress.Parse("10.0.0.10"), out _));
    }

    [Fact]
    public void Unresolvable_host_stops_startup()
    {
        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => Build("missing.contoso.local"));
        Assert.Contains("missing.contoso.local", ex.Message);
    }

    [Fact]
    public void Host_without_ipv4_address_stops_startup()
    {
        Assert.Throws<InvalidOperationException>(() => Build("v6only.contoso.local"));
    }

    [Fact]
    public void A_missing_secret_stops_startup()
    {
        Assert.Throws<InvalidOperationException>(() =>
            RadiusClientRegistry.FromOptions(new RadiusOptions { Clients = [new() { Host = "10.0.0.10" }] }, _ => null, _ => []));
    }
}
