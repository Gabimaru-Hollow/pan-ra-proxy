namespace PanRaProxy.Firewall;

/// <summary>
/// What happened to one Batch.
/// </summary>
public abstract record SubmissionResult
{
    private SubmissionResult()
    {
    }

    /// <summary>
    /// A Firewall processed the Batch. Every Login and Logout was applied except those in <see cref="Rejections"/>.
    /// </summary>
    public sealed record Accepted(Uri Firewall, IReadOnlyList<Rejection> Rejections) : SubmissionResult;

    /// <summary>
    /// A Firewall answered but refused the whole Batch (e.g. invalid credential, malformed request).
    /// No failover: the next Firewall would refuse it the same way. The Batch is dropped (P3-4).
    /// </summary>
    public sealed record ApiError(Uri Firewall, string Message) : SubmissionResult;

    /// <summary>
    /// No Firewall could be reached: connection or TLS failure, timeout, or HTTP 5xx on every one.
    /// </summary>
    public sealed record Unreachable(IReadOnlyList<FailedAttempt> Attempts) : SubmissionResult;

    /// <summary>
    /// A dry run (<c>--dry-run</c>): the Batch was logged and deliberately not sent to any Firewall.
    /// </summary>
    public sealed record NotSent : SubmissionResult;
}

/// <summary>
/// One Login or Logout the Firewall refused, as it reported it.
/// </summary>
public sealed record Rejection(RejectionKind Kind, string Username, string IpAddress, string Message);

public enum RejectionKind
{
    Login,
    Logout,
}

public sealed record FailedAttempt(Uri Firewall, string Reason);
