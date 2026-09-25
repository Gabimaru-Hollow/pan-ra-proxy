using Microsoft.Extensions.Logging;
using PanRaProxy.Diagnostics;

namespace PanRaProxy.Tests.Diagnostics;

public sealed class FileLoggingTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("panraproxy-log-").FullName;

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    private string[] Files() => Directory.GetFiles(this.directory, "test-*.log").OrderBy(f => f).ToArray();

    private FileLoggerProvider Provider(long fileSizeLimitBytes = 1024 * 1024, int retainedFiles = 5) =>
        new(new FileLoggingOptions { Directory = this.directory, FileNamePrefix = "test", RetainedFiles = retainedFiles }, fileSizeLimitBytes);

    [Fact]
    public void Every_line_carries_the_time_level_event_id_and_category()
    {
        using (FileLoggerProvider provider = this.Provider())
        {
            ILogger logger = provider.CreateLogger("PanRaProxy.Firewall.BatchSender");
            logger.LogInformation(new EventId(4002), "Firewall applied {Logins} Logins", 2);
            logger.LogError(new EventId(3007), new InvalidOperationException("boom"), "rejected");
        }

        string[] lines = File.ReadAllText(this.Files().Single()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("INF 4002 PanRaProxy.Firewall.BatchSender | Firewall applied 2 Logins", lines[0]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", lines[0]);
        Assert.Contains("ERR 3007", lines[1]);
        Assert.Contains("System.InvalidOperationException: boom", string.Join("\n", lines));
    }

    [Fact]
    public void Files_roll_at_the_size_limit_and_only_the_newest_are_kept()
    {
        using (FileLoggerProvider provider = this.Provider(fileSizeLimitBytes: 200, retainedFiles: 3))
        {
            ILogger logger = provider.CreateLogger("test");
            for (int i = 0; i < 40; i++)
            {
                logger.LogInformation("{Line}", new string('x', 100));
            }
        }

        string[] files = this.Files();

        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 400, $"{f} grew past the limit"));
    }

    [Fact]
    public void A_log_file_that_cannot_be_opened_does_not_stop_the_proxy()
    {
        // A directory under the name the file would take: opening it for writing is denied.
        Directory.CreateDirectory(Path.Combine(this.directory, $"test-{DateTime.Now:yyyyMMdd}.log"));

        using FileLoggerProvider provider = this.Provider();
        ILogger logger = provider.CreateLogger("test");

        logger.LogInformation("lost, and nothing else happens");
        logger.LogError(new InvalidOperationException("boom"), "lost too");
    }

    [Fact]
    public void A_directory_that_cannot_be_created_does_not_stop_the_proxy()
    {
        FileLoggingOptions options = new() { Directory = Path.Combine(this.directory, "file.txt", "logs") };
        File.WriteAllText(Path.Combine(this.directory, "file.txt"), "not a directory");

        using FileLoggerProvider provider = new(Microsoft.Extensions.Options.Options.Create(options));

        provider.CreateLogger("test").LogInformation("still alive");
    }

    [Fact]
    public void Disabled_file_logging_writes_nothing()
    {
        FileLoggingOptions options = new() { Enabled = false, Directory = this.directory };

        using FileLoggerProvider provider = new(Microsoft.Extensions.Options.Options.Create(options));
        provider.CreateLogger("test").LogInformation("nothing here");

        Assert.Empty(Directory.GetFiles(this.directory));
    }
}
