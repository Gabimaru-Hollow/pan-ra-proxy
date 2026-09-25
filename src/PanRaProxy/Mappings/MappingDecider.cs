using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;
using PanRaProxy.Radius;

namespace PanRaProxy.Mappings;

/// <summary>
/// Decides what an accepted Accounting-Request means for Mappings: Username Filter on the raw
/// User-Name, Username Rewrite and the domain rules to the Canonical Username, Placeholder IP
/// removal, Login with Timeout on Start/Interim-Update, Logout-on-Stop.
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
    private readonly DomainRule[] domainRules;
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

        this.domainRules = this.options.Domain.Rules
            .Select(r => new DomainRule(CreateRegex(r.Match), r.Nt4Domain, r.Replace, r.Lookup))
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
    /// Username Rewrite rules first (an escape hatch), then the first matching domain rule.
    /// A username no rule matches is passed through unchanged.
    /// </summary>
    private string ToCanonicalUsername(string rawUsername)
    {
        string username = rawUsername;

        foreach ((Regex match, string replace) in this.usernameRewrites)
        {
            username = match.Replace(username, replace);
        }

        foreach (DomainRule rule in this.domainRules)
        {
            Match match = rule.Match.Match(username);
            if (!match.Success)
            {
                continue;
            }

            if (rule.Lookup is { } lookup && this.nameTranslator.TryTranslateToNt4(match.Result(lookup)) is { } translated)
            {
                return translated;
            }

            if (rule.Nt4Domain is { } domain)
            {
                return $"{domain}\\{match.Result("${user}")}";
            }

            return rule.Replace is { } replacement ? match.Result(replacement) : username;
        }

        return username;
    }

    private static Regex CreateRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, OptionsChecks.RegexMatchTimeout);

    private sealed record DomainRule(Regex Match, string? Nt4Domain, string? Replace, string? Lookup);
}
