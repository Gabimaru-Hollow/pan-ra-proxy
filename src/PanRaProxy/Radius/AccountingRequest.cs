using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace PanRaProxy.Radius;

/// <summary>
/// RADIUS attribute type codes the Proxy reads or echoes (RFC 2865, RFC 2866).
/// </summary>
public static class RadiusAttributeType
{
    public const byte UserName = 1;
    public const byte FramedIpAddress = 8;
    public const byte VendorSpecific = 26;
    public const byte ProxyState = 33;
    public const byte AcctStatusType = 40;
    public const byte AcctSessionId = 44;
}

/// <summary>
/// Acct-Status-Type values (RFC 2866 §5.1) the Proxy acts on.
/// </summary>
public static class AcctStatusType
{
    public const uint Start = 1;
    public const uint Stop = 2;
    public const uint InterimUpdate = 3;
}

/// <summary>
/// One attribute exactly as received: its type code and raw value octets.
/// Interpretation is left to the accessors on <see cref="AccountingRequest"/>, so an
/// unexpected value shape yields "absent", never an exception.
/// </summary>
public readonly record struct RadiusAttribute(byte Type, ReadOnlyMemory<byte> Value);

/// <summary>
/// An authenticated Accounting-Request from a RADIUS Client.
/// </summary>
public sealed class AccountingRequest(IPAddress source, byte identifier, IReadOnlyList<RadiusAttribute> attributes)
{
    public IPAddress Source { get; } = source;

    public byte Identifier { get; } = identifier;

    public IReadOnlyList<RadiusAttribute> Attributes { get; } = attributes;

    /// <summary>
    /// Acct-Status-Type, or null when absent or not a 4-octet integer.
    /// </summary>
    public uint? StatusType
    {
        get
        {
            RadiusAttribute? attribute = this.First(RadiusAttributeType.AcctStatusType);
            return attribute is { Value.Length: 4 } a ? BinaryPrimitives.ReadUInt32BigEndian(a.Value.Span) : null;
        }
    }

    /// <summary>
    /// User-Name exactly as received (UTF-8, RFC 2865 §5.1), or null when absent or empty.
    /// </summary>
    public string? UserName
    {
        get
        {
            RadiusAttribute? attribute = this.First(RadiusAttributeType.UserName);
            return attribute is { Value.Length: > 0 } a ? Encoding.UTF8.GetString(a.Value.Span) : null;
        }
    }

    /// <summary>
    /// Every well-formed Framed-IP-Address, in order, including Placeholder IPs.
    /// </summary>
    public IEnumerable<IPAddress> FramedIpAddresses =>
        this.Attributes
            .Where(a => a.Type == RadiusAttributeType.FramedIpAddress && a.Value.Length == 4)
            .Select(a => new IPAddress(a.Value.Span));

    public override string ToString() =>
        $"Accounting-Request #{this.Identifier} from {this.Source}: status={this.StatusType?.ToString() ?? "?"}, user={this.UserName ?? "?"}, ip=[{string.Join(", ", this.FramedIpAddresses)}]";

    private RadiusAttribute? First(byte type)
    {
        foreach (RadiusAttribute attribute in this.Attributes)
        {
            if (attribute.Type == type)
            {
                return attribute;
            }
        }

        return null;
    }
}
