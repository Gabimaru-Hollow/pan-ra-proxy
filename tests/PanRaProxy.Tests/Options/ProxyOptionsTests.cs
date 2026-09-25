using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;

namespace PanRaProxy.Tests.Options;

public class ProxyOptionsTests
{
    private static readonly Dictionary<string, string?> ValidConfig = new()
    {
        ["Radius:Clients:0:Host"] = "10.0.0.10",
        ["Radius:Clients:0:SecretName"] = "RADIUS_SECRET",
        ["Firewalls:Endpoints:0"] = "https://fw-a.test/api/",
        ["Firewalls:Endpoints:1"] = "https://fw-b.test/api/",
        ["Firewalls:ApiKeySecretName"] = "PAN_API_KEY",
    };

    private static readonly Dictionary<string, string> ValidEnvironment = new()
    {
        ["RADIUS_SECRET"] = "lab-secret",
        ["PAN_API_KEY"] = "lab-key",
    };

    private static ServiceProvider BuildProvider(
        Dictionary<string, string?>? overrides = null,
        Dictionary<string, string>? environment = null)
    {
        Dictionary<string, string?> config = new(ValidConfig);
        foreach (KeyValuePair<string, string?> pair in overrides ?? [])
        {
            config[pair.Key] = pair.Value;
        }

        Dictionary<string, string> env = environment ?? ValidEnvironment;

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(config).Build();

        ServiceCollection services = new();
        services.AddSingleton<SecretLookup>(name => env.GetValueOrDefault(name));
        services.AddProxyOptions(configuration);
        return services.BuildServiceProvider();
    }

