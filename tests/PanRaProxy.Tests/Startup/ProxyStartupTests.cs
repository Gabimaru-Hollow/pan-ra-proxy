using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using PanRaProxy.Diagnostics;
using PanRaProxy.Firewall;
using PanRaProxy.Options;
using PanRaProxy.Startup;

namespace PanRaProxy.Tests.Startup;

/// <summary>
/// What an administrator meets first: the switches, the exit codes (docs/install.md), and a console run
/// that uses what the installation created but creates nothing on the machine.
/// </summary>
public sealed class ProxyStartupTests : IDisposable
{
    private static readonly string[] ValidSettings =
    [
        "--Radius:Clients:0:Host=10.0.0.10",
        "--Firewalls:Endpoints:0=https://fw-a.test/api/",
    ];

    private readonly string directory = Directory.CreateTempSubdirectory("panraproxy-startup-").FullName;
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();
    private readonly List<string> protectedDirectories = [];
    private int restarts;

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    private string SecretsDirectory => Path.Combine(this.directory, "secrets");

    /// <summary>
    /// A console on a machine where every effect is recorded instead of applied: no ACL change, no service restart.
    /// </summary>
    private StartupEnvironment Console(string? eventLogProblem = null, string? typedSecret = "typed-secret") => new(
        this.output,
        this.error,
        SiteSettingsFile: Path.Combine(this.directory, "no-site-settings.json"),
        IsWindowsService: false,
        EventLogProblem: () => eventLogProblem,
        ReplaceServices: services => services.Replace(ServiceDescriptor.Singleton(new SecretLookup(name => SecretNames.All.Contains(name) ? "lab" : null))),
        SecretsDirectory: this.SecretsDirectory,
        ReadSecretValue: _ => typedSecret,
        ProtectSecretsDirectory: path =>
        {
            Directory.CreateDirectory(path);
            this.protectedDirectories.Add(path);
            return null;
        },
        RestartService: () =>
        {
            this.restarts++;
            return "The PanRaProxy service was restarted.";
        });

    private string[] Args(params string[] args) =>
        [.. args, $"--Logging:File:Directory={Path.Combine(this.directory, "logs")}"];

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    public void Help_prints_the_usage_and_exits_0(string help)
    {
        Assert.Equal(0, ProxyStartup.Run([help], this.Console()));
        Assert.Contains("--check-config", this.output.ToString());
    }

    [Fact]
    public void Version_prints_the_version_and_exits_0()
    {
        Assert.Equal(0, ProxyStartup.Run(["--version"], this.Console()));
        Assert.Matches(@"^\d+\.\d+\.\d+", this.output.ToString());
    }

    [Theory]
    [InlineData("--chek-config")]
    [InlineData("-x")]
    [InlineData("--verbose")]
    public void An_unknown_switch_exits_2_instead_of_starting(string unknown)
    {
        Assert.Equal(2, ProxyStartup.Run([unknown], this.Console()));
        Assert.Contains(unknown, this.error.ToString());
    }

    [Theory]
    [InlineData("--Radius:Port=1812")]
    [InlineData("--Radius:Port", "1812")]
    [InlineData("/Radius:Port=1812")]
    public void Settings_on_the_command_line_are_not_switches(params string[] args)
    {
        CommandLine command = CommandLine.Parse(args);

        Assert.Null(command.Error);
        Assert.Equal(args, command.SettingsArgs);
    }

