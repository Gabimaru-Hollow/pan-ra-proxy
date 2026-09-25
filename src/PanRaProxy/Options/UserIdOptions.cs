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

    /// <summary>
    /// Replaces only the part of the username that <see cref="Match"/> found, like <c>Regex.Replace</c>:
    /// <c>@domain\.local$</c> with an empty Replace strips the suffix. A domain rule's
    /// <see cref="DomainRuleOptions.Replace"/> builds the whole name instead.
    /// </summary>
    public string Replace { get; set; } = "";
}
