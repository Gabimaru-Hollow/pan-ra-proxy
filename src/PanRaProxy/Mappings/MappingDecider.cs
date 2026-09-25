using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Mappings;

/// <summary>
/// Decides what an accepted Accounting-Request means for Mappings: Username Filter on the raw
/// User-Name, Username Rewrite and optional Name Translation to the Canonical Username,
/// Placeholder IP removal, Login with Timeout on Start/Interim-Update, Logout-on-Stop.
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
    private readonly INameTranslator nameTranslator;
    private readonly Regex? usernameFilter;
    private readonly (Regex Match, string Replace)[] usernameRewrites;
    private readonly TimeSpan timeout;

    public MappingDecider(IOptions<UserIdOptions> options, INameTranslator nameTranslator)
    {
        this.options = options.Value;
        this.nameTranslator = nameTranslator;
        this.timeout = TimeSpan.FromMinutes(this.options.TimeoutMinutes);

        this.usernameFilter = string.IsNullOrEmpty(this.options.UsernameFilter)
            ? null
            : CreateRegex(this.options.UsernameFilter);

        this.usernameRewrites = this.options.UsernameRewrites
            .Select(r => (CreateRegex(r.Match), r.Replace))
            .ToArray();
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

        string username = this.ToCanonicalUsername(rawUsername);

        MappingChange[] changes = statusType == AcctStatusType.Stop
            ? addresses.Select(a => (MappingChange)new MappingChange.Logout(new Mapping(username, a))).ToArray()
            : addresses.Select(a => (MappingChange)new MappingChange.Login(new Mapping(username, a), this.timeout)).ToArray();

        return new MappingDecision.Changes(changes);
    }

    /// <summary>
    /// Username Rewrite rules first, then the domain rules for the three forms seen on the wire:
    /// NT4 (kept), UPN (translated, or its suffix mapped to a domain) and bare (given the default
    /// UPN suffix and translated, or the default domain). A form with no rule is passed through.
    /// </summary>
    private string ToCanonicalUsername(string rawUsername)
    {
        string username = rawUsername;

        foreach ((Regex match, string replace) in this.usernameRewrites)
        {
            username = match.Replace(username, replace);
        }

        DomainOptions domain = this.options.Domain;

        if (username.Contains('\\'))
        {
            return username; // already NT4
        }

        if (username.Contains('@'))
        {
            return this.FromUpn(username, domain);
        }

        if (this.options.NameTranslation && !string.IsNullOrEmpty(domain.DefaultUpnSuffix)
            && this.nameTranslator.TryTranslateToNt4($"{username}@{domain.DefaultUpnSuffix}") is { } translatedBare)
        {
            return translatedBare;
        }

        return string.IsNullOrEmpty(domain.DefaultNt4Domain) ? username : $"{domain.DefaultNt4Domain}\\{username}";
    }

    private string FromUpn(string upn, DomainOptions domain)
    {
        if (this.options.NameTranslation && this.nameTranslator.TryTranslateToNt4(upn) is { } translated)
        {
            return translated;
        }

        string suffix = upn[(upn.LastIndexOf('@') + 1)..];

        return domain.UpnSuffixes.TryGetValue(suffix, out string? nt4Domain) && !string.IsNullOrEmpty(nt4Domain)
            ? $"{nt4Domain}\\{upn[..upn.LastIndexOf('@')]}"
            : upn;
    }

    private static Regex CreateRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, OptionsChecks.RegexMatchTimeout);
}
