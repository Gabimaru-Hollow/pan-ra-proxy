using System.Text;
using System.Xml.Linq;

namespace PanRaProxy.Tests;

/// <summary>
/// Keeps what a test's mock Firewall received, under <c>artifacts/tests</c> in the repository, so the
/// uid-messages a run produced can be read afterwards instead of only being asserted on.
/// </summary>
public static class TestArtifacts
{
    private static readonly Lazy<string> Root = new(() =>
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "PanRaProxy.sln")))
        {
            directory = directory.Parent;
        }

        string root = Path.Combine(directory?.FullName ?? AppContext.BaseDirectory, "artifacts", "tests");
        Directory.CreateDirectory(root);
        return root;
    });

    /// <summary>
    /// Writes the uid-messages, one per Batch, with a summary of the Logins and Logouts in each.
    /// Returns the file's path.
    /// </summary>
    public static string WriteUidMessages(string testName, IEnumerable<string> commands)
    {
        StringBuilder report = new();
        report.AppendLine($"# {testName}");
        report.AppendLine($"# {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        report.AppendLine();

        int batch = 0;
        foreach (string command in commands)
        {
            batch++;
            XElement message = XElement.Parse(command);
            string[] logins = Entries(message, "login");
            string[] logouts = Entries(message, "logout");

            report.AppendLine($"## Batch {batch}: {logins.Length} login, {logouts.Length} logout");
            foreach (string entry in logins.Concat(logouts))
            {
                report.AppendLine($"  {entry}");
            }

            report.AppendLine();
            report.AppendLine(message.ToString());
            report.AppendLine();
        }

        string path = Path.Combine(Root.Value, $"{testName}.uid-messages.log");
        File.WriteAllText(path, report.ToString());
        return path;
    }

    private static string[] Entries(XElement message, string kind) =>
        message.Descendants(kind).Elements("entry")
            .Select(e => $"{kind.ToUpperInvariant(),-6} {e.Attribute("name")?.Value,-32} {e.Attribute("ip")?.Value,-15} "
                         + (e.Attribute("timeout") is { } t ? $"timeout={t.Value}m" : ""))
            .ToArray();
}
