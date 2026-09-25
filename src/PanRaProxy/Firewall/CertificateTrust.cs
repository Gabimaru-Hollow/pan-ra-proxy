using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PanRaProxy.Options;

namespace PanRaProxy.Firewall;

/// <summary>
/// How the Proxy decides whether to trust a Firewall's TLS certificate (NFR-02).
/// Scoped to the Firewall HttpClient only, never process-wide.
/// </summary>
public static class CertificateTrust
{
    /// <summary>
    /// Null means the platform default: the certificate must chain to a root in the OS trust store.
    /// </summary>
    public static RemoteCertificateValidationCallback? Create(FirewallOptions options)
    {
        if (options.DisableCertificateValidation)
        {
            return (_, _, _, _) => true;
        }

        if (string.IsNullOrEmpty(options.CaFile))
        {
            return null;
        }

        X509Certificate2Collection roots = LoadCertificates(options.CaFile);
        return (_, certificate, chain, errors) => IsTrustedByRoots(certificate, chain, errors, roots);
    }

    /// <summary>
    /// Accepts the certificate only when its host name matches and it chains to one of <paramref name="roots"/>.
    /// </summary>
    internal static bool IsTrustedByRoots(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors, X509Certificate2Collection roots)
    {
        if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None)
        {
            return false; // no certificate, or a host name mismatch
        }

        using X509Chain customChain = new();
        customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        customChain.ChainPolicy.CustomTrustStore.AddRange(roots);
        customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;

        if (chain is not null)
        {
            // Intermediates the Firewall sent along with its certificate.
            foreach (X509ChainElement element in chain.ChainElements.Skip(1))
            {
                customChain.ChainPolicy.ExtraStore.Add(element.Certificate);
            }
        }

        using X509Certificate2 leaf = new(certificate);
        return customChain.Build(leaf);
    }

    private static X509Certificate2Collection LoadCertificates(string path)
    {
        X509Certificate2Collection certificates = [];

        try
        {
            string text = File.ReadAllText(path);

            if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal))
            {
                certificates.ImportFromPem(text);
            }
            else
            {
                certificates.Add(new X509Certificate2(path)); // DER
            }
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // Unreadable for the service account, or not a PEM or DER certificate.
            throw new InvalidOperationException($"Firewalls:CaFile '{path}' can't be loaded: {ex.Message}", ex);
        }

        if (certificates.Count == 0)
        {
            throw new InvalidOperationException($"Firewalls:CaFile '{path}' contains no certificate.");
        }

        return certificates;
    }
}
