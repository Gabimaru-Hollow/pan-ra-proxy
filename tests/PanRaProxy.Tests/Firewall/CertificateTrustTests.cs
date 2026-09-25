using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PanRaProxy.Firewall;
using PanRaProxy.Options;

namespace PanRaProxy.Tests.Firewall;

public sealed class CertificateTrustTests : IDisposable
{
    private readonly X509Certificate2 labCa = CreateCa("CN=Lab CA");
    private readonly X509Certificate2 otherCa = CreateCa("CN=Other CA");
    private readonly string caFile = Path.Combine(Path.GetTempPath(), $"pan-ca-{Guid.NewGuid():N}.pem");

    public CertificateTrustTests()
    {
        File.WriteAllText(this.caFile, this.labCa.ExportCertificatePem());
    }

    public void Dispose()
    {
        File.Delete(this.caFile);
        this.labCa.Dispose();
        this.otherCa.Dispose();
    }

    [Fact]
    public void Default_uses_the_platform_trust_store()
    {
        Assert.Null(CertificateTrust.Create(new FirewallOptions()));
    }

    [Fact]
    public void Disabled_validation_accepts_anything()
    {
        RemoteCertificateValidationCallback callback = CertificateTrust.Create(new FirewallOptions { DisableCertificateValidation = true })!;

        Assert.True(callback(this, null, null, SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Certificate_issued_by_the_configured_ca_is_trusted()
    {
        using X509Certificate2 leaf = CreateLeaf(this.labCa, "fw-a.test");

        Assert.True(this.CaCallback()(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Certificate_issued_by_another_ca_is_rejected()
    {
        using X509Certificate2 leaf = CreateLeaf(this.otherCa, "fw-a.test");

        Assert.False(this.CaCallback()(this, leaf, null, SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Host_name_mismatch_is_rejected_even_with_the_configured_ca()
    {
        using X509Certificate2 leaf = CreateLeaf(this.labCa, "fw-a.test");

        Assert.False(this.CaCallback()(this, leaf, null, SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    [Fact]
    public void Missing_certificate_is_rejected()
    {
        Assert.False(this.CaCallback()(this, null, null, SslPolicyErrors.RemoteCertificateNotAvailable));
    }

    private RemoteCertificateValidationCallback CaCallback() =>
        CertificateTrust.Create(new FirewallOptions { CaFile = this.caFile })!;

    private static X509Certificate2 CreateCa(string subject)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new(subject, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
    }

    private static X509Certificate2 CreateLeaf(X509Certificate2 issuer, string host)
    {
        using RSA key = RSA.Create(2048);
        CertificateRequest request = new($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        SubjectAlternativeNameBuilder san = new();
        san.AddDnsName(host);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        byte[] serial = RandomNumberGenerator.GetBytes(8);
        return request.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(7), serial);
    }
}
