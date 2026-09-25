using Microsoft.Extensions.Logging;
using PanRaProxy.Diagnostics;

namespace PanRaProxy.Tests.Diagnostics;

public sealed class FileLoggingTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("panraproxy-log-").FullName;

    public void Dispose() => Directory.Delete(this.directory, recursive: true);

    private string[] Files() => Directory.GetFiles(this.directory, "test-*.log").OrderBy(f => f).ToArray();

    /// <summary>Reads a log the writer still holds open, the way tail would.</summary>
    private static string ReadShared(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static void WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 100 && !condition(); i++)
        {
            Thread.Sleep(20);
        }

        Assert.True(condition(), "the file log did not catch up within 2 s");
    }

    [Fact]
    public void Every_line_carries_the_time_level_event_id_and_category()
    {
        using (FileLogWriter writer = new(this.directory, "test", 1024 * 1024, 5))
        {
            ILogger logger = new FileLoggerProvider(writer).CreateLogger("PanRaProxy.Firewall.BatchSender");
            logger.Log(LogLevel.Information, new EventId(4002), "state", null, (_, _) => "Firewall applied 2 Logins");
            logger.Log(LogLevel.Error, new EventId(3007), "state", new InvalidOperationException("boom"), (_, _) => "rejected");

            WaitFor(() => ReadShared(writer.CurrentFile).Contains("rejected"));
        }

        string[] lines = ReadShared(this.Files().Single()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("INF 4002 PanRaProxy.Firewall.BatchSender | Firewall applied 2 Logins", lines[0]);
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3} ", lines[0]);
        Assert.Contains("ERR 3007", lines[1]);
        Assert.Contains("System.InvalidOperationException: boom", string.Join("\n", lines));
    }

    [Fact]
    public void Files_roll_at_the_size_limit_and_only_the_newest_are_kept()
    {
        using (FileLogWriter writer = new(this.directory, "test", maxBytes: 200, retainedFiles: 3))
        {
            for (int i = 0; i < 40; i++)
            {
                writer.Write(new string('x', 100));
            }

            WaitFor(() => this.Files().Length >= 3);
        }

        string[] files = this.Files();

        Assert.Equal(3, files.Length);
        Assert.All(files, f => Assert.True(new FileInfo(f).Length <= 400, $"{f} grew past the limit"));
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
