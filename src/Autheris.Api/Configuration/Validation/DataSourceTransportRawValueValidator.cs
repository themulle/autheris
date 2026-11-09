namespace Autheris.Api.Configuration.Validation;

using System;
using System.Collections.Generic;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #447 (decision 13): the configuration binder turns an empty or unparsable<c>AllowedTransports</c> into
/// <c>null</c> (which now inherits <c>Defaults</c>). This validator inspects the raw configuration text so such a value fails startup
/// instead of silently widening access.
/// </summary>
public sealed class DataSourceTransportRawValueValidator(IConfigurationSection section) : IValidateOptions<DataSourceConfigurationOptions>
{
    private const string KeyName = nameof(DataSourceProfileOptions.AllowedTransports);

    public ValidateOptionsResult Validate(string? name, DataSourceConfigurationOptions options)
    {
        var failures = new List<string>();
        Check(section.GetSection(nameof(DataSourceConfigurationOptions.Defaults)), "Defaults", failures);

        foreach (var profile in section.GetSection(nameof(DataSourceConfigurationOptions.Profiles)).GetChildren())
        {
            Check(profile, profile.Key, failures);
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void Check(IConfigurationSection profileSection, string prefix, List<string> failures)
    {
        var node = profileSection.GetSection(KeyName);
        if (!node.Exists())
        {
            return;
        }

        var raw = node.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            failures.Add($"{prefix}.{KeyName} must not be empty; omit the key to inherit the default or use None to deny all transports.");
            return;
        }

        if (raw.Contains("ArrowFlight", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add($"{prefix}.{KeyName}: transport 'ArrowFlight' was removed; remove it from the configuration.");
            return;
        }

        if (!Enum.TryParse<DataSourceTransportFlags>(raw, ignoreCase: true, out var parsed) ||
            (parsed & ~DataSourceTransportFlags.All) != 0)
        {
            failures.Add($"{prefix}.{KeyName} is not a valid transport list.");
        }
    }
}
