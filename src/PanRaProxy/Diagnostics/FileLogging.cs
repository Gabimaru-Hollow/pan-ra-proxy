using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Configuration;
using Microsoft.Extensions.Options;
using PanRaProxy.Options;

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

    /// <summary>A new file is started once the current one reaches this size.</summary>
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

[ProviderAlias("File")]
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly FileLogWriter? writer;


    public FileLoggerProvider(IOptions<FileLoggingOptions> options)
    {
        FileLoggingOptions settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        string directory = string.IsNullOrWhiteSpace(settings.Directory)
            ? Path.Combine(ProxyPaths.DataDirectory, "logs")
            : settings.Directory;

        try
        {
            this.writer = new FileLogWriter(directory, settings.FileNamePrefix,
                Math.Max(1, settings.MaxFileSizeMb) * 1024L * 1024L, settings.RetainedFiles);
        }
        catch (Exception ex)
        {
            // Never stop the Proxy because it can't write its log; the console and Event Log remain.
            Console.Error.WriteLine($"File logging is off: {ex.Message}");
        }
    }

    /// <summary>Used by the tests to drive the writer directly.</summary>
    internal FileLoggerProvider(FileLogWriter writer) => this.writer = writer;

    public ILogger CreateLogger(string categoryName) =>
        this.writer is null ? NullLogger.Instance : new FileLogger(categoryName, this.writer);

    public void Dispose() => this.writer?.Dispose();

    private sealed class NullLogger : ILogger
    {
        public static readonly NullLogger Instance = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
        }
    }

    private sealed class FileLogger(string category, FileLogWriter writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!this.IsEnabled(logLevel))
            {
                return;
            }

            StringBuilder line = new StringBuilder(256)
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(' ').Append(Level(logLevel))
                .Append(' ').Append(eventId.Id.ToString(CultureInfo.InvariantCulture).PadLeft(4))
                .Append(' ').Append(category)
                .Append(" | ").Append(formatter(state, exception));

            if (exception is not null)
            {
                line.Append(Environment.NewLine).Append(exception);
            }

            writer.Write(line.ToString());
        }

        private static string Level(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            LogLevel.Critical => "CRT",
            _ => "???",
        };
    }
}

/// <summary>
/// Writes lines on one background thread, rolls the file by size and keeps the newest files.
/// Writing never blocks the caller: when the queue is full, lines are dropped and counted.
/// </summary>
internal sealed class FileLogWriter : IDisposable
{
    private const int QueueCapacity = 10_000;

    private readonly BlockingCollection<string> queue = new(QueueCapacity);
    private readonly Thread thread;
    private readonly string directory;
    private readonly string prefix;
    private readonly long maxBytes;
    private readonly int retained;
    private StreamWriter? file;
    private long written;
    private long dropped;

    public FileLogWriter(string directory, string prefix, long maxBytes, int retainedFiles)
    {
        this.directory = directory;
        this.prefix = prefix;
        this.maxBytes = Math.Max(1, maxBytes);
        this.retained = Math.Max(1, retainedFiles);

        System.IO.Directory.CreateDirectory(directory);
        this.Open();

        this.thread = new Thread(this.Run) { IsBackground = true, Name = "PanRaProxy file log" };
        this.thread.Start();
    }

    public string CurrentFile { get; private set; } = "";

    public void Write(string line)
    {
        if (!this.queue.TryAdd(line))
        {
            Interlocked.Increment(ref this.dropped);
        }
    }

    public void Dispose()
    {
        this.queue.CompleteAdding();
        this.thread.Join(TimeSpan.FromSeconds(5));
        this.file?.Dispose();
    }

    private void Run()
    {
        foreach (string line in this.queue.GetConsumingEnumerable())
        {
            try
            {
                long dropped = Interlocked.Exchange(ref this.dropped, 0);
                if (dropped > 0)
                {
                    this.WriteLine($"[{dropped} log lines dropped: the file log queue was full]");
                }

                this.WriteLine(line);
            }
            catch (IOException)
            {
                // A locked or full disk must not take the Proxy down.
            }
        }

        this.file?.Flush();
    }

    private void WriteLine(string line)
    {
        if (this.file is null)
        {
            return;
        }

        this.file.WriteLine(line);
        this.file.Flush();
        this.written += line.Length + Environment.NewLine.Length;

        if (this.written >= this.maxBytes)
        {
            this.file.Dispose();
            this.Open();
        }
    }

    private void Open()
    {
        // Several rolls can land in the same second, so the name gets a counter when it's taken.
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string path = Path.Combine(this.directory, $"{this.prefix}-{stamp}.log");
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(this.directory, $"{this.prefix}-{stamp}-{i}.log");
        }

        this.CurrentFile = path;
        // UTF-8 without a BOM: log files are read with grep and tail, not with an editor.
        this.file = new StreamWriter(this.CurrentFile, append: true, new UTF8Encoding(false));
        this.written = 0;
        this.Prune();
    }

    private void Prune()
    {
        try
        {
            foreach (FileInfo old in new DirectoryInfo(this.directory)
                         .GetFiles($"{this.prefix}-*.log")
                         .OrderByDescending(f => f.CreationTimeUtc)
                         .Skip(this.retained))
            {
                old.Delete();
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