    private static OptionsValidationException AssertInvalid<T>(ServiceProvider provider)
        where T : class =>
        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<T>>().Value);

    [Fact]
    public void Minimal_config_binds_with_spec_defaults()
    {
        using ServiceProvider provider = BuildProvider();

        RadiusOptions radius = provider.GetRequiredService<IOptions<RadiusOptions>>().Value;
        UserIdOptions userId = provider.GetRequiredService<IOptions<UserIdOptions>>().Value;
        FirewallOptions firewalls = provider.GetRequiredService<IOptions<FirewallOptions>>().Value;

        Assert.Equal(18131, radius.Port);
        Assert.Equal("10.0.0.10", Assert.Single(radius.Clients).Host);

        Assert.Equal(15, userId.TimeoutMinutes);
        Assert.Equal(5, userId.InterimIntervalMinutes);
        Assert.False(userId.LogoutOnStop);
        Assert.Equal(@"(\$$|^host/)", userId.UsernameFilter);
        Assert.Equal(200, userId.BatchSize);
        Assert.Equal(50, userId.BatchWindowMs);
        Assert.Equal(10_000, userId.QueueCapacity);

        Assert.Equal(2, firewalls.Endpoints.Count);
        Assert.False(firewalls.DisableCertificateValidation);
        Assert.Equal(10, firewalls.TimeoutSeconds);
    }

    [Fact]
    public void Missing_radius_secret_in_environment_is_rejected()
    {
        using ServiceProvider provider = BuildProvider(environment: new() { ["PAN_API_KEY"] = "lab-key" });

        OptionsValidationException ex = AssertInvalid<RadiusOptions>(provider);
        Assert.Contains(ex.Failures, f => f.Contains("RADIUS_SECRET"));
    }

    [Fact]
    public void Missing_api_key_in_environment_is_rejected()
    {
        using ServiceProvider provider = BuildProvider(environment: new() { ["RADIUS_SECRET"] = "lab-secret" });

        OptionsValidationException ex = AssertInvalid<FirewallOptions>(provider);
        Assert.Contains(ex.Failures, f => f.Contains("PAN_API_KEY"));
    }

    [Fact]
    public void No_radius_clients_is_rejected()
    {
        using ServiceProvider provider = BuildProvider(new()
        {
            ["Radius:Clients:0:Host"] = null,
            ["Radius:Clients:0:SecretName"] = null,
        });

        AssertInvalid<RadiusOptions>(provider);
    }

    [Fact]
    public void Duplicate_radius_client_host_is_rejected()
    {
        using ServiceProvider provider = BuildProvider(new()
        {
            ["Radius:Clients:1:Host"] = "10.0.0.10",
            ["Radius:Clients:1:SecretName"] = "RADIUS_SECRET",
        });

        OptionsValidationException ex = AssertInvalid<RadiusOptions>(provider);
        Assert.Contains(ex.Failures, f => f.Contains("more than once"));
    }

    [Theory]
    [InlineData("Radius:Port", "0")]
    [InlineData("Radius:Port", "70000")]
    public void Out_of_range_port_is_rejected(string key, string value)
    {
        using ServiceProvider provider = BuildProvider(new() { [key] = value });

        AssertInvalid<RadiusOptions>(provider);
    }

    [Theory]
    [InlineData("UserId:Domain:Rules:0:Match", "(unclosed")]
    [InlineData("UserId:Domain:LookupCacheMinutes", "0")]
    [InlineData("UserId:InterimIntervalMinutes", "9")]
    [InlineData("UserId:UsernameFilter", "(unclosed")]
    [InlineData("UserId:UsernameRewrites:0:Match", "[bad")]
    [InlineData("UserId:TimeoutMinutes", "0")]
    [InlineData("UserId:BatchSize", "0")]
    [InlineData("UserId:BatchWindowMs", "-1")]
    [InlineData("UserId:QueueCapacity", "10")]
    public void Invalid_user_id_setting_is_rejected(string key, string value)
    {
        using ServiceProvider provider = BuildProvider(new() { [key] = value });

        AssertInvalid<UserIdOptions>(provider);
    }

    [Fact]
    public void Domain_rule_without_an_action_is_rejected()
    {
        using ServiceProvider provider = BuildProvider(new() { ["UserId:Domain:Rules:0:Match"] = "^(?<user>.+)$" });

        OptionsValidationException ex = AssertInvalid<UserIdOptions>(provider);
        Assert.Contains(ex.Failures, f => f.Contains("must set Nt4Domain, Replace or Lookup"));
    }

    [Fact]
    public void Nt4Domain_without_a_user_group_is_rejected()
    {
        using ServiceProvider provider = BuildProvider(new()
        {
            ["UserId:Domain:Rules:0:Match"] = "^.+$",
            ["UserId:Domain:Rules:0:Nt4Domain"] = "XDOMAIN",
        });

        OptionsValidationException ex = AssertInvalid<UserIdOptions>(provider);
        Assert.Contains(ex.Failures, f => f.Contains("needs a 'user' group"));
    }

    [Fact]
    public void Empty_username_filter_means_no_filter_and_is_valid()
    {
        using ServiceProvider provider = BuildProvider(new() { ["UserId:UsernameFilter"] = "" });

        Assert.Equal("", provider.GetRequiredService<IOptions<UserIdOptions>>().Value.UsernameFilter);
    }

    [Theory]
    [InlineData("http://fw-a.test/api/")]
    [InlineData("/api/")]
    public void Non_https_firewall_endpoint_is_rejected(string endpoint)
    {
        using ServiceProvider provider = BuildProvider(new() { ["Firewalls:Endpoints:0"] = endpoint });

        AssertInvalid<FirewallOptions>(provider);
    }

    [Fact]
    public void Missing_ca_file_is_rejected()
    {
        using ServiceProvider provider = BuildProvider(new() { ["Firewalls:CaFile"] = @"Z:\does-not-exist\pan-ca.pem" });

        AssertInvalid<FirewallOptions>(provider);
    }

    [Fact]
    public async Task Host_refuses_to_start_with_invalid_options()
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(ValidConfig)
        {
            ["UserId:UsernameFilter"] = "(unclosed",
        });
        builder.Services.AddSingleton<SecretLookup>(name => ValidEnvironment.GetValueOrDefault(name));
        builder.Services.AddProxyOptions(builder.Configuration);

        using IHost host = builder.Build();

        await Assert.ThrowsAnyAsync<OptionsValidationException>(() => host.StartAsync());
    }

    [Fact]
    public void Startup_validation_reports_every_failure_at_once()
    {
        CollectingLoggerProvider logs = new();
        ServiceCollection services = new();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddSingleton(new SecretLookup(_ => null));
        services.AddProxyOptions(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UserId:UsernameFilter"] = "(unclosed",
        }).Build());
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.False(StartupValidation.Validate(provider));

        (int id, string message) = Assert.Single(logs.Entries);
        Assert.Equal(3106, id);
        Assert.Contains("Radius:Clients must list at least one RADIUS Client.", message);
        Assert.Contains("UserId:UsernameFilter is not a valid regular expression", message);
        Assert.Contains("Firewalls:Endpoints must list at least one Firewall.", message);
        Assert.Contains("Firewalls:ApiKeySecretName is required.", message);
    }

    [Fact]
    public void Startup_validation_passes_a_valid_configuration()
    {
        using ServiceProvider provider = BuildProvider();

        Assert.True(StartupValidation.Validate(new LoggingProvider(provider)));
    }

    [Fact]
    public void Startup_validation_passes_the_whole_proxy_with_a_valid_configuration()
    {
        CollectingLoggerProvider logs = new();
        using ServiceProvider provider = BuildProxy([], logs);

        Assert.True(StartupValidation.Validate(provider));
        Assert.Empty(logs.Entries);
    }

    [Fact]
    public void Startup_validation_reports_a_radius_client_that_does_not_resolve()
    {
        CollectingLoggerProvider logs = new();
        using ServiceProvider provider = BuildProxy(
            new() { ["Radius:Clients:0:Host"] = "nps.invalid" },
            logs,
            _ => throw new SocketException((int)SocketError.HostNotFound));

        Assert.False(StartupValidation.Validate(provider));

        (int id, string message) = Assert.Single(logs.Entries);
        Assert.Equal(3106, id);
        Assert.Contains("RADIUS Client 'nps.invalid' did not resolve", message);
    }

    [Fact]
    public void Startup_validation_reports_a_ca_file_that_holds_no_certificate()
    {
        string caFile = Path.GetTempFileName();
        File.WriteAllText(caFile, "not a certificate");

        try
        {
            CollectingLoggerProvider logs = new();
            using ServiceProvider provider = BuildProxy(new() { ["Firewalls:CaFile"] = caFile }, logs);

            Assert.False(StartupValidation.Validate(provider));

            (int id, string message) = Assert.Single(logs.Entries);
            Assert.Equal(3106, id);
            Assert.Contains($"Firewalls:CaFile '{caFile}'", message);
        }
        finally
        {
            File.Delete(caFile);
        }
    }

    /// <summary>
    /// The graph that ships, with only the machine replaced: secrets, and name resolution when given.
    /// </summary>
    private static ServiceProvider BuildProxy(Dictionary<string, string?> overrides, CollectingLoggerProvider logs, Func<string, IPAddress[]>? resolve = null)
    {
        Dictionary<string, string?> config = new(ValidConfig);
        foreach (KeyValuePair<string, string?> pair in overrides)
        {
            config[pair.Key] = pair.Value;
        }

        ServiceCollection services = new();
        services.AddLogging(b => b.AddProvider(logs));
        services.AddPanRaProxy(new ConfigurationBuilder().AddInMemoryCollection(config).Build());
        services.Replace(ServiceDescriptor.Singleton(new SecretLookup(name => ValidEnvironment.GetValueOrDefault(name))));

        if (resolve is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(resolve));
        }

        return services.BuildServiceProvider();
    }

    private sealed class LoggingProvider(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ILoggerFactory) ? Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance : inner.GetService(serviceType);
    }

    private sealed class CollectingLoggerProvider : ILoggerProvider
    {
        public List<(int Id, string Message)> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Collecting(this.Entries);

        public void Dispose()
        {
        }

        private sealed class Collecting(List<(int Id, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                entries.Add((eventId.Id, formatter(state, exception)));
        }
    }
}
