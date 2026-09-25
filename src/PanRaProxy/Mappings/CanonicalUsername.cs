using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Mappings;

/// <summary>
/// Turns a received User-Name into the Canonical Username: the Username Rewrites first (an escape
/// hatch), then the first matching domain rule. A username no rule matches is passed through unchanged.
/// </summary>
public sealed class CanonicalUsernameResolver(CanonicalUsernameRules rules, INameTranslator directory)
{
    /// <summary>
    /// May ask the directory (Name Translation), so it can block: never call it on the listener's path.
    /// </summary>
    public string Resolve(string rawUsername)
    {
        string username = rawUsername;

        foreach ((Regex match, string replace) in rules.Rewrites)
        {
            username = match.Replace(username, replace);
        }

        foreach (DomainRule rule in rules.DomainRules)
        {
            if (rule.Apply(username, directory) is { } canonical)
            {
                return canonical;
            }
        }

        return username;
    }
}

/// <summary>
/// The Username Rewrites and domain rules, compiled. Valid by construction: <see cref="TryCreate"/> is
/// the only definition of what a valid rule is, used both to validate the settings and to run them.
/// </summary>
public sealed class CanonicalUsernameRules
{
    private CanonicalUsernameRules((Regex Match, string Replace)[] rewrites, DomainRule[] domainRules)
    {
        this.Rewrites = rewrites;
        this.DomainRules = domainRules;
    }

    internal IReadOnlyList<(Regex Match, string Replace)> Rewrites { get; }

    internal IReadOnlyList<DomainRule> DomainRules { get; }

    /// <summary>
    /// Whether any rule asks the directory, which needs Name Translation.
    /// </summary>
    public bool UseLookup => this.DomainRules.Any(r => r.Lookup is not null);

    /// <summary>
    /// Throws when the settings aren't valid; startup validation has reported why before this runs.
    /// </summary>
    public static CanonicalUsernameRules Create(UserIdOptions options)
    {
        List<string> failures = [];
        return TryCreate(options, failures) ?? throw new InvalidOperationException(string.Join(" ", failures));
    }

    /// <summary>
    /// The compiled rules, or null with every reason they can't be built added to <paramref name="failures"/>.
    /// </summary>
    public static CanonicalUsernameRules? TryCreate(UserIdOptions options, List<string> failures)
    {
        int before = failures.Count;
        List<(Regex, string)> rewrites = [];
        List<DomainRule> domainRules = [];

        for (int i = 0; i < options.UsernameRewrites.Count; i++)
        {
            UsernameRewriteOptions rewrite = options.UsernameRewrites[i];
            string key = $"UserId:UsernameRewrites:{i}";

            if (string.IsNullOrEmpty(rewrite.Match))
            {
                failures.Add($"{key}:Match is required.");
            }
            else if (OptionsChecks.TryCreateRegex(rewrite.Match, $"{key}:Match", failures, out Regex? match))
            {
                rewrites.Add((match, rewrite.Replace));
            }
        }

        for (int i = 0; i < options.Domain.Rules.Count; i++)
        {
            if (DomainRule.TryCreate(options.Domain.Rules[i], $"UserId:Domain:Rules:{i}", failures) is { } rule)
            {
                domainRules.Add(rule);
            }
        }

        return failures.Count == before ? new CanonicalUsernameRules([.. rewrites], [.. domainRules]) : null;
    }
}

/// <summary>
/// One domain rule: what it needs to be valid, and what it does with a username.
/// </summary>
internal sealed record DomainRule(Regex Match, string? Nt4Domain, string? Replace, string? Lookup)
{
    public static DomainRule? TryCreate(DomainRuleOptions options, string key, List<string> failures)
    {
        if (string.IsNullOrEmpty(options.Match))
        {
            failures.Add($"{key}:Match is required.");
            return null;
        }

        if (!OptionsChecks.TryCreateRegex(options.Match, $"{key}:Match", failures, out Regex? match))
        {
            return null;
        }

        int before = failures.Count;

        if (options.Nt4Domain is null && options.Replace is null && options.Lookup is null)
        {
            failures.Add($"{key} must set Nt4Domain, Replace or Lookup.");
        }

        foreach ((string name, string? template) in new[] { ("Nt4Domain", options.Nt4Domain), ("Replace", options.Replace), ("Lookup", options.Lookup) })
        {
            if (template is "")
            {
                failures.Add($"{key}:{name} must not be empty: remove it, or set a template such as ${{user}}.");
            }
        }

        if (options.Nt4Domain is not null && !match.GetGroupNames().Contains("user"))
        {
            failures.Add($"{key}:Nt4Domain needs a 'user' group in Match, e.g. ^(?<user>[^@\\]+)$.");
        }

        if (options.Lookup is not null && !OperatingSystem.IsWindows())
        {
            failures.Add($"{key}:Lookup asks the directory, which needs a Windows host.");
        }

        return failures.Count == before ? new DomainRule(match, options.Nt4Domain, options.Replace, options.Lookup) : null;
    }

    /// <summary>
    /// The Canonical Username, or null when the rule doesn't match. A Lookup that fails falls back to
    /// Nt4Domain or Replace, and without either leaves the username as it is.
    /// </summary>
    public string? Apply(string username, INameTranslator directory)
    {
        Match match = this.Match.Match(username);
        if (!match.Success)
        {
            return null;
        }

        if (this.Lookup is { } lookup && directory.TryTranslateToNt4(match.Result(lookup)) is { } translated)
        {
            return translated;
        }

        if (this.Nt4Domain is { } domain)
        {
            return $"{domain}\\{match.Result("${user}")}";
        }

        return this.Replace is { } replacement ? match.Result(replacement) : username;
    }
}

/// <summary>
/// Reports what <see cref="CanonicalUsernameRules.TryCreate"/> rejects, with the other UserId settings (event 3106).
/// </summary>
internal sealed class CanonicalUsernameRulesValidator : IValidateOptions<UserIdOptions>
{
    public ValidateOptionsResult Validate(string? name, UserIdOptions options)
    {
        List<string> failures = [];
        CanonicalUsernameRules.TryCreate(options, failures);
        return OptionsChecks.Result(failures);
    }
}
