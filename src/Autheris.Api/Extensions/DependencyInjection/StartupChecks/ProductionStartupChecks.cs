namespace Autheris.Api.Extensions.DependencyInjection.StartupChecks;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Security;
using Autheris.Application.Governance;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Dialects;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>Ordered startup checks of one area; each method is one contiguous section of the former <c>ValidateStartup</c> body (ARCH-MNT-12).</summary>
internal static class ProductionStartupChecks
{
    internal static void ProductionGovernance(StartupCheckContext context)
    {
        var options = context.Options;
        var dangerBypasses = context.DangerBypasses;

        if (context.IsDevelopment)
        {
            return;
        }

        if (options.GovernanceDb.SeedDemoData == true)
        {
            throw new ValidationException("Sicherheitsverletzung: GovernanceDb.SeedDemoData darf AUSSCHLIESSLICH in der Development-Umgebung true sein!");
        }

        // WP-ARC-B-12 (fail closed): the schema must never be started unchecked outside Development.
        if (options.GovernanceDb.Migrations.Mode == GovernanceMigrationMode.Skip)
        {
            throw new ValidationException("Security violation: GovernanceDb:Migrations:Mode = Skip is only allowed in the Development environment. Use Apply or Verify.");
        }

        if (options.Authentication.EnableTestAuthHandler)
        {
            throw new ValidationException("Security violation: EnableTestAuthHandler may be true ONLY in the Development environment.");
        }

        // SEC SG-37: RequireHttpsMetadata must not be false outside Development.
        if (options.Authentication.EntraId.RequireHttpsMetadata == false ||
            options.Authentication.Adfs.RequireHttpsMetadata == false)
        {
            throw new ValidationException("Security violation: RequireHttpsMetadata must not be false outside Development.");
        }

        if (options.IsAnonymousAccessAllowed)
        {
            throw new ValidationException("Security violation: danger_allow_anonymous_access may be true ONLY in the Development environment.");
        }

        if (options.Rebac.SeedTuples.Count > 0)
        {
            if (options.Rebac.SeedTuples.Any(t => t.User.Contains("david", StringComparison.OrdinalIgnoreCase) || t.Object.Contains("prod", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ValidationException("Security critical: Demo or production-targeted ReBAC seed tuples are not permitted outside Development.");
            }
        }

        // Only DANGER entries are blocked outside Development; WARN entries are permitted (reported above).
        if (dangerBypasses.Count > 0)
        {
            throw new ValidationException(
                $"Critical security violation: The following security bypasses may be active ONLY in the Development environment:\n  - " +
                string.Join("\n  - ", dangerBypasses));
        }
    }

    internal static void ProductionTransportAndOrigins(StartupCheckContext context)
    {
        var options = context.Options;

        if (context.IsDevelopment)
        {
            return;
        }

        // RR-L3-05: warn_enable_introspection and GraphQL:EnableIntrospection are prohibited outside Development without explicit opt-in
        if ((options.IsIntrospectionForced || options.GraphQL.EnableIntrospection) && !options.AllowInsecureWarnFlagsInProduction)
        {
            throw new ValidationException(
                "Security violation: Outside Development, GraphQL introspection (GraphQL:EnableIntrospection or warn_enable_introspection) " +
                "may be active only with an explicit opt-in (AllowInsecureWarnFlagsInProduction = true).");
        }

        // API-16: warn_allow_all_cors_origins is prohibited outside Development without explicit opt-in
        if (options.IsAllCorsAllowed && !options.AllowInsecureWarnFlagsInProduction)
        {
            throw new ValidationException(
                "Security violation (API-16): Outside Development, warn_allow_all_cors_origins may be active only with an explicit " +
                "opt-in (AllowInsecureWarnFlagsInProduction = true).");
        }

        // Sicherheits-Invariante 2: Mcp.EnableDeveloperCors is strictly prohibited outside Development and cannot be bypassed
        if (options.Mcp.EnableDeveloperCors)
        {
            throw new ValidationException(
                "Security violation: Mcp.EnableDeveloperCors is strictly prohibited outside Development and cannot be bypassed.");
        }

        if (!options.IsInsecureTransportAllowed && !options.IsColumnMaskingDisabled &&
            (string.IsNullOrWhiteSpace(options.DataMasking.HmacSecretKeyVaultRef) ||
            options.DataMasking.HmacSecretKeyVaultRef == "DEV_INSECURE_TEST_KEY_ONLY" ||
            options.DataMasking.HmacSecretKeyVaultRef == "dev-only-hmac-salt-secure-fallback"))
        {
            throw new ValidationException("NF-SEC-03 violation: Outside Development, HmacSecretKeyVaultRef must be a valid Key Vault secret reference.");
        }

        if (!options.IsInsecureTransportAllowed && options.OpenMetadata.Enabled &&
            Uri.TryCreate(options.OpenMetadata.ServerUrl, UriKind.Absolute, out var omUri) &&
            !string.Equals(omUri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ValidationException("Security violation: Outside Development, OpenMetadata.ServerUrl must use HTTPS.");
        }

        if (options.AreUntrustedCertificatesAllowed)
        {
            throw new ValidationException("Security violation: danger_allow_untrusted_certificates may be true ONLY in the Development environment.");
        }

        // DEP-7 / INF-6: data source connections must encrypt and verify the server certificate outside Development.
        foreach (var (name, connection) in options.DataSources.Connections)
        {
            if (ConnectionTlsPolicy.Validate(connection.Provider, connection.ConnectionString) is { } tlsError)
            {
                throw new ValidationException($"Sicherheitsverletzung: DataSources:Connections:{name}: {tlsError}");
            }
        }

        if (options.GraphQL.TrustedOrigins.Contains("*"))
        {
            throw new ValidationException("Security violation: TrustedOrigins '*' (wildcard CORS) is prohibited outside the Development environment for security reasons (CSRF protection).");
        }

        if (options.GraphQL.TrustedOrigins.Any(o => o != "*" && (!Uri.TryCreate(o, UriKind.Absolute, out var u) || !string.Equals(u.Scheme, "https", StringComparison.OrdinalIgnoreCase))))
        {
            throw new ValidationException("Security violation: Outside Development, TrustedOrigins must contain only HTTPS URLs.");
        }

        if (options.HighAvailability.MultiNodeClusterMode && options.Rebac.Enabled && !options.Caching.Redis.Enabled)
        {
            throw new ValidationException("Security violation: ReBAC in MultiNodeClusterMode requires Caching.Redis.Enabled = true for cluster-wide invalidation (RR-L4-04).");
        }

        if ((options.HighAvailability.MultiNodeClusterMode || options.HighAvailability.Replicas > 1) && !options.Caching.Redis.Enabled)
        {
            throw new ValidationException("NF-HA-02 violation: In MultiNodeClusterMode or with more than one replica (PG-6), cluster-wide cache and epoch invalidation requires Caching.Redis.Enabled = true.");
        }

        if (!string.IsNullOrWhiteSpace(options.Plugins.Directory) &&
            Directory.Exists(Path.GetFullPath(options.Plugins.Directory)) &&
            !options.Plugins.RequireIntegrityManifest)
        {
            throw new ValidationException("Security violation: Outside Development, a configured plugin directory requires Plugins.RequireIntegrityManifest = true.");
        }
    }
}
