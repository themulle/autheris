namespace Autheris.Application.Identity.Validators;

using System;
using System.ComponentModel.DataAnnotations;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Options;

public sealed class IdentityRealmOptionsValidator : IValidateOptions<IdentityRealmsOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityRealmsOptions options)
    {
        if (options == null) return ValidateOptionsResult.Fail("Options are null.");
        try
        {
            Validate(options);
            return ValidateOptionsResult.Success;
        }
        catch (ValidationException ex)
        {
            return ValidateOptionsResult.Fail(ex.Message);
        }
    }

    public static void Validate(IdentityRealmsOptions options, TenantId? workforceCorporateTenantId = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        var workforceTenants = new HashSet<TenantId>();
        if (workforceCorporateTenantId.HasValue)
        {
            workforceTenants.Add(workforceCorporateTenantId.Value);
        }

        // 1. Validate all tenant IDs across all realms and collect workforce tenants
        foreach (var (realmId, config) in options.Realms)
        {
            if (config.Scim is not null)
            {
                if (config.Scim.MaxSyncAgeMinutes is < ScimRealmSecretOptions.MinMaxSyncAgeMinutes or > ScimRealmSecretOptions.MaxMaxSyncAgeMinutes)
                {
                    throw new ValidationException(
                        $"Authentication:Realms:Realms:{realmId}:Scim:MaxSyncAgeMinutes must be between {ScimRealmSecretOptions.MinMaxSyncAgeMinutes} and {ScimRealmSecretOptions.MaxMaxSyncAgeMinutes}.");
                }

                if (!Enum.IsDefined(config.Scim.StaleSyncBehavior))
                {
                    throw new ValidationException(
                        $"Authentication:Realms:Realms:{realmId}:Scim:StaleSyncBehavior is invalid.");
                }
            }

            if (!string.IsNullOrWhiteSpace(config.FixedTenantId))
            {
                if (!TenantId.TryParse(config.FixedTenantId, out var fixedId))
                {
                    throw new ValidationException($"FixedTenantId '{config.FixedTenantId}' in realm '{realmId}' is not a valid tenant id.");
                }

                if (config.Kind is IdentityRealmKind.EntraWorkforce or IdentityRealmKind.Adfs or IdentityRealmKind.Kerberos ||
                    string.Equals(realmId, "workforce-entra", StringComparison.OrdinalIgnoreCase))
                {
                    workforceTenants.Add(fixedId);
                }
            }

            if (config.TenantClaimMapping != null)
            {
                foreach (var (_, mappedVal) in config.TenantClaimMapping)
                {
                    if (!TenantId.TryParse(mappedVal, out var mappedId))
                    {
                        throw new ValidationException($"Mapped tenant id '{mappedVal}' in realm '{realmId}' is not a valid tenant id.");
                    }

                    if (config.Kind is IdentityRealmKind.EntraWorkforce or IdentityRealmKind.Adfs or IdentityRealmKind.Kerberos ||
                        string.Equals(realmId, "workforce-entra", StringComparison.OrdinalIgnoreCase))
                    {
                        workforceTenants.Add(mappedId);
                    }
                }
            }
        }

        // 2. Enforce customer realms cannot bind to workforce tenants
        foreach (var (realmId, config) in options.Realms)
        {
            if (config.Kind is IdentityRealmKind.EntraExternalId or IdentityRealmKind.AzureAdB2C or IdentityRealmKind.Customer)
            {
                if (!string.IsNullOrWhiteSpace(config.FixedTenantId) && TenantId.TryParse(config.FixedTenantId, out var fixedTenant))
                {
                    if (workforceTenants.Contains(fixedTenant))
                    {
                        throw new ValidationException(
                            $"Customer Realm '{realmId}' (Kind: {config.Kind}) cannot be configured with TenantBinding targeting the workforce tenant '{fixedTenant.Value}' (SEC-SC-21).");
                    }
                }

                if (config.TenantClaimMapping != null)
                {
                    foreach (var (_, mappedVal) in config.TenantClaimMapping)
                    {
                        if (TenantId.TryParse(mappedVal, out var mappedTenant) && workforceTenants.Contains(mappedTenant))
                        {
                            throw new ValidationException(
                                $"Customer Realm '{realmId}' (Kind: {config.Kind}) cannot be configured with TenantBinding targeting the workforce tenant '{mappedTenant.Value}' (SEC-SC-21).");
                        }
                    }
                }
            }
        }

        foreach (var (realmId, config) in options.Realms)
        {
            foreach (var roleName in config.AllowedGatewayRoles ?? [])
            {
                if (!SubjectRoleMapper.TryParseRoleName(roleName, out _))
                {
                    throw new ValidationException(
                        $"Authentication:Realms:Realms:{realmId}:AllowedGatewayRoles contains an unknown, removed, or numeric gateway role name '{roleName}'.");
                }
            }

            if (config.SubjectRoles != null)
            {
                for (var i = 0; i < config.SubjectRoles.Count; i++)
                {
                    var entryRoles = config.SubjectRoles[i]?.Roles;
                    if (entryRoles == null)
                    {
                        continue;
                    }

                    foreach (var entryRole in entryRoles)
                    {
                        if (!SubjectRoleMapper.TryParseRoleName(entryRole, out _))
                        {
                            throw new ValidationException(
                                $"Authentication:Realms:Realms:{realmId}:SubjectRoles:{i}:Roles contains an unknown, removed, or numeric gateway role name '{entryRole}'.");
                        }
                    }
                }
            }
        }
    }

    public static void ValidateRealm(IdentityRealm realm, TenantId? workforceCorporateTenantId = null)
    {
        ArgumentNullException.ThrowIfNull(realm);

        if (realm.Kind is IdentityRealmKind.EntraExternalId or IdentityRealmKind.AzureAdB2C or IdentityRealmKind.Customer)
        {
            if (realm.TenantBinding.Kind == TenantBindingKind.Fixed && realm.TenantBinding.FixedTenantId.HasValue)
            {
                var fixedTenant = realm.TenantBinding.FixedTenantId.Value;
                if (workforceCorporateTenantId.HasValue && fixedTenant == workforceCorporateTenantId.Value)
                {
                    throw new ValidationException(
                        $"Customer Realm '{realm.RealmId}' (Kind: {realm.Kind}) cannot be configured with TenantBinding targeting the workforce tenant '{fixedTenant.Value}' (SEC-SC-21).");
                }
            }

            if (realm.TenantBinding.Kind == TenantBindingKind.ClaimMapped && realm.TenantBinding.Mapping != null)
            {
                if (workforceCorporateTenantId.HasValue && realm.TenantBinding.Mapping.Values.Contains(workforceCorporateTenantId.Value))
                {
                    throw new ValidationException(
                        $"Customer Realm '{realm.RealmId}' (Kind: {realm.Kind}) cannot be configured with TenantBinding targeting the workforce tenant '{workforceCorporateTenantId.Value.Value}' (SEC-SC-21).");
                }
            }
        }
    }
}
