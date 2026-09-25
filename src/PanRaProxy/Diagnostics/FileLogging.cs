using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PanRaProxy.Diagnostics;

/// <summary>
/// Rolling log files, so the Proxy keeps its own detailed record whether it runs as a service or
/// from a console. Standard level filtering applies through <c>Logging:File:LogLevel</c>.
/// </summary>
public sealed class FileLoggingOptions
{
    /// <summary>Defaults to <c>%ProgramData%\PanRaProxy\logs</c>.</summary>
    public string? Directory { get; set; }

    public string FileNamePrefix { get; set; } = "panraproxy";

    /// <summary>A new file is started once the current one reaches this size, and every day.</summary>
    public int MaxFileSizeMb { get; set; } = 16;

    /// <summary>Older files beyond this count are deleted, newest kept.</summary>
    public int RetainedFiles { get; set; } = 14;

    /// <summary>false turns file logging off entirely.</summary>
    public bool Enabled { get; set; } = true;
}

public static class FileLoggingRegistration
{
    /// <summary>
    /// Adds the file provider, bound to the <c>Logging:File</c> section.
    /// </summary>
    public static ILoggingBuilder AddProxyFileLog(this ILoggingBuilder logging)
    {
        logging.Services.TryAddEnumerable(ServiceDescriptor.Singleton<ILoggerProvider, FileLoggerProvider>());
        LoggerProviderOptions.RegisterProviderOptions<FileLoggingOptions, FileLoggerProvider>(logging.Services);
        return logging;
    }
}

/// <summary>
/// The file log, written by Serilog's file sink behind <c>ILogger</c> (ADR 0004): the code only ever sees
/// <c>ILogger</c>, and this is the one place that knows Serilog. The alias keeps the settings under
/// <c>Logging:File</c>. Serilog never throws into the caller: a file that can't be written is reported
/// to Serilog's SelfLog and the Proxy goes on.
/// </summary>
[ProviderAlias("File")]
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const string OutputTemplate =
        "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3} {EventNumber,4} {SourceContext} | {Message:lj}{NewLine}{Exception}";

    private readonly SerilogLoggerProvider? serilog;

    public FileLoggerProvider(IOptions<FileLoggingOptions> options)
        : this(options.Value, Math.Max(1, options.Value.MaxFileSizeMb) * 1024L * 1024L)
    {
    }

    /// <summary>Used by the tests, to roll files after a few hundred bytes.</summary>
    internal FileLoggerProvider(FileLoggingOptions settings, long fileSizeLimitBytes)
    {
        if (!settings.Enabled)
        {
            return;
        }

        string directory = string.IsNullOrWhiteSpace(settings.Directory)
            ? Path.Combine(ProxyPaths.DataDirectory, "logs")
            : settings.Directory;

        try
        {
            // Created here so a folder that can't exist is reported at startup, not silently on the first line.
            System.IO.Directory.CreateDirectory(directory);
        }
        catch (Exception ex)
        {
            // Never stop the Proxy because it can't write its log; the console and Event Log remain.
            Console.Error.WriteLine($"File logging is off: {ex.Message}");
            return;
        }

        Serilog.Core.Logger logger = new LoggerConfiguration()
            .MinimumLevel.Verbose() // Logging:File:LogLevel filters before Serilog sees an event.
            .Enrich.With<EventNumberEnricher>()
            .WriteTo.File(
                Path.Combine(directory, $"{settings.FileNamePrefix}-.log"),
                outputTemplate: OutputTemplate,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: Math.Max(1, fileSizeLimitBytes),
                retainedFileCountLimit: Math.Max(1, settings.RetainedFiles))
            .CreateLogger();

        this.serilog = new SerilogLoggerProvider(logger, dispose: true);
    }

    public ILogger CreateLogger(string categoryName) =>
        this.serilog?.CreateLogger(categoryName) ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

    public void Dispose() => this.serilog?.Dispose();

    /// <summary>
    /// The <c>ILogger</c> event ID as a plain number for the output template; 0 when there is none.
    /// </summary>
    private sealed class EventNumberEnricher : ILogEventEnricher
    {
        public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
        {
            int id = logEvent.Properties.TryGetValue("EventId", out LogEventPropertyValue? value)
                     && value is StructureValue structure
                     && structure.Properties.FirstOrDefault(p => p.Name == "Id")?.Value is ScalarValue { Value: int number }
                ? number
                : 0;

            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("EventNumber", id));
        }
    }
}
