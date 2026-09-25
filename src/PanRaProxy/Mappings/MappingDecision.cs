using System.Net;

namespace PanRaProxy.Mappings;

/// <summary>
/// The association of one Canonical Username with one IP address, as held by the Firewall.
/// </summary>
public readonly record struct Mapping(string Username, IPAddress IpAddress)
{
    public override string ToString() => $"{this.Username} on {this.IpAddress}";
}

/// <summary>
/// An instruction to the Firewall about one Mapping.
/// </summary>
public abstract record MappingChange(Mapping Mapping)
{
    public sealed record Login(Mapping Mapping, TimeSpan Timeout) : MappingChange(Mapping);

    public sealed record Logout(Mapping Mapping) : MappingChange(Mapping);
}

/// <summary>
/// What one accepted Accounting-Request means for Mappings.
/// </summary>
public abstract record MappingDecision
{
    private MappingDecision()
    {
    }

    public sealed record Changes(IReadOnlyList<MappingChange> Items) : MappingDecision;

    public sealed record Dropped(DropReason Reason) : MappingDecision;
}

public enum DropReason
{
    /// <summary>No usable Acct-Status-Type or User-Name.</summary>
    MissingAttributes,

    /// <summary>An Acct-Status-Type other than Start, Stop or Interim-Update (e.g. Accounting-On/Off).</summary>
    UnsupportedStatusType,

    /// <summary>The raw User-Name matched the Username Filter (e.g. a Machine Account).</summary>
    Filtered,

    /// <summary>No Framed-IP-Address other than Placeholder IPs.</summary>
    NoUsableIpAddress,

    /// <summary>An Accounting-Stop while Logout-on-Stop is off.</summary>
    LogoutOnStopDisabled,
}
