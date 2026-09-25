using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PanRaProxy.Options;

namespace PanRaProxy.Radius;

/// <summary>
/// The shared secret of each RADIUS Client, keyed by source IP.
/// </summary>
public sealed class RadiusClientRegistry
{
    private readonly Dictionary<IPAddress, byte[]> secrets;

    public RadiusClientRegistry(IReadOnlyDictionary<IPAddress, string> secretsByAddress)
    {
        this.secrets = secretsByAddress.ToDictionary(p => Normalize(p.Key), p => Encoding.UTF8.GetBytes(p.Value));
    }

    public bool TryGetSecret(IPAddress source, [NotNullWhen(true)] out byte[]? secret) =>
        this.secrets.TryGetValue(Normalize(source), out secret);

    /// <summary>
    /// Resolves each configured host to its IPv4 addresses once, at startup. A host that doesn't resolve,
    /// or two hosts sharing an address with different secrets, stops the Proxy (NFR-08).
    /// </summary>
    public static RadiusClientRegistry FromOptions(RadiusOptions options, SecretLookup secrets, Func<string, IPAddress[]> resolve)
    {
        Dictionary<IPAddress, string> secretsByAddress = [];

        foreach (RadiusClientOptions client in options.Clients)
        {
            string secret = secrets(client.SecretName)
                            ?? throw new InvalidOperationException($"Secret '{client.SecretName}' for RADIUS Client '{client.Host}' is not set.");

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

            foreach (IPAddress address in addresses.Select(Normalize))
            {
                if (secretsByAddress.TryGetValue(address, out string? existing) && existing != secret)
                {
                    throw new InvalidOperationException($"Address {address} belongs to more than one RADIUS Client with different secrets.");
                }

                secretsByAddress[address] = secret;
            }
        }

        return new RadiusClientRegistry(secretsByAddress);
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
