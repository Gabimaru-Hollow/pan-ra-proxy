using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PanRaProxy.Diagnostics;

namespace PanRaProxy.Mappings;

/// <summary>
/// Name Translation: a directory lookup from a UPN to its NT4 name, for users whose UPN prefix
/// differs from their account name. Off by default (UserId:NameTranslation).
/// </summary>
public interface INameTranslator
{
    /// <summary>
    /// The NT4 name for <paramref name="upn"/>, or null when it can't be translated.
    /// </summary>
    string? TryTranslateToNt4(string upn);
}

/// <summary>
/// Used when Name Translation is off: never translates.
/// </summary>
public sealed class NoNameTranslation : INameTranslator
{
    public string? TryTranslateToNt4(string upn) => null;
}

/// <summary>
/// Name Translation through secur32 <c>TranslateName</c>, ported from upstream. A domain the directory
/// doesn't know is skipped for 8 hours so it doesn't flood the directory or the log.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsNameTranslator(TimeProvider time, ILogger<WindowsNameTranslator> logger) : INameTranslator
{
    private const int NameSamCompatible = 2;
    private const int NameUserPrincipal = 8;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoSuchUser = 1317;
    private const int ErrorNoSuchDomain = 1355;

    private static readonly TimeSpan UnknownDomainSkip = TimeSpan.FromHours(8);

    private readonly Dictionary<string, DateTimeOffset> unknownDomains = new(StringComparer.OrdinalIgnoreCase);
    private readonly object unknownDomainsLock = new();

    public string? TryTranslateToNt4(string upn)
    {
        string domain = upn[(upn.LastIndexOf('@') + 1)..];

        lock (this.unknownDomainsLock)
        {
            if (this.unknownDomains.TryGetValue(domain, out DateTimeOffset until) && until > time.GetUtcNow())
            {
                return null;
            }
        }

        try
        {
            return Translate(upn);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoSuchUser)
        {
            Log.NameNotFound(logger, upn);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorNoSuchDomain)
        {
            lock (this.unknownDomainsLock)
            {
                this.unknownDomains[domain] = time.GetUtcNow() + UnknownDomainSkip;
            }

            Log.DomainNotFound(logger, upn, domain, UnknownDomainSkip.TotalHours);
        }
        catch (Win32Exception ex)
        {
            Log.NameTranslationFailed(logger, ex, upn);
        }

        return null;
    }

    private static string Translate(string upn)
    {
        char[] buffer = new char[256];
        int size = buffer.Length;

        if (!TranslateNameW(upn, NameUserPrincipal, NameSamCompatible, buffer, ref size))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error);
            }

            buffer = new char[size];
            if (!TranslateNameW(upn, NameUserPrincipal, NameSamCompatible, buffer, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }

        // size includes the terminating null.
        return new string(buffer, 0, Math.Max(0, size - 1));
    }

    [DllImport("secur32.dll", EntryPoint = "TranslateNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool TranslateNameW(string accountName, int accountNameFormat, int desiredNameFormat, [Out] char[] translatedName, ref int size);
}
