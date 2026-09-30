namespace PanRaProxy.Options;

/// <summary>
/// The Proxy's accounting listener and the RADIUS Clients allowed to send to it.
/// </summary>
public sealed class RadiusOptions
{
    public const string SectionName = "Radius";

    public int Port { get; set; } = 18131;

    public List<RadiusClientOptions> Clients { get; set; } = [];
}

/// <summary>
/// A RADIUS Client, identified by host name or IP. Every RADIUS Client shares the one RADIUS secret,
/// which is never in configuration (ADR 0007, <see cref="SecretStore"/>).
/// </summary>
public sealed class RadiusClientOptions
{
    public string Host { get; set; } = "";
}
