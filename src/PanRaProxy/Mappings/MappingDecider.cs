using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Mappings;

/// <summary>
/// Decides what an accepted Accounting-Request means for Mappings: Username Filter on the raw
/// User-Name, Placeholder IP removal, Login with Timeout on Start/Interim-Update, Logout-on-Stop.
/// The Canonical Username comes from <see cref="CanonicalUsernameResolver"/>.
/// </summary>
public sealed class MappingDecider
{
    /// <summary>
    /// Framed-IP-Address values meaning "no real address yet" (RFC 2865 §5.8 and unassigned).
    /// </summary>
    private static readonly IPAddress[] PlaceholderIps =
    [
        IPAddress.Any,                       // 0.0.0.0
        IPAddress.Broadcast,                 // 255.255.255.255: the user may select an address
        IPAddress.Parse("255.255.255.254"),  // the NAS should select an address
    ];

    private readonly UserIdOptions options;
    private readonly CanonicalUsernameResolver canonicalUsername;
    private readonly Regex? usernameFilter;
    private readonly TimeSpan timeout;

    public MappingDecider(IOptions<UserIdOptions> options, CanonicalUsernameResolver canonicalUsername)
    {
        this.options = options.Value;
        this.canonicalUsername = canonicalUsername;
        this.timeout = TimeSpan.FromMinutes(this.options.TimeoutMinutes);

        this.usernameFilter = string.IsNullOrEmpty(this.options.UsernameFilter)
            ? null
            : OptionsChecks.CreateRegex(this.options.UsernameFilter);
    }

    public MappingDecision Decide(AccountingRequest request)
    {
        if (request.StatusType is not { } statusType || request.UserName is not { } rawUsername)
        {
            return new MappingDecision.Dropped(DropReason.MissingAttributes);
        }

        if (statusType is not (AcctStatusType.Start or AcctStatusType.InterimUpdate or AcctStatusType.Stop))
        {
            return new MappingDecision.Dropped(DropReason.UnsupportedStatusType);
        }

        if (this.usernameFilter?.IsMatch(rawUsername) == true)
        {
            return new MappingDecision.Dropped(DropReason.Filtered);
        }

        IPAddress[] addresses = request.FramedIpAddresses.Where(a => !PlaceholderIps.Contains(a)).Distinct().ToArray();
        if (addresses.Length == 0)
        {
            return new MappingDecision.Dropped(DropReason.NoUsableIpAddress);
        }

        if (statusType == AcctStatusType.Stop && !this.options.LogoutOnStop)
        {
            return new MappingDecision.Dropped(DropReason.LogoutOnStopDisabled);
        }

        string username = this.canonicalUsername.Resolve(rawUsername);

        if (!IsUsable(username))
        {
            return new MappingDecision.Dropped(DropReason.UnusableUsername);
        }

        MappingChange[] changes = statusType == AcctStatusType.Stop
            ? addresses.Select(a => (MappingChange)new MappingChange.Logout(new Mapping(username, a))).ToArray()
            : addresses.Select(a => (MappingChange)new MappingChange.Login(new Mapping(username, a), this.timeout)).ToArray();

        return new MappingDecision.Changes(changes);
    }

    /// <summary>
    /// A uid-message is XML: one username it can't carry would fail the whole Batch, every Interim-Update.
    /// </summary>
    private static bool IsUsable(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        try
        {
            XmlConvert.VerifyXmlChars(username);
            return true;
        }
        catch (XmlException)
        {
            return false;
        }
    }
}
