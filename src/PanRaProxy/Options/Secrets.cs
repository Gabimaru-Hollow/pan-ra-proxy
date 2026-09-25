using System.Text.RegularExpressions;

namespace PanRaProxy.Options;

/// <summary>
/// Looks up a secret by name, or returns null when it isn't set. Production uses <see cref="SecretStore"/>;
/// tests pass a dictionary lookup.
/// </summary>
public delegate string? SecretLookup(string name);

/// <summary>
/// Where the Proxy keeps its site configuration and secrets, outside the install folder so upgrades never touch them.
/// </summary>
public static class ProxyPaths
{
    /// <summary><c>%ProgramData%\PanRaProxy</c>.</summary>
    public static string DataDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PanRaProxy");

    /// <summary>Site configuration, loaded after the defaults in the install folder.</summary>
    public static string SiteSettingsFile { get; } = Path.Combine(DataDirectory, "appsettings.json");

    /// <summary>One file per secret, readable only by the service account and Administrators (NFR-01).</summary>
    public static string SecretsDirectory { get; } = Path.Combine(DataDirectory, "secrets");

    /// <summary>Rolling log files; the MSI creates the folder.</summary>
    public static string LogsDirectory { get; } = Path.Combine(DataDirectory, "logs");
}

/// <summary>
/// Secrets provided after installation: a file named after the secret in the secrets folder
/// (written by <c>Set-PanRaProxySecret.ps1</c>), falling back to an environment variable of the same name.
/// </summary>
public static partial class SecretStore
{
    /// <summary>
    /// Secret names are file names: letters, digits, <c>_</c>, <c>-</c> and <c>.</c>, not starting with a dot.
    /// </summary>
    public static bool IsValidName(string name) => ValidName().IsMatch(name);

    public static string? Read(string name) => Read(name, ProxyPaths.SecretsDirectory);

    /// <summary>
    /// True when the folder exists but this session can't list it (its ACL admits only the service
    /// account and Administrators): every secret in it would then look unset.
    /// </summary>
    public static bool IsUnreadable(string directory)
    {
        try
        {
            _ = Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any();
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static string? Read(string name, string directory)
    {
        if (!IsValidName(name))
        {
            return null;
        }

        string path = Path.Combine(directory, name);
        if (File.Exists(path))
        {
            string value = File.ReadAllText(path).TrimEnd('\r', '\n');
            return value.Length > 0 ? value : null;
        }

        string? environment = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(environment) ? null : environment;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_\-][A-Za-z0-9_.\-]{0,127}$")]
    private static partial Regex ValidName();
}
