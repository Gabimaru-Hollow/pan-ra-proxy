namespace PanRaProxy.Mappings;

/// <summary>
/// Remembers what the directory answered. Interim-Updates repeat the same usernames every few
/// minutes, so without this every one of them would be another lookup. Failures are cached too,
/// for a shorter time, so an account the directory doesn't know isn't asked for on every packet.
/// </summary>
public sealed class CachingNameTranslator(INameTranslator inner, TimeProvider time, TimeSpan lifetime) : INameTranslator
{
    private static readonly TimeSpan FailureLifetime = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, (string? Result, DateTimeOffset Until)> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object gate = new();

    public string? TryTranslateToNt4(string upn)
    {
        DateTimeOffset now = time.GetUtcNow();

        lock (this.gate)
        {
            if (this.cache.TryGetValue(upn, out (string? Result, DateTimeOffset Until) cached) && cached.Until > now)
            {
                return cached.Result;
            }
        }

        string? translated = inner.TryTranslateToNt4(upn);

        lock (this.gate)
        {
            this.cache[upn] = (translated, now + (translated is null ? FailureLifetime : lifetime));
        }

        return translated;
    }
}
