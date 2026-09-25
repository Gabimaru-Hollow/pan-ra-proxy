using Microsoft.Extensions.Configuration;
using PanRaProxy.Options;

namespace PanRaProxy.Tests.Options;

public sealed class SecretsAndSiteSettingsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("panraproxy-").FullName;

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    [Fact]
    public void Secret_file_is_read_without_its_trailing_newline()
    {
        File.WriteAllText(Path.Combine(this.directory, "RADIUS_SECRET_NPS1"), "s3cr3t with spaces \r\n");

        Assert.Equal("s3cr3t with spaces ", SecretStore.Read("RADIUS_SECRET_NPS1", this.directory));
    }

    [Fact]
    public void Missing_file_falls_back_to_an_environment_variable()
    {
        string name = $"PANRAPROXY_TEST_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            Assert.Equal("from-env", SecretStore.Read(name, this.directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void File_wins_over_environment_variable()
    {
        string name = $"PANRAPROXY_TEST_{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(this.directory, name), "from-file");
        Environment.SetEnvironmentVariable(name, "from-env");
        try
        {
            Assert.Equal("from-file", SecretStore.Read(name, this.directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Empty_file_and_unknown_name_are_not_set()
    {
        File.WriteAllText(Path.Combine(this.directory, "EMPTY"), "\r\n");

        Assert.Null(SecretStore.Read("EMPTY", this.directory));
        Assert.Null(SecretStore.Read($"UNKNOWN_{Guid.NewGuid():N}", this.directory));
    }

    [Theory]
    [InlineData(@"..\secrets\x")]
    [InlineData("../x")]
    [InlineData(@"C:\Windows\win.ini")]
    [InlineData(".hidden")]
    [InlineData("")]
    [InlineData("a b")]
    public void Secret_names_cannot_escape_the_secrets_folder(string name)
    {
        Assert.False(SecretStore.IsValidName(name));
        Assert.Null(SecretStore.Read(name, this.directory));
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
