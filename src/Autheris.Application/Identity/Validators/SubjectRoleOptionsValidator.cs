namespace Autheris.Application.Identity.Validators;

using System;
using System.Collections.Generic;
using System.IO;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Startup validation of workload subject mappings and static JWKS sources. Every violation fails startup: no entry is dropped silently,
/// a privileged mapping needs an explicit opt-in, and messages carry the realm id and entry index only (never a subject or key material).
/// </summary>
public sealed class SubjectRoleOptionsValidator(ILogger<SubjectRoleOptionsValidator>? logger = null) : IValidateOptions<IdentityRealmsOptions>
{
    public ValidateOptionsResult Validate(string? name, IdentityRealmsOptions options)
    {
        if (options is null)
        {
            return ValidateOptionsResult.Fail("Options are null.");
        }

        var failures = new List<string>();
        foreach (var (realmId, config) in options.Realms)
        {
            ValidateRealm(realmId, config, failures);
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private void ValidateRealm(string realmId, IdentityRealmConfigOptions config, List<string> failures)
    {
        if (config.Kind == IdentityRealmKind.GenericOidc && config.ReadOnlyScopes is { Count: 0 })
        {
            logger?.LogWarning(
                "Authentication:Realms:Realms:{RealmId}:ReadOnlyScopes is configured as an empty list; scope-based read-only token marking is disabled for this realm.",
                realmId);
        }

        var prefix = $"Authentication:Realms:Realms:{realmId}";
        var subjectRoles = config.SubjectRoles ?? [];

        if (config.Jwks is { } jwks)
        {
            ValidateJwks(prefix, config, jwks, failures);
        }

        if (subjectRoles.Count == 0)
        {
            if (config.AllowPrivilegedSubjectRoles)
            {
                failures.Add($"{prefix}:AllowPrivilegedSubjectRoles is set but the realm has no SubjectRoles.");
            }

            return;
        }

        if (config.Kind != IdentityRealmKind.GenericOidc)
        {
            failures.Add($"{prefix}:SubjectRoles requires Kind GenericOidc.");
        }

        if (!string.Equals(config.UserKeyClaim, "sub", StringComparison.Ordinal))
        {
            failures.Add($"{prefix}:SubjectRoles requires UserKeyClaim sub.");
        }

        if (config.Issuers is not { Count: > 0 })
        {
            failures.Add($"{prefix}:SubjectRoles requires at least one issuer.");
        }

        var hasFixedTenant = !string.IsNullOrWhiteSpace(config.FixedTenantId)
            && TenantId.TryParse(config.FixedTenantId, out var fixedTenant)
            && !TenantId.IsReserved(fixedTenant);
        if (!hasFixedTenant)
        {
            failures.Add($"{prefix}:SubjectRoles requires a valid FixedTenantId (fixed tenant binding only).");
        }

        if (config.GroupSource != RealmGroupSource.Resolver)
        {
            failures.Add($"{prefix}:SubjectRoles requires GroupSource Resolver.");
        }

        if (config.ResolverChain is null || !config.ResolverChain.Exists(static id => string.Equals(id, Providers.SubjectRoleProvider.Id, StringComparison.OrdinalIgnoreCase)))
        {
            failures.Add($"{prefix}:ResolverChain must list the '{Providers.SubjectRoleProvider.Id}' provider when SubjectRoles is set.");
        }

        if (subjectRoles.Count > SubjectRoleMapper.MaxEntries)
        {
            failures.Add($"{prefix}:SubjectRoles has more than {SubjectRoleMapper.MaxEntries} entries.");
        }

        var ceiling = new HashSet<GatewayRole>();
        foreach (var roleName in config.AllowedGatewayRoles ?? [])
        {
            if (SubjectRoleMapper.TryParseRoleName(roleName, out var role))
            {
                ceiling.Add(role);
            }
            else
            {
                failures.Add($"{prefix}:AllowedGatewayRoles contains an unknown or numeric role name.");
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var hasClusterAdmin = false;
        for (var i = 0; i < subjectRoles.Count; i++)
        {
            var entry = subjectRoles[i];
            var entryPrefix = $"{prefix}:SubjectRoles:{i}";
            if (entry is null)
            {
                failures.Add($"{entryPrefix} is empty.");
                continue;
            }

            ValidateSubject(entryPrefix, entry.Subject, seen, failures);
            hasClusterAdmin |= ValidateRoles(entryPrefix, entry.Roles, ceiling, failures);
        }

        if (hasClusterAdmin)
        {
            if (!config.AllowPrivilegedSubjectRoles)
            {
                failures.Add($"{prefix}:SubjectRoles grants ClusterAdmin; AllowPrivilegedSubjectRoles must be true.");
            }

            if (config.Audiences is not { Count: > 0 })
            {
                failures.Add($"{prefix}:SubjectRoles grants ClusterAdmin; Audiences must not be empty.");
            }
        }
    }

    private static void ValidateSubject(string entryPrefix, string? subject, HashSet<string> seen, List<string> failures)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            failures.Add($"{entryPrefix}:Subject must not be empty.");
            return;
        }

        if (subject.Length > SubjectRoleMapper.MaxSubjectLength)
        {
            failures.Add($"{entryPrefix}:Subject is longer than {SubjectRoleMapper.MaxSubjectLength} characters.");
        }

        if (!string.Equals(subject, subject.Trim(), StringComparison.Ordinal))
        {
            failures.Add($"{entryPrefix}:Subject must not have leading or trailing whitespace.");
        }

        foreach (var c in subject)
        {
            if (char.IsControl(c) || SubjectRoleMapper.ForbiddenSubjectCharacters.Contains(c, StringComparison.Ordinal))
            {
                failures.Add($"{entryPrefix}:Subject contains a control, wildcard or pattern character.");
                break;
            }
        }

        if (!seen.Add(subject))
        {
            failures.Add($"{entryPrefix}:Subject is a duplicate.");
        }
    }

    private static bool ValidateRoles(string entryPrefix, List<string>? roles, HashSet<GatewayRole> ceiling, List<string> failures)
    {
        if (roles is not { Count: > 0 })
        {
            failures.Add($"{entryPrefix}:Roles must not be empty.");
            return false;
        }

        if (roles.Count > SubjectRoleMapper.MaxRolesPerEntry)
        {
            failures.Add($"{entryPrefix}:Roles has more than {SubjectRoleMapper.MaxRolesPerEntry} entries.");
        }

        var clusterAdmin = false;
        for (var j = 0; j < roles.Count; j++)
        {
            if (!SubjectRoleMapper.TryParseRoleName(roles[j], out var role))
            {
                failures.Add($"{entryPrefix}:Roles:{j} is an unknown or numeric role name.");
                continue;
            }

            if (!ceiling.Contains(role))
            {
                failures.Add($"{entryPrefix}:Roles:{j} is not listed in AllowedGatewayRoles.");
            }

            clusterAdmin |= role == GatewayRole.ClusterAdmin;
        }

        return clusterAdmin;
    }

    private static void ValidateJwks(string prefix, IdentityRealmConfigOptions config, StaticJwksSource jwks, List<string> failures)
    {
        if (config.Kind != IdentityRealmKind.GenericOidc)
        {
            failures.Add($"{prefix}:Jwks requires Kind GenericOidc.");
        }

        if (!string.IsNullOrWhiteSpace(config.MetadataAddress))
        {
            failures.Add($"{prefix}:Jwks and MetadataAddress are mutually exclusive.");
        }

        if (string.IsNullOrWhiteSpace(jwks.File) || !Path.IsPathFullyQualified(jwks.File))
        {
            failures.Add($"{prefix}:Jwks:File must be an absolute path.");
        }

        if (jwks.ReloadIntervalMinutes is < StaticJwksSource.MinReloadIntervalMinutes or > StaticJwksSource.MaxReloadIntervalMinutes)
        {
            failures.Add($"{prefix}:Jwks:ReloadIntervalMinutes must be between {StaticJwksSource.MinReloadIntervalMinutes} and {StaticJwksSource.MaxReloadIntervalMinutes}.");
        }

        if (config.Issuers is not { Count: > 0 })
        {
            failures.Add($"{prefix}:Jwks requires at least one issuer.");
        }

        if (config.Audiences is not { Count: > 0 })
        {
            failures.Add($"{prefix}:Jwks requires at least one audience.");
        }
    }
}
