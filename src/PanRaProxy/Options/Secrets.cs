using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

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
/// The Proxy's two secrets (ADR 0007): one RADIUS shared secret for every RADIUS Client, and the Firewall API key.
/// </summary>
public static class SecretNames
{
    public const string Radius = "radius";

    public const string FirewallApiKey = "firewall-api-key";

    public static IReadOnlyList<string> All { get; } = [Radius, FirewallApiKey];

    /// <summary>
    /// Read when the secret has no file: for development and tests.
    /// </summary>
    public static string EnvironmentVariable(string name) => name switch
    {
        Radius => "PANRAPROXY_RADIUS_SECRET",
        FirewallApiKey => "PANRAPROXY_FIREWALL_API_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Not one of the Proxy's secrets."),
    };
}

/// <summary>
/// The secrets folder: one file per secret, written by <c>PanRaProxy --set-secret</c> as
/// <c>dpapi:v1:&lt;base64&gt;</c>, encrypted with DPAPI for this machine. A file without the prefix is
/// read as plaintext (as ADR 0003 wrote them) and reported, so it can be encrypted.
/// </summary>
public static class SecretStore
{
    private const string ProtectedPrefix = "dpapi:v1:";

    public static string? Read(string name) => Read(name, ProxyPaths.SecretsDirectory);

    /// <summary>
    /// The secret, or null when it isn't set. Throws <see cref="InvalidOperationException"/> when the file holds a
    /// value this machine can't decrypt: restored from another host, or the machine was reinstalled.
    /// </summary>
    public static string? Read(string name, string directory)
    {
        if (!SecretNames.All.Contains(name))
        {
            return null;
        }

        if (ReadFile(name, directory) is not { } stored)
        {
            string? environment = Environment.GetEnvironmentVariable(SecretNames.EnvironmentVariable(name));
            return string.IsNullOrEmpty(environment) ? null : environment;
        }

        return stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal) ? Unprotect(name, stored[ProtectedPrefix.Length..]) : stored;
    }

    /// <summary>
    /// True when the secret's file exists but isn't encrypted.
    /// </summary>
    public static bool IsStoredInPlaintext(string name, string directory) =>
        SecretNames.All.Contains(name) && ReadFile(name, directory) is { } stored && !stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Encrypts the value for this machine and replaces the secret's file in one step, so a reader never sees
    /// half a file. The folder and its ACL are the caller's.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static void Write(string name, string value, string directory)
    {
        if (!SecretNames.All.Contains(name))
        {
            throw new ArgumentOutOfRangeException(nameof(name), name, "Not one of the Proxy's secrets.");
        }

        byte[] blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.LocalMachine);
        string path = Path.Combine(directory, name);
        string temporary = path + ".tmp";

        File.WriteAllText(temporary, ProtectedPrefix + Convert.ToBase64String(blob), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

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

    private static string? ReadFile(string name, string directory)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path))
        {
            return null;
        }

        string value = File.ReadAllText(path).TrimEnd('\r', '\n');
        return value.Length > 0 ? value : null;
    }

    private static string Unprotect(string name, string blob)
    {
        string failure = $"The secret '{name}' can't be decrypted on this machine (restored from another host, or the machine was reinstalled). Set it again with PanRaProxy --set-secret {name}.";

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(failure);
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), null, DataProtectionScope.LocalMachine));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            throw new InvalidOperationException(failure, ex);
        }
    }
}
