using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace PanRaProxy.Options;

internal sealed class RadiusOptionsValidator(SecretLookup secrets) : IValidateOptions<RadiusOptions>
{
    public ValidateOptionsResult Validate(string? name, RadiusOptions options)
    {
        List<string> failures = [];

        if (options.Port is < 1 or > 65535)
        {
            failures.Add($"Radius:Port must be between 1 and 65535 (was {options.Port}).");
        }

        if (options.Clients.Count == 0)
        {
            failures.Add("Radius:Clients must list at least one RADIUS Client.");
        }

        for (int i = 0; i < options.Clients.Count; i++)
        {
            RadiusClientOptions client = options.Clients[i];

            if (string.IsNullOrWhiteSpace(client.Host))
            {
                failures.Add($"Radius:Clients:{i}:Host is required.");
            }

            OptionsChecks.RequireSecret(secrets, client.SecretName, $"Radius:Clients:{i}:SecretName", failures);
        }

        foreach (string duplicate in options.Clients
                     .Where(c => !string.IsNullOrWhiteSpace(c.Host))
                     .GroupBy(c => c.Host, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1)
                     .Select(g => g.Key))
        {
            failures.Add($"Radius:Clients lists host '{duplicate}' more than once.");
        }

        return OptionsChecks.Result(failures);
    }
}

internal sealed class UserIdOptionsValidator : IValidateOptions<UserIdOptions>
{
    public ValidateOptionsResult Validate(string? name, UserIdOptions options)
    {
        List<string> failures = [];

        if (options.TimeoutMinutes < 1)
        {
            failures.Add($"UserId:TimeoutMinutes must be at least 1 (was {options.TimeoutMinutes}).");
        }

        if (options.InterimIntervalMinutes < 1)
        {
            failures.Add($"UserId:InterimIntervalMinutes must be at least 1 (was {options.InterimIntervalMinutes}).");
        }
        else if (options.TimeoutMinutes < 2 * options.InterimIntervalMinutes)
        {
            // One lost Interim-Update would then expire a Mapping while the client is still connected.
            failures.Add($"UserId:TimeoutMinutes ({options.TimeoutMinutes}) must be at least twice UserId:InterimIntervalMinutes ({options.InterimIntervalMinutes}).");
        }

        if (options.Domain.LookupCacheMinutes < 1)
        {
            failures.Add($"UserId:Domain:LookupCacheMinutes must be at least 1 (was {options.Domain.LookupCacheMinutes}).");
        }

        for (int i = 0; i < options.Domain.Rules.Count; i++)
        {
            ValidateDomainRule(options.Domain.Rules[i], $"UserId:Domain:Rules:{i}", failures);
        }

        if (options.BatchSize < 1)
        {
            failures.Add($"UserId:BatchSize must be at least 1 (was {options.BatchSize}).");
        }

        if (options.BatchWindowMs < 0)
        {
            failures.Add($"UserId:BatchWindowMs must not be negative (was {options.BatchWindowMs}).");
        }

        if (options.QueueCapacity < options.BatchSize)
        {
            failures.Add($"UserId:QueueCapacity ({options.QueueCapacity}) must be at least UserId:BatchSize ({options.BatchSize}).");
        }

        if (!string.IsNullOrEmpty(options.UsernameFilter))
        {
            OptionsChecks.RequireRegex(options.UsernameFilter, "UserId:UsernameFilter", failures);
        }

        for (int i = 0; i < options.UsernameRewrites.Count; i++)
        {
            UsernameRewriteOptions rewrite = options.UsernameRewrites[i];

            if (string.IsNullOrEmpty(rewrite.Match))
            {
                failures.Add($"UserId:UsernameRewrites:{i}:Match is required.");
            }
            else
            {
                OptionsChecks.RequireRegex(rewrite.Match, $"UserId:UsernameRewrites:{i}:Match", failures);
            }
        }

        return OptionsChecks.Result(failures);
    }

    private static void ValidateDomainRule(DomainRuleOptions rule, string key, List<string> failures)
    {
        if (string.IsNullOrEmpty(rule.Match))
        {
            failures.Add($"{key}:Match is required.");
            return;
        }

        if (!OptionsChecks.TryCreateRegex(rule.Match, $"{key}:Match", failures, out Regex? match))
        {
            return;
        }

        if (rule.Nt4Domain is null && rule.Replace is null && rule.Lookup is null)
        {
            failures.Add($"{key} must set Nt4Domain, Replace or Lookup.");
        }

        if (rule.Nt4Domain is not null && !match.GetGroupNames().Contains("user"))
        {
            failures.Add($"{key}:Nt4Domain needs a 'user' group in Match, e.g. ^(?<user>[^@\\]+)$.");
        }

        if (rule.Lookup is not null && !OperatingSystem.IsWindows())
        {
            failures.Add($"{key}:Lookup asks the directory, which needs a Windows host.");
        }
    }
}

internal sealed class FirewallOptionsValidator(SecretLookup secrets) : IValidateOptions<FirewallOptions>
{
    public ValidateOptionsResult Validate(string? name, FirewallOptions options)
    {
        List<string> failures = [];

        if (options.Endpoints.Count == 0)
        {
            failures.Add("Firewalls:Endpoints must list at least one Firewall.");
        }

        for (int i = 0; i < options.Endpoints.Count; i++)
        {
            Uri endpoint = options.Endpoints[i];

            if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttps)
            {
                failures.Add($"Firewalls:Endpoints:{i} must be an absolute https URL (was '{endpoint}').");
            }
        }

        OptionsChecks.RequireSecret(secrets, options.ApiKeySecretName, "Firewalls:ApiKeySecretName", failures);

        if (!string.IsNullOrEmpty(options.CaFile) && !File.Exists(options.CaFile))
        {
            failures.Add($"Firewalls:CaFile '{options.CaFile}' does not exist.");
        }

        if (options.TimeoutSeconds is < 1 or > 300)
        {
            failures.Add($"Firewalls:TimeoutSeconds must be between 1 and 300 (was {options.TimeoutSeconds}).");
        }

        return OptionsChecks.Result(failures);
    }
}

internal static class OptionsChecks
{
    public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    public static void RequireSecret(SecretLookup secrets, string secretName, string key, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(secretName))
        {
            failures.Add($"{key} is required.");
        }
        else if (!SecretStore.IsValidName(secretName))
        {
            failures.Add($"{key} '{secretName}' is not a valid secret name (letters, digits, '_', '-', '.').");
        }
        else if (string.IsNullOrEmpty(secrets(secretName)))
        {
            failures.Add($"{key} names secret '{secretName}', which is not set. Set it with Set-PanRaProxySecret.ps1 -Name {secretName}.");
        }
    }

    public static void RequireRegex(string pattern, string key, List<string> failures) =>
        TryCreateRegex(pattern, key, failures, out _);

    public static bool TryCreateRegex(string pattern, string key, List<string> failures, [NotNullWhen(true)] out Regex? regex)
    {
        try
        {
            regex = new Regex(pattern, RegexOptions.IgnoreCase, RegexMatchTimeout);
            return true;
        }
        catch (ArgumentException ex)
        {
            failures.Add($"{key} is not a valid regular expression: {ex.Message}");
            regex = null;
            return false;
        }
    }

    public static ValidateOptionsResult Result(List<string> failures) =>
        failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
}
