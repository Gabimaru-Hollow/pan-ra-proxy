using System.Net;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Tests.Radius;

public class RadiusClientRegistryTests
{
    private static readonly Dictionary<string, string> Environment = new()
    {
        ["SECRET_A"] = "secret-a",
        ["SECRET_B"] = "secret-b",
    };

    private static readonly Dictionary<string, IPAddress[]> Dns = new()
    {
        ["nps1.contoso.local"] = [IPAddress.Parse("10.0.0.10"), IPAddress.Parse("fe80::1")],
        ["nps-alias.contoso.local"] = [IPAddress.Parse("10.0.0.10")],
        ["v6only.contoso.local"] = [IPAddress.Parse("fe80::2")],
    };

    private static RadiusClientRegistry Build(params (string Host, string SecretName)[] clients) =>
        RadiusClientRegistry.FromOptions(
            new RadiusOptions { Clients = clients.Select(c => new RadiusClientOptions { Host = c.Host, SecretName = c.SecretName }).ToList() },
            name => Environment.GetValueOrDefault(name),
            host => Dns.TryGetValue(host, out IPAddress[]? a) ? a : throw new System.Net.Sockets.SocketException(11001));

    [Fact]
    public void Ip_literal_and_resolved_host_both_register()
    {
        RadiusClientRegistry registry = Build(("192.168.1.5", "SECRET_B"), ("nps1.contoso.local", "SECRET_A"));

        Assert.True(registry.TryGetSecret(IPAddress.Parse("192.168.1.5"), out byte[]? b));
        Assert.Equal("secret-b"u8.ToArray(), b);
        Assert.True(registry.TryGetSecret(IPAddress.Parse("10.0.0.10"), out byte[]? a));
        Assert.Equal("secret-a"u8.ToArray(), a);
        Assert.False(registry.TryGetSecret(IPAddress.Parse("fe80::1"), out _));
    }

    [Fact]
    public void Unresolvable_host_stops_startup()
    {
        Assert.ThrowsAny<Exception>(() => Build(("missing.contoso.local", "SECRET_A")));
    }

    [Fact]
    public void Host_without_ipv4_address_stops_startup()
    {
        Assert.Throws<InvalidOperationException>(() => Build(("v6only.contoso.local", "SECRET_A")));
    }

    [Fact]
    public void Same_address_with_different_secrets_stops_startup()
    {
        Assert.Throws<InvalidOperationException>(() => Build(("nps1.contoso.local", "SECRET_A"), ("nps-alias.contoso.local", "SECRET_B")));
    }

    [Fact]
    public void Same_address_with_same_secret_is_allowed()
    {
        RadiusClientRegistry registry = Build(("nps1.contoso.local", "SECRET_A"), ("nps-alias.contoso.local", "SECRET_A"));

        Assert.True(registry.TryGetSecret(IPAddress.Parse("10.0.0.10"), out _));
    }
}
