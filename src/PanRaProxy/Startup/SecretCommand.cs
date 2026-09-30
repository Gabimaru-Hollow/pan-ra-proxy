using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using PanRaProxy.Options;

namespace PanRaProxy.Startup;

/// <summary>
/// <c>PanRaProxy --set-secret radius|firewall-api-key</c> (ADR 0007): the one command that writes to the
/// machine, and only because the administrator runs it. It reads the value, encrypts it for this machine,
/// stores it in the protected secrets folder and restarts the service so it reads the new value.
/// </summary>
internal static class SecretCommand
{
    public static int Run(string name, StartupEnvironment environment)
    {
        if (!OperatingSystem.IsWindows())
        {
            environment.Error.WriteLine("Secrets are encrypted with DPAPI, which needs Windows.");
            return 1;
        }

        string? value = environment.ReadSecretValue(name);
        if (string.IsNullOrEmpty(value))
        {
            environment.Error.WriteLine($"No value given: the secret '{name}' is unchanged.");
            return 1;
        }

        string path = Path.Combine(environment.SecretsDirectory, name);
        try
        {
            if (environment.ProtectSecretsDirectory(environment.SecretsDirectory) is { } note)
            {
                environment.Output.WriteLine(note);
            }

            SecretStore.Write(name, value, environment.SecretsDirectory);
        }
        catch (UnauthorizedAccessException)
        {
            environment.Error.WriteLine($"Can't write {path}: run PanRaProxy --set-secret {name} from an elevated prompt.");
            return 1;
        }
        catch (Exception ex) when (ex is IOException or CryptographicException)
        {
            environment.Error.WriteLine($"Can't store the secret '{name}': {ex.Message}");
            return 1;
        }

        environment.Output.WriteLine($"The secret '{name}' is stored in {path}, encrypted for this machine.");

        if (environment.RestartService() is { } restart)
        {
            environment.Output.WriteLine(restart);
        }

        return 0;
    }

    /// <summary>
    /// The value from standard input when it's redirected (automation), otherwise typed twice without echo:
    /// a mistyped RADIUS secret would only show as discarded packets.
    /// </summary>
    public static string? ReadFromConsole(string name)
    {
        if (Console.IsInputRedirected)
        {
            return Console.In.ReadToEnd().TrimEnd('\r', '\n');
        }

        string first = ReadHidden($"Value for '{name}': ");
        string second = ReadHidden("Again, to confirm: ");

        if (first != second)
        {
            Console.Error.WriteLine("The two values differ.");
            return null;
        }

        return first;
    }

    /// <summary>
    /// Creates the secrets folder if needed and sets its ACL: not inherited from ProgramData, full control for
    /// SYSTEM and Administrators, read for the service account. Returns a note when the service account doesn't
    /// exist yet (the service isn't installed: the MSI grants it on install).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? ProtectDirectory(string directory)
    {
        DirectoryInfo folder = Directory.CreateDirectory(directory);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

        Allow(security, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl);
        Allow(security, new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl);

        string? note = null;
        try
        {
            IdentityReference service = new NTAccount("NT SERVICE", "PanRaProxy").Translate(typeof(SecurityIdentifier));
            Allow(security, service, FileSystemRights.ReadAndExecute);
        }
        catch (IdentityNotMappedException)
        {
            note = "The PanRaProxy service isn't installed: the folder is readable by SYSTEM and Administrators only until it is.";
        }

        folder.SetAccessControl(security);
        return note;
    }

    /// <summary>
    /// Restarts the service when it's running, since the Proxy reads its secrets only at startup.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static string? RestartServiceIfRunning()
    {
        try
        {
            using ServiceController service = new("PanRaProxy");
            if (service.Status != ServiceControllerStatus.Running)
            {
                return "The PanRaProxy service isn't running: it reads the secret when it starts.";
            }

            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            return "The PanRaProxy service was restarted and uses the new value.";
        }
        catch (InvalidOperationException)
        {
            return "The PanRaProxy service isn't installed: it reads the secret when it starts.";
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return "The PanRaProxy service didn't restart within 30 s: check it with 'sc query PanRaProxy'.";
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Allow(DirectorySecurity security, IdentityReference identity, FileSystemRights rights) =>
        security.AddAccessRule(new FileSystemAccessRule(
            identity,
            rights,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

    private static string ReadHidden(string prompt)
    {
        // The prompt goes to stderr, so stdout stays clean for scripts.
        Console.Error.Write(prompt);
        StringBuilder value = new();

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (value.Length > 0)
                {
                    value.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                value.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return value.ToString();
    }
}
