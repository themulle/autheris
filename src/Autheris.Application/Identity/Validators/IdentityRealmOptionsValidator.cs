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

        foreach (var (realmId, config) in options.Realms)
        {
            if (config.Kind is IdentityRealmKind.EntraExternalId or IdentityRealmKind.AzureAdB2C)
            {
                // SEC-SC-21: Customer realm cannot bind to workforce tenant
                if (config.FixedTenantId != null)
                {
                    var fixedTenant = new TenantId(config.FixedTenantId);
                    if (workforceCorporateTenantId.HasValue && fixedTenant == workforceCorporateTenantId.Value)
                    {
                        throw new ValidationException(
                            $"Customer Realm '{realmId}' (Kind: {config.Kind}) cannot be configured with TenantBinding targeting the workforce tenant '{fixedTenant.Value}' (SEC-SC-21).");
                    }
                }
            }
        }
    }

    public static void ValidateRealm(IdentityRealm realm, TenantId? workforceCorporateTenantId = null)
    {
        ArgumentNullException.ThrowIfNull(realm);

        if (realm.Kind is IdentityRealmKind.EntraExternalId or IdentityRealmKind.AzureAdB2C)
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
        }
    }
}
