using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
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
        }

        // One shared secret for every RADIUS Client (ADR 0007).
        OptionsChecks.RequireSecret(secrets, SecretNames.Radius, failures);

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

        // The Username Rewrites and domain rules are validated by the module that runs them (CanonicalUsernameRules).
        return OptionsChecks.Result(failures);
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

        OptionsChecks.RequireSecret(secrets, SecretNames.FirewallApiKey, failures);

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

/// <summary>
/// The settings ADR 0007 removed. Binding would ignore them silently, leaving the administrator to wonder
/// why the secret they name isn't used, so they fail validation with what to do instead.
/// </summary>
internal sealed class ObsoleteSettingsValidator(IConfiguration configuration) : IValidateOptions<RadiusOptions>, IValidateOptions<FirewallOptions>
{
    public ValidateOptionsResult Validate(string? name, RadiusOptions options) =>
        OptionsChecks.Result(configuration.GetSection("Radius:Clients").GetChildren()
            .Where(client => client["SecretName"] is not null)
            .Select(client => $"Radius:Clients:{client.Key}:SecretName is no longer used: every RADIUS Client shares one secret, set with PanRaProxy --set-secret {SecretNames.Radius}.")
            .ToList());

    public ValidateOptionsResult Validate(string? name, FirewallOptions options) =>
        OptionsChecks.Result(configuration["Firewalls:ApiKeySecretName"] is null
            ? []
            : [$"Firewalls:ApiKeySecretName is no longer used: the API key is set with PanRaProxy --set-secret {SecretNames.FirewallApiKey}."]);
}

internal static class OptionsChecks
{
    public static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(100);

    public static void RequireSecret(SecretLookup secrets, string name, List<string> failures)
    {
        try
        {
            if (string.IsNullOrEmpty(secrets(name)))
            {
                failures.Add($"The secret '{name}' is not set. Set it with PanRaProxy --set-secret {name}, from an elevated prompt.");
            }
        }
        catch (InvalidOperationException ex)
        {
            failures.Add(ex.Message); // a value this machine can't decrypt
        }
    }

    public static void RequireRegex(string pattern, string key, List<string> failures) =>
        TryCreateRegex(pattern, key, failures, out _);

    /// <summary>
    /// Every regex in the settings is built here, so a pattern is validated exactly as it runs.
    /// </summary>
    public static Regex CreateRegex(string pattern) =>
        new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexMatchTimeout);

    public static bool TryCreateRegex(string pattern, string key, List<string> failures, [NotNullWhen(true)] out Regex? regex)
    {
        try
        {
            regex = CreateRegex(pattern);
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
