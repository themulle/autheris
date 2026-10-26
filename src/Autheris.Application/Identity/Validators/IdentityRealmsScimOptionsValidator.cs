namespace Autheris.Application.Identity.Validators;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public class IdentityRealmsScimOptionsValidator : IValidateOptions<IdentityRealmsOptions>
{
    public const int MinimumSecretLength = 32;

    private readonly IHostEnvironment? _environment;
    private readonly ILogger<IdentityRealmsScimOptionsValidator>? _logger;

    public IdentityRealmsScimOptionsValidator(IHostEnvironment? environment = null, ILogger<IdentityRealmsScimOptionsValidator>? logger = null)
    {
        _environment = environment;
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, IdentityRealmsOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("Options are null.");
        }

        // ARCH-MNT-06 (SC-01): an invalid caller range fails the start; it never silently disables the allowlist.
        var ranges = options.Scim?.AllowedIpRanges ?? [];
        for (var i = 0; i < ranges.Count; i++)
        {
            if (!ScimIpAllowlist.IsValidRange(ranges[i]))
            {
                return ValidateOptionsResult.Fail($"Authentication:Realms:Scim:AllowedIpRanges[{i}] is not a valid CIDR range or IP address.");
            }
        }

        if (ranges.Count == 0
            && _environment?.IsProduction() == true
            && options.Realms.Values.Any(r => r.Provisioning == RealmProvisioningMode.Scim))
        {
            _logger?.LogWarning(
                "SCIM provisioning is enabled without Authentication:Realms:Scim:AllowedIpRanges: any peer holding a SCIM secret is accepted. Restrict the callers by CIDR.");
        }

        var seenSecrets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (realmId, config) in options.Realms)
        {
            if (config.Provisioning != RealmProvisioningMode.Scim)
            {
                continue;
            }

            var scim = config.Scim;
            var primary = scim?.PrimarySecret;
            var secondary = scim?.SecondarySecret;

            if (string.IsNullOrWhiteSpace(primary) && string.IsNullOrWhiteSpace(secondary))
            {
                return ValidateOptionsResult.Fail($"SCIM realm '{realmId}' must have at least one secret configured.");
            }

            if (!string.IsNullOrWhiteSpace(primary))
            {
                if (primary.Length < MinimumSecretLength)
                {
                    return ValidateOptionsResult.Fail($"SCIM secret for realm '{realmId}' is shorter than {MinimumSecretLength} characters.");
                }

                if (!seenSecrets.Add(primary))
                {
                    return ValidateOptionsResult.Fail($"SCIM secret for realm '{realmId}' is duplicate. Secrets must be unique across realms.");
                }
            }

            if (!string.IsNullOrWhiteSpace(secondary))
            {
                if (secondary.Length < MinimumSecretLength)
                {
                    return ValidateOptionsResult.Fail($"SCIM secondary secret for realm '{realmId}' is shorter than {MinimumSecretLength} characters.");
                }

                if (!seenSecrets.Add(secondary))
                {
                    return ValidateOptionsResult.Fail($"SCIM secondary secret for realm '{realmId}' is duplicate. Secrets must be unique across realms.");
                }
            }
        }

        return ValidateOptionsResult.Success;
    }
}

public sealed class ScimOptionsValidator : IdentityRealmsScimOptionsValidator
{
}
