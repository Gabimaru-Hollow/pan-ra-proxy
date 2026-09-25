namespace PanRaProxy.Options;

/// <summary>
/// How accounting becomes Logins and Logouts, and how they are grouped into Batches.
/// </summary>
public sealed class UserIdOptions
{
    public const string SectionName = "UserId";

    /// <summary>
    /// Timeout carried by every Login. Validated against <see cref="InterimIntervalMinutes"/>: a Mapping
    /// survives a missed Interim-Update only if the Timeout covers at least two intervals.
    /// </summary>
    public int TimeoutMinutes { get; set; } = 15;

    /// <summary>
    /// How often the Controller sends Interim-Updates. The Proxy doesn't control this: it's the NAS
    /// setting, declared here so the Timeout can be checked against it at startup.
    /// </summary>
    public int InterimIntervalMinutes { get; set; } = 5;

    public bool LogoutOnStop { get; set; }

    /// <summary>
    /// Applied first, before <see cref="Domain"/>: an escape hatch for usernames the domain rules can't handle.
    /// </summary>
    public List<UsernameRewriteOptions> UsernameRewrites { get; set; } = [];

    /// <summary>
    /// How a received username becomes the Canonical Username.
    /// </summary>
    public DomainOptions Domain { get; set; } = new();

    public bool NameTranslation { get; set; }

    /// <summary>
    /// Matched against the raw User-Name, before any Username Rewrite. The default drops Machine Accounts.
    /// </summary>
    public string UsernameFilter { get; set; } = @"(\$$|^host/)";

    public int BatchSize { get; set; } = 200;

    /// <summary>
    /// Hard timer: starts at the first packet of a Batch and doesn't restart (ADR 0001).
    /// </summary>
    public int BatchWindowMs { get; set; } = 50;

    public int QueueCapacity { get; set; } = 10_000;
}

public sealed class UsernameRewriteOptions
{
    public string Match { get; set; } = "";

    public string Replace { get; set; } = "";
}

/// <summary>
/// The domain rules that turn the three username forms seen on the wire (bare, UPN, NT4) into the
/// Canonical Username. All optional: a form with no rule configured is passed through unchanged.
/// </summary>
public sealed class DomainOptions
{
    /// <summary>
    /// NT4 domain given to a bare username (no '\' and no '@'), e.g. "XDOMAIN".
    /// </summary>
    public string? DefaultNt4Domain { get; set; }

    /// <summary>
    /// UPN suffix assumed for a bare username when Name Translation is on, e.g. "xdomain.local".
    /// Without it, a bare username can't be looked up in the directory.
    /// </summary>
    public string? DefaultUpnSuffix { get; set; }

    /// <summary>
    /// UPN suffix to NT4 domain, e.g. {"xdomain.local": "XDOMAIN", "example.com": "XDOMAIN"}.
    /// Used when Name Translation is off or fails. It assumes the UPN prefix equals the account name;
    /// where that isn't true, only Name Translation gives the right Canonical Username.
    /// </summary>
    public Dictionary<string, string> UpnSuffixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
