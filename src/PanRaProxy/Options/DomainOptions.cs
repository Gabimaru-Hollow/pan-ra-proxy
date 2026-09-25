namespace PanRaProxy.Options;

/// <summary>
/// How a received username becomes the Canonical Username. One ordered list of rules covers every
/// form seen on the wire: bare names, UPNs of any suffix, and names already in NT4 form.
/// </summary>
public sealed class DomainOptions
{
    /// <summary>
    /// Tried in order; the first whose <see cref="DomainRuleOptions.Match"/> matches decides.
    /// A username matching no rule is passed through unchanged.
    /// </summary>
    public List<DomainRuleOptions> Rules { get; set; } = [];

    /// <summary>
    /// How long a successful directory lookup is reused. Interim-Updates repeat the same usernames
    /// every few minutes, so without a cache each one would hit the directory again.
    /// </summary>
    public int LookupCacheMinutes { get; set; } = 480;
}

/// <summary>
/// One rule: a pattern, and what to do with the names it matches.
/// </summary>
/// <remarks>
/// <para>
/// Templates use the regex's groups, e.g. <c>${user}</c> or <c>$1</c>.
/// </para>
/// <para>
/// <see cref="Lookup"/> asks the directory (Name Translation, Windows only) for the NT4 name; when it
/// fails, <see cref="Nt4Domain"/> or <see cref="Replace"/> is used if present, otherwise the username
/// is left as it is. Use it for the accounts where the naming convention doesn't hold.
/// </para>
/// </remarks>
public sealed class DomainRuleOptions
{
    public string Match { get; set; } = "";

    /// <summary>
    /// Shorthand for <c>Replace = "DOMAIN\${user}"</c>: requires a <c>user</c> group in <see cref="Match"/>.
    /// </summary>
    public string? Nt4Domain { get; set; }

    /// <summary>
    /// The Canonical Username to build from the match, e.g. <c>XDOMAIN\${user}</c>.
    /// </summary>
    public string? Replace { get; set; }

    /// <summary>
    /// The UPN to look up in the directory, e.g. <c>${user}@xdomain.local</c>.
    /// </summary>
    public string? Lookup { get; set; }
}
