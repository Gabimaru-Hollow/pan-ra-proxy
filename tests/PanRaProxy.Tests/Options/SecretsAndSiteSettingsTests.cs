using Microsoft.Extensions.Configuration;
using PanRaProxy.Options;

namespace PanRaProxy.Tests.Options;

[System.Runtime.Versioning.SupportedOSPlatform("windows")] // DPAPI
public sealed class SecretsAndSiteSettingsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("panraproxy-").FullName;

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    [Fact]
    public void A_plaintext_secret_file_is_read_without_its_trailing_newline()
    {
        File.WriteAllText(Path.Combine(this.directory, SecretNames.Radius), "s3cr3t with spaces \r\n");

        Assert.Equal("s3cr3t with spaces ", SecretStore.Read(SecretNames.Radius, this.directory));
        Assert.True(SecretStore.IsStoredInPlaintext(SecretNames.Radius, this.directory));
    }

    [Fact]
    public void A_written_secret_is_encrypted_on_disk_and_read_back()
    {
        SecretStore.Write(SecretNames.FirewallApiKey, "LUFRPT1-api-key", this.directory);

        string onDisk = File.ReadAllText(Path.Combine(this.directory, SecretNames.FirewallApiKey));
        Assert.StartsWith("dpapi:v1:", onDisk);
        Assert.DoesNotContain("LUFRPT1", onDisk);
        Assert.False(SecretStore.IsStoredInPlaintext(SecretNames.FirewallApiKey, this.directory));
        Assert.Equal("LUFRPT1-api-key", SecretStore.Read(SecretNames.FirewallApiKey, this.directory));
    }

    [Fact]
    public void Writing_again_replaces_the_secret_and_leaves_no_temporary_file()
    {
        SecretStore.Write(SecretNames.Radius, "first", this.directory);
        SecretStore.Write(SecretNames.Radius, "second", this.directory);

        Assert.Equal("second", SecretStore.Read(SecretNames.Radius, this.directory));
        Assert.Equal([SecretNames.Radius], Directory.GetFiles(this.directory).Select(Path.GetFileName));
    }

    [Fact]
    public void A_secret_this_machine_cannot_decrypt_says_how_to_fix_it()
    {
        File.WriteAllText(Path.Combine(this.directory, SecretNames.Radius), "dpapi:v1:AQAAAG5vdCBhIHJlYWwgYmxvYg==");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SecretStore.Read(SecretNames.Radius, this.directory));
        Assert.Contains("--set-secret radius", ex.Message);
    }

    [Fact]
    public void Without_a_file_the_environment_variable_is_used()
    {
        // The two fixed names map to fixed variables; only this test sets the RADIUS one.
        Environment.SetEnvironmentVariable("PANRAPROXY_RADIUS_SECRET", "from-env");
        try
        {
            Assert.Equal("from-env", SecretStore.Read(SecretNames.Radius, this.directory));

            File.WriteAllText(Path.Combine(this.directory, SecretNames.Radius), "from-file");
            Assert.Equal("from-file", SecretStore.Read(SecretNames.Radius, this.directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PANRAPROXY_RADIUS_SECRET", null);
        }
    }

    [Theory]
    [InlineData("RADIUS_SECRET_NPS1")] // an ADR 0003 name: no longer read
    [InlineData(@"..\secrets\x")]
    [InlineData("")]
    public void Only_the_two_known_secrets_are_read(string name)
    {
        File.WriteAllText(Path.Combine(this.directory, "RADIUS_SECRET_NPS1"), "old");

        Assert.Null(SecretStore.Read(name, this.directory));
    }

    [Fact]
    public void An_empty_file_is_not_set()
    {
        File.WriteAllText(Path.Combine(this.directory, SecretNames.Radius), "\r\n");

        Assert.Null(SecretStore.Read(SecretNames.Radius, this.directory));
    }

    [Fact]
    public void Site_settings_override_defaults_and_environment_overrides_site()
    {
        string defaults = Path.Combine(this.directory, "appsettings.json");
        string site = Path.Combine(this.directory, "site.json");
        File.WriteAllText(defaults, """{ "Radius": { "Port": 18131 }, "UserId": { "TimeoutMinutes": 75, "BatchSize": 200 } }""");
        File.WriteAllText(site, """{ "Radius": { "Port": 18999 }, "UserId": { "TimeoutMinutes": 90 } }""");

        ConfigurationManager configuration = new();
        configuration.AddJsonFile(defaults);
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["UserId:TimeoutMinutes"] = "120" }); // stands in for env vars / command line
        configuration.AddSiteSettings(site);

        Assert.Equal("18999", configuration["Radius:Port"]);         // site over defaults
        Assert.Equal("200", configuration["UserId:BatchSize"]);      // defaults kept
        Assert.Equal("120", configuration["UserId:TimeoutMinutes"]); // env / command line over site
    }

    [Fact]
    public void Missing_site_settings_file_is_optional()
    {
        ConfigurationManager configuration = new();
        configuration.AddSiteSettings(Path.Combine(this.directory, "absent.json"));

        Assert.Null(configuration["Radius:Port"]);
    }
}