    [Theory]
    [InlineData(SecretNames.Radius)]
    [InlineData(SecretNames.FirewallApiKey)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")] // DPAPI
    public void Set_secret_encrypts_the_value_protects_the_folder_and_restarts_the_service(string name)
    {
        Assert.Equal(0, ProxyStartup.Run(["--set-secret", name], this.Console(typedSecret: "s3cr3t-value")));

        string onDisk = File.ReadAllText(Path.Combine(this.SecretsDirectory, name));
        Assert.StartsWith("dpapi:v1:", onDisk);
        Assert.Equal("s3cr3t-value", SecretStore.Read(name, this.SecretsDirectory));
        Assert.Equal([this.SecretsDirectory], this.protectedDirectories);
        Assert.Equal(1, this.restarts);
        Assert.DoesNotContain("s3cr3t-value", this.output.ToString() + this.error.ToString());
    }

    [Theory]
    [InlineData("--set-secret")]
    [InlineData("--set-secret", "RADIUS_SECRET_NPS1")]
    [InlineData("--set-secret", "--debug")]
    public void Set_secret_without_a_known_name_exits_2(params string[] args)
    {
        Assert.Equal(2, ProxyStartup.Run(args, this.Console()));
        Assert.Contains("radius", this.error.ToString());
        Assert.False(Directory.Exists(this.SecretsDirectory));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Set_secret_with_no_value_changes_nothing(string? typed)
    {
        Assert.Equal(1, ProxyStartup.Run(["--set-secret", SecretNames.Radius], this.Console(typedSecret: typed)));
        Assert.False(File.Exists(Path.Combine(this.SecretsDirectory, SecretNames.Radius)));
        Assert.Equal(0, this.restarts);
    }

    [Fact]
    public void Dry_run_is_a_flag_that_combines_with_check_config()
    {
        Assert.True(CommandLine.Parse(["--dry-run"]).DryRun);
        Assert.Equal(StartupMode.Run, CommandLine.Parse(["--dry-run"]).Mode);
        Assert.Equal(StartupMode.CheckConfig, CommandLine.Parse(["--check-config", "--dry-run"]).Mode);
        Assert.False(CommandLine.Parse(["--check-config"]).DryRun);
    }

    [Fact]
    public void A_dry_run_needs_only_the_radius_side()
    {
        // The production trial: NPS forwards to the Proxy, nothing reaches a Firewall. No endpoint, no API key.
        StartupEnvironment radiusOnly = this.Console() with
        {
            ReplaceServices = services => services.Replace(ServiceDescriptor.Singleton(new SecretLookup(name => name == SecretNames.Radius ? "lab" : null))),
        };

        Assert.Equal(1, ProxyStartup.Run(this.Args("--check-config", "--Radius:Clients:0:Host=10.0.0.10"), radiusOnly));
        Assert.Equal(0, ProxyStartup.Run(this.Args("--check-config", "--dry-run", "--Radius:Clients:0:Host=10.0.0.10"), radiusOnly));
    }

    [Fact]
    public void A_dry_run_has_no_firewall_client_at_all()
    {
        using IHost host = ProxyStartup.BuildHost(CommandLine.Parse(this.Args("--dry-run", "--Radius:Clients:0:Host=10.0.0.10")), this.Console());

        Assert.IsType<DryRunSubmitter>(host.Services.GetRequiredService<IBatchSubmitter>());
        Assert.Null(host.Services.GetService<FirewallClient>());
    }

    [Fact]
    public void Check_config_reports_a_valid_configuration_and_exits_0()
    {
        Assert.Equal(0, ProxyStartup.Run(this.Args(["--check-config", .. ValidSettings]), this.Console()));
        Assert.Contains("The configuration is valid", this.output.ToString());
    }

    [Fact]
    public void Check_config_reports_an_invalid_configuration_and_exits_1()
    {
        Assert.Equal(1, ProxyStartup.Run(this.Args("--check-config"), this.Console()));
        Assert.Contains("The configuration is invalid", this.output.ToString());
    }

    [Fact]
    public void Site_settings_that_are_not_valid_json_exit_1_with_the_reason()
    {
        // The most likely mistake after editing the site settings by hand: a missing comma.
        string site = Path.Combine(this.directory, "appsettings.json");
        File.WriteAllText(site, "{ \"Radius\": { \"Port\": 18131 } \"UserId\": {} }");

        int code = ProxyStartup.Run(this.Args(["--check-config", .. ValidSettings]), this.Console() with { SiteSettingsFile = site });

        Assert.Equal(1, code);
        Assert.Contains(site, this.error.ToString());
    }

    [Fact]
    public void A_value_of_the_wrong_type_is_reported_as_invalid_configuration()
    {
        Assert.Equal(1, ProxyStartup.Run(this.Args(["--check-config", "--Radius:Port=abc", .. ValidSettings]), this.Console()));
        Assert.Contains("The configuration is invalid", this.output.ToString());
    }

    [Fact]
    public void Check_config_writes_no_log_file()
    {
        ProxyStartup.Run(this.Args(["--check-config", .. ValidSettings]), this.Console());

        Assert.False(Directory.Exists(Path.Combine(this.directory, "logs")));
    }

    [Fact]
    public void A_console_run_without_the_event_log_source_does_not_use_the_event_log()
    {
        // The Event Log provider would register a missing source in the registry on its first entry.
        using IHost host = ProxyStartup.BuildHost(CommandLine.Parse(this.Args(ValidSettings)), this.Console("the MSI creates it"));

        Assert.DoesNotContain(host.Services.GetServices<ILoggerProvider>(), p => p is EventLogLoggerProvider);
    }

    [Fact]
    public void A_console_run_with_the_event_log_source_uses_it()
    {
        using IHost host = ProxyStartup.BuildHost(CommandLine.Parse(this.Args(ValidSettings)), this.Console());

        Assert.Contains(host.Services.GetServices<ILoggerProvider>(), p => p is EventLogLoggerProvider);
    }

    [Fact]
    public void A_console_run_does_not_create_the_logs_folder()
    {
        using IHost host = ProxyStartup.BuildHost(CommandLine.Parse(this.Args(ValidSettings)), this.Console());

        Assert.DoesNotContain(host.Services.GetServices<ILoggerProvider>(), p => p is FileLoggerProvider);
        Assert.False(Directory.Exists(Path.Combine(this.directory, "logs")));
    }

    [Fact]
    public void A_console_run_writes_to_a_logs_folder_the_installation_created()
    {
        Directory.CreateDirectory(Path.Combine(this.directory, "logs"));

        using IHost host = ProxyStartup.BuildHost(CommandLine.Parse(this.Args(ValidSettings)), this.Console());

        Assert.Contains(host.Services.GetServices<ILoggerProvider>(), p => p is FileLoggerProvider);
    }
}
