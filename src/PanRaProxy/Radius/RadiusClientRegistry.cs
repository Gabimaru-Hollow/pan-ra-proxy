using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PanRaProxy.Options;

namespace PanRaProxy.Radius;

/// <summary>
/// Resolves a RADIUS Client's host name to its addresses. Production uses <see cref="Dns.GetHostAddresses(string)"/>;
/// tests pass a fixed table. Throws <see cref="SocketException"/> when the name doesn't resolve.
/// </summary>
public delegate IPAddress[] HostResolver(string host);

/// <summary>
/// The RADIUS Clients' addresses, and the one secret they share (ADR 0007).
/// </summary>
public sealed class RadiusClientRegistry
{
    private readonly HashSet<IPAddress> addresses;
    private readonly byte[] secret;

    public RadiusClientRegistry(IEnumerable<IPAddress> addresses, string secret)
    {
        this.addresses = addresses.Select(Normalize).ToHashSet();
        this.secret = Encoding.UTF8.GetBytes(secret);
    }

    public bool TryGetSecret(IPAddress source, [NotNullWhen(true)] out byte[]? secret)
    {
        secret = this.addresses.Contains(Normalize(source)) ? this.secret : null;
        return secret is not null;
    }

    /// <summary>
    /// Resolves each configured host to its IPv4 addresses once, at startup. A host that doesn't resolve
    /// stops the Proxy (NFR-08).
    /// </summary>
    public static RadiusClientRegistry FromOptions(RadiusOptions options, SecretLookup secrets, HostResolver resolve)
    {
        string secret = secrets(SecretNames.Radius)
                        ?? throw new InvalidOperationException($"The secret '{SecretNames.Radius}' is not set.");

        List<IPAddress> resolved = [];

        foreach (RadiusClientOptions client in options.Clients)
        {
            IPAddress[] addresses;
            try
            {
                addresses = IPAddress.TryParse(client.Host, out IPAddress? literal)
                    ? [literal]
                    : resolve(client.Host).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            }
            catch (SocketException ex)
            {
                throw new InvalidOperationException($"RADIUS Client '{client.Host}' did not resolve: {ex.Message}", ex);
            }

            if (addresses.Length == 0)
            {
                throw new InvalidOperationException($"RADIUS Client '{client.Host}' did not resolve to an IPv4 address.");
            }

            resolved.AddRange(addresses);
        }

        return new RadiusClientRegistry(resolved, secret);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
