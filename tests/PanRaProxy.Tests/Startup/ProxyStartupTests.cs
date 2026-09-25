using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using PanRaProxy.Diagnostics;
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
        "--Radius:Clients:0:SecretName=RADIUS_SECRET",
        "--Firewalls:Endpoints:0=https://fw-a.test/api/",
    ];

    private readonly string directory = Directory.CreateTempSubdirectory("panraproxy-startup-").FullName;
    private readonly StringWriter output = new();
    private readonly StringWriter error = new();

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    private StartupEnvironment Console(string? eventLogProblem = null) => new(
        this.output,
        this.error,
        SiteSettingsFile: Path.Combine(this.directory, "no-site-settings.json"),
        IsWindowsService: false,
        EventLogProblem: () => eventLogProblem,
        ReplaceServices: services => services.Replace(ServiceDescriptor.Singleton(new SecretLookup(name => name is "RADIUS_SECRET" or "PAN_API_KEY" ? "lab" : null))));

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
