namespace PanRaProxy.Radius;

/// <summary>
/// What the Proxy does with one received datagram.
/// </summary>
public abstract record PacketVerdict
{
    private PacketVerdict()
    {
    }

    /// <summary>
    /// Authenticated: <see cref="Response"/> must be sent back whatever happens to the request afterwards (FR-00).
    /// </summary>
    public sealed record Accepted(AccountingRequest Request, byte[] Response) : PacketVerdict;

    /// <summary>
    /// Silently discarded: no response is sent (RFC 2866 §3).
    /// </summary>
    public sealed record Discarded(DiscardReason Reason) : PacketVerdict;
}

public enum DiscardReason
{
    /// <summary>Shorter than a header, declared Length out of range, or attributes don't fit the packet.</summary>
    Malformed,

    /// <summary>Any code other than Accounting-Request (4).</summary>
    NotAccountingRequest,

    /// <summary>The source IP isn't a configured RADIUS Client.</summary>
    UnknownClient,

    /// <summary>The Request Authenticator doesn't match the RADIUS Client's shared secret.</summary>
    BadAuthenticator,
}
