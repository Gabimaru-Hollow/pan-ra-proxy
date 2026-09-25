namespace PanRaProxy.Options;

/// <summary>
/// The Firewalls that receive Logins and Logouts, tried in order.
/// </summary>
public sealed class FirewallOptions
{
    public const string SectionName = "Firewalls";

    public List<Uri> Endpoints { get; set; } = [];

    /// <summary>
    /// Name of the secret holding the API key (NFR-01, <see cref="SecretStore"/>).
    /// </summary>
    public string ApiKeySecretName { get; set; } = "";

    public string? CaFile { get; set; }

    public bool DisableCertificateValidation { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}
