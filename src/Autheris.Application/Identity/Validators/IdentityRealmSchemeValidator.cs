namespace Autheris.Application.Identity.Validators;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Tenancy;
using Autheris.Domain.Identity;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Microsoft.Extensions.Options;

/// <summary>
/// DT-A-11 / DT-SC-14: identity realms are explicit. An enabled authentication scheme needs a realm of the matching kind in
/// <c>Authentication:Realms</c> (no implicit <c>*-legacy</c> realm exists), and a realm without its own tenant binding needs a
/// configured default tenant. Messages name the scheme and the realm id, never configured values.
/// </summary>
public sealed class IdentityRealmSchemeValidator(GatewayOptions gateway) : IValidateOptions<IdentityRealmsOptions>
{
    private readonly GatewayOptions _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));

    public ValidateOptionsResult Validate(string? name, IdentityRealmsOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Options are null.");
        }

        var failures = FindFailures(_gateway, options);
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    public static IReadOnlyList<string> FindFailures(GatewayOptions gateway, IdentityRealmsOptions realms)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(realms);

        var failures = new List<string>();
        var auth = gateway.Authentication;
        var enabled = new List<(string Scheme, IdentityRealmKind Kind)>();
        if (auth.EntraId.Enabled)
        {
            enabled.Add(("EntraId", IdentityRealmKind.EntraWorkforce));
        }

        if (auth.Adfs.Enabled)
        {
            enabled.Add(("Adfs", IdentityRealmKind.Adfs));
        }

        if (auth.BasicAuth.Enabled)
        {
            enabled.Add(("BasicAuth", IdentityRealmKind.Basic));
        }

        if (auth.ForwardAuth.Enabled)
        {
            enabled.Add(("ForwardAuth", IdentityRealmKind.ForwardAuth));
        }

        if (auth.IsKerberosConfigured)
        {
            enabled.Add(("Kerberos", IdentityRealmKind.Kerberos));
        }

        foreach (var (scheme, kind) in enabled)
        {
            if (!realms.Realms.Values.Any(r => r.Kind == kind))
            {
                failures.Add(
                    $"Authentication scheme '{scheme}' is enabled but '{IdentityRealmsOptions.SectionName}' has no realm of kind '{kind}'. " +
                    "Configure an explicit realm; implicit '*-legacy' realms no longer exist.");
            }
        }

        if (TenantResolver.EffectiveDefaultOf(gateway.Tenancy) is null)
        {
            foreach (var (realmId, config) in realms.Realms)
            {
                var fixedBinding = config.FixedTenantId is not null;
                var claimBinding = config.TenantClaimName is not null && config.TenantClaimMapping is not null;
                if (!fixedBinding && !claimBinding)
                {
                    failures.Add(
                        $"Identity realm '{realmId}' has neither FixedTenantId nor a tenant claim mapping and no default tenant is configured; " +
                        "set a realm tenant binding or 'Gateway:Tenancy:DefaultTenantId'.");
                }
            }
        }

        return failures;
    }
}
