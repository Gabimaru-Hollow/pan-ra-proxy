using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;

namespace PanRaProxy.Radius;

/// <summary>
/// Turns one received datagram into a <see cref="PacketVerdict"/>: header and length checks,
/// RADIUS Client lookup, Request Authenticator check, attribute walk and Accounting-Response.
/// Pure: no I/O, no logging, never throws on packet content.
/// </summary>
public sealed class AccountingPacketHandler(RadiusClientRegistry clients)
{
    private const byte AccountingRequestCode = 4;
    private const byte AccountingResponseCode = 5;
    private const int HeaderLength = 20;
    private const int AuthenticatorOffset = 4;
    private const int AuthenticatorLength = 16;
    private const int MaxPacketLength = 4096;

    private static readonly byte[] ZeroAuthenticator = new byte[AuthenticatorLength];

    public PacketVerdict Handle(ReadOnlySpan<byte> datagram, IPAddress source)
    {
        if (datagram.Length < HeaderLength)
        {
            return new PacketVerdict.Discarded(DiscardReason.Malformed);
        }

        // RFC 2865 §3: shorter than Length is discarded, octets beyond Length are padding.
        int length = BinaryPrimitives.ReadUInt16BigEndian(datagram[2..4]);
        if (length is < HeaderLength or > MaxPacketLength || length > datagram.Length)
        {
            return new PacketVerdict.Discarded(DiscardReason.Malformed);
        }

        ReadOnlySpan<byte> packet = datagram[..length];

        if (packet[0] != AccountingRequestCode)
        {
            return new PacketVerdict.Discarded(DiscardReason.NotAccountingRequest);
        }

        if (!clients.TryGetSecret(source, out byte[]? secret))
        {
            return new PacketVerdict.Discarded(DiscardReason.UnknownClient);
        }

        if (!IsRequestAuthentic(packet, secret))
        {
            return new PacketVerdict.Discarded(DiscardReason.BadAuthenticator);
        }

        if (!TryReadAttributes(packet[HeaderLength..], out List<RadiusAttribute> attributes))
        {
            return new PacketVerdict.Discarded(DiscardReason.Malformed);
        }

        AccountingRequest request = new(source, packet[1], attributes);
        return new PacketVerdict.Accepted(request, BuildResponse(packet, attributes, secret));
    }

    /// <summary>
    /// RFC 2866 §3: MD5(Code + Identifier + Length + 16 zero octets + Attributes + Secret).
    /// </summary>
    private static bool IsRequestAuthentic(ReadOnlySpan<byte> packet, byte[] secret)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(packet[..AuthenticatorOffset]);
        md5.AppendData(ZeroAuthenticator);
        md5.AppendData(packet[HeaderLength..]);
        md5.AppendData(secret);

        Span<byte> expected = stackalloc byte[AuthenticatorLength];
        md5.GetHashAndReset(expected);

        return CryptographicOperations.FixedTimeEquals(expected, packet.Slice(AuthenticatorOffset, AuthenticatorLength));
    }

    private static bool TryReadAttributes(ReadOnlySpan<byte> region, out List<RadiusAttribute> attributes)
    {
        attributes = [];
        int index = 0;

        while (index < region.Length)
        {
            if (region.Length - index < 2)
            {
                return false;
            }

            int attributeLength = region[index + 1];
            if (attributeLength < 2 || index + attributeLength > region.Length)
            {
                return false;
            }

            attributes.Add(new RadiusAttribute(region[index], region.Slice(index + 2, attributeLength - 2).ToArray()));
            index += attributeLength;
        }

        return true;
    }

    /// <summary>
    /// RFC 2866 §3 and RFC 2865 §5.33: echo every Proxy-State in order, then
    /// MD5(Code + Identifier + Length + Request Authenticator + Attributes + Secret).
    /// </summary>
    private static byte[] BuildResponse(ReadOnlySpan<byte> request, List<RadiusAttribute> attributes, byte[] secret)
    {
        List<RadiusAttribute> proxyStates = attributes.Where(a => a.Type == RadiusAttributeType.ProxyState).ToList();
        int length = HeaderLength + proxyStates.Sum(a => a.Value.Length + 2);

        byte[] response = new byte[length];
        response[0] = AccountingResponseCode;
        response[1] = request[1];
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(2, 2), (ushort)length);
        request.Slice(AuthenticatorOffset, AuthenticatorLength).CopyTo(response.AsSpan(AuthenticatorOffset));

        int index = HeaderLength;
        foreach (RadiusAttribute proxyState in proxyStates)
        {
            response[index] = RadiusAttributeType.ProxyState;
            response[index + 1] = (byte)(proxyState.Value.Length + 2);
            proxyState.Value.Span.CopyTo(response.AsSpan(index + 2));
            index += proxyState.Value.Length + 2;
        }

        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        md5.AppendData(response);
        md5.AppendData(secret);
        md5.GetHashAndReset(response.AsSpan(AuthenticatorOffset, AuthenticatorLength));

        return response;
    }
}
