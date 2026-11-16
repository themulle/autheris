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

        //  (SC-01): an invalid caller range fails the start; it never silently disables the allowlist.
        var ranges = options.Scim?.AllowedIpRanges ?? [];
        for (var i = 0; i < ranges.Count; i++)
        {
            if (!ScimIpAllowlist.IsValidRange(ranges[i]))
            {
                return ValidateOptionsResult.Fail($"Authentication:Realms:Scim:AllowedIpRanges[{i}] is not a valid CIDR range or IP address.");
            }
        }

        var protection = options.Scim?.MassChangeProtection ?? new ScimMassChangeProtectionOptions();
        if (protection.MaxDestructiveOperations is < ScimMassChangeProtectionOptions.MaxOperationsLowerBound or > ScimMassChangeProtectionOptions.MaxOperationsUpperBound)
        {
            return ValidateOptionsResult.Fail(
                $"Authentication:Realms:Scim:MassChangeProtection:MaxDestructiveOperations must be between {ScimMassChangeProtectionOptions.MaxOperationsLowerBound} and {ScimMassChangeProtectionOptions.MaxOperationsUpperBound}.");
        }

        if (protection.WindowMinutes is < ScimMassChangeProtectionOptions.WindowMinutesLowerBound or > ScimMassChangeProtectionOptions.WindowMinutesUpperBound)
        {
            return ValidateOptionsResult.Fail(
                $"Authentication:Realms:Scim:MassChangeProtection:WindowMinutes must be between {ScimMassChangeProtectionOptions.WindowMinutesLowerBound} and {ScimMassChangeProtectionOptions.WindowMinutesUpperBound}.");
        }

        if (!protection.Enabled)
        {
            _logger?.LogWarning(
                "SCIM mass-change protection is explicitly disabled (Authentication:Realms:Scim:MassChangeProtection:Enabled=false): a compromised IdP token can deprovision every user without throttling.");
        }

        var hasScimRealm = options.Realms.Values.Any(r => r.Provisioning == RealmProvisioningMode.Scim);
        var isDevelopment = _environment?.IsDevelopment() == true;

        if (hasScimRealm && !isDevelopment && ranges.Count == 0)
        {
            return ValidateOptionsResult.Fail(
                "Authentication:Realms:Scim:AllowedIpRanges must contain at least one CIDR range or IP address outside Development when SCIM provisioning is enabled.");
        }

        var defaultRpm = options.Scim?.RequestsPerMinute ?? ScimOptions.DefaultRequestsPerMinute;
        if (defaultRpm is < ScimOptions.MinRequestsPerMinute or > ScimOptions.MaxRequestsPerMinute)
        {
            return ValidateOptionsResult.Fail(
                $"Authentication:Realms:Scim:RequestsPerMinute must be between {ScimOptions.MinRequestsPerMinute} and {ScimOptions.MaxRequestsPerMinute}.");
        }

        var seenSecrets = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (realmId, config) in options.Realms)
        {
            if (config.Provisioning != RealmProvisioningMode.Scim)
            {
                continue;
            }

            var scim = config.Scim;
            if (scim is not null)
            {
                if (scim.MaxSyncAgeMinutes is < ScimRealmSecretOptions.MinMaxSyncAgeMinutes or > ScimRealmSecretOptions.MaxMaxSyncAgeMinutes)
                {
                    return ValidateOptionsResult.Fail(
                        $"Authentication:Realms:Realms:{realmId}:Scim:MaxSyncAgeMinutes must be between {ScimRealmSecretOptions.MinMaxSyncAgeMinutes} and {ScimRealmSecretOptions.MaxMaxSyncAgeMinutes}.");
                }

                if (scim.RequestsPerMinute is < ScimOptions.MinRequestsPerMinute or > ScimOptions.MaxRequestsPerMinute)
                {
                    return ValidateOptionsResult.Fail(
                        $"Authentication:Realms:Realms:{realmId}:Scim:RequestsPerMinute must be between {ScimOptions.MinRequestsPerMinute} and {ScimOptions.MaxRequestsPerMinute}.");
                }

                if (!Enum.IsDefined(scim.StaleSyncBehavior))
                {
                    return ValidateOptionsResult.Fail(
                        $"Authentication:Realms:Realms:{realmId}:Scim:StaleSyncBehavior is invalid.");
                }

                if (scim.StaleSyncBehavior == ScimStaleSyncBehavior.DropScimGroups)
                {
                    _logger?.LogWarning(
                        "SCIM realm {RealmId} uses StaleSyncBehavior=DropScimGroups: grants assigned to SCIM-only groups will not be applied while SCIM sync is stale, whereas denies on SCIM-only groups still degrade and deny access.",
                        realmId);
                }
            }

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
