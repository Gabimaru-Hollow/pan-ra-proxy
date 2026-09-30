namespace PanRaProxy.Options;

/// <summary>
/// The Firewalls that receive Logins and Logouts, tried in order.
/// </summary>
public sealed class FirewallOptions
{
    public const string SectionName = "Firewalls";

    public List<Uri> Endpoints { get; set; } = [];

    public string? CaFile { get; set; }

    public bool DisableCertificateValidation { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}
