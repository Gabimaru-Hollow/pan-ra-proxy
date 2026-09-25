using System.Net;
using PanRaProxy.Firewall;
using PanRaProxy.Mappings;
using PanRaProxy.Radius;

namespace PanRaProxy.Diagnostics;

/// <summary>
/// Every log message the Proxy writes. Event IDs keep upstream's numbers (NFR-07) so existing
/// Event Log monitoring keeps matching; IDs marked "new" didn't exist upstream.
/// </summary>
internal static partial class Log
{
    // 2xxx: warnings

    [LoggerMessage(2001, LogLevel.Warning, "Firewall certificate validation is disabled (Firewalls:DisableCertificateValidation). The Firewall's TLS certificate is not checked.")]
    public static partial void CertificateValidationDisabled(ILogger logger);

    [LoggerMessage(2101, LogLevel.Warning, "Could not translate {Upn}: not found in the directory")]
    public static partial void NameNotFound(ILogger logger, string upn);

    [LoggerMessage(2102, LogLevel.Warning, "Could not translate {Upn}: domain {Domain} is unknown and is skipped for {Hours} hours")]
    public static partial void DomainNotFound(ILogger logger, string upn, string domain, double hours);

    [LoggerMessage(2199, LogLevel.Warning, "Could not translate {Upn}")]
    public static partial void NameTranslationFailed(ILogger logger, Exception exception, string upn);

    // 30xx: Firewall

    [LoggerMessage(3002, LogLevel.Error, "Firewall {Firewall} refused a Batch of {Logins} Logins and {Logouts} Logouts: {Message}")]
    public static partial void BatchRefused(ILogger logger, Uri firewall, int logins, int logouts, string message);

    [LoggerMessage(3004, LogLevel.Error, "No Firewall reachable; dropped a Batch of {Logins} Logins and {Logouts} Logouts. {Attempts}")]
    public static partial void NoFirewallReachable(ILogger logger, int logins, int logouts, string attempts);

    [LoggerMessage(3001, LogLevel.Error, "Submitting a Batch of {Count} failed; the Batch is dropped")]
    public static partial void BatchSubmissionFailed(ILogger logger, Exception exception, int count);

    [LoggerMessage(3005, LogLevel.Warning, "Firewall {Firewall} is unreachable: {Reason}")]
    public static partial void FirewallUnreachable(ILogger logger, Uri firewall, string reason);

    [LoggerMessage(3006, LogLevel.Warning, "Failed over to Firewall {Firewall}")]
    public static partial void FailedOver(ILogger logger, Uri firewall);

    [LoggerMessage(3007, LogLevel.Error, "Firewall rejected the Login for {Username} on {IpAddress}: {Message}")]
    public static partial void LoginRejected(ILogger logger, string username, string ipAddress, string message);

    [LoggerMessage(3008, LogLevel.Error, "Firewall rejected the Logout for {Username} on {IpAddress}: {Message}")]
    public static partial void LogoutRejected(ILogger logger, string username, string ipAddress, string message);

    // 31xx–32xx: accounting

    [LoggerMessage(3101, LogLevel.Warning, "Discarded a RADIUS packet from {Source}, which is not a configured RADIUS Client")]
    public static partial void UnknownRadiusClient(ILogger logger, IPAddress source);

    [LoggerMessage(3102, LogLevel.Debug, "Dropped {Request}: no usable Acct-Status-Type or User-Name")]
    public static partial void MissingAttributes(ILogger logger, AccountingRequest request);

    [LoggerMessage(3103, LogLevel.Critical, "The RADIUS accounting listener failed and the Proxy will exit")] // new
    public static partial void ListenerFailed(ILogger logger, Exception exception);

    [LoggerMessage(3104, LogLevel.Error, "Processing {Request} failed; the listener continues")] // new
    public static partial void RequestProcessingFailed(ILogger logger, Exception exception, AccountingRequest request);

    [LoggerMessage(3105, LogLevel.Error, "Handling a datagram from {Source} failed; the listener continues")] // new
    public static partial void DatagramHandlingFailed(ILogger logger, Exception exception, IPEndPoint source);

    [LoggerMessage(3106, LogLevel.Critical, "The Proxy's configuration is invalid and it will not start. Site settings: {SiteSettingsFile}{NewLine}{Failures}")] // new
    private static partial void InvalidConfiguration(ILogger logger, string siteSettingsFile, string newLine, string failures);

    public static void InvalidConfiguration(ILogger logger, string siteSettingsFile, string failures) =>
        InvalidConfiguration(logger, siteSettingsFile, Environment.NewLine, failures);

    [LoggerMessage(3202, LogLevel.Debug, "Discarded a RADIUS packet from {Source}: {Reason}")]
    public static partial void PacketDiscarded(ILogger logger, IPAddress source, DiscardReason reason);

    // 4xxx: information

    [LoggerMessage(4001, LogLevel.Debug, "Accepted {Request}")]
    public static partial void RequestAccepted(ILogger logger, AccountingRequest request);

    [LoggerMessage(4002, LogLevel.Information, "Firewall {Firewall} applied {Logins} Logins and {Logouts} Logouts ({Rejected} rejected)")]
    public static partial void BatchApplied(ILogger logger, Uri firewall, int logins, int logouts, int rejected);

    [LoggerMessage(4003, LogLevel.Debug, "Dropped {Request}: the User-Name matched the Username Filter")]
    public static partial void UsernameFiltered(ILogger logger, AccountingRequest request);

    [LoggerMessage(4004, LogLevel.Information, "Listening for RADIUS accounting on UDP {Port}")] // new
    public static partial void Listening(ILogger logger, int port);

    [LoggerMessage(4005, LogLevel.Debug, "Dropped {Request}: {Reason}")] // new
    public static partial void RequestDropped(ILogger logger, AccountingRequest request, DropReason reason);
}
