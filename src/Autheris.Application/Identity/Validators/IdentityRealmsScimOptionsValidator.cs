namespace Autheris.Application.Identity.Validators;

using System;
using System.Collections.Generic;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Options;

public class IdentityRealmsScimOptionsValidator : IValidateOptions<IdentityRealmsOptions>
{
    public const int MinimumSecretLength = 32;

    public ValidateOptionsResult Validate(string? name, IdentityRealmsOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("Options are null.");
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
