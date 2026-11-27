namespace Autheris.Infrastructure.Databricks.Provisioning;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using Autheris.Application.Governance.Provisioning;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Options;

/// <summary>
/// Startup validation of the Unity Catalog provisioning section of every Databricks data source. Fails closed: a dedicated
/// OAuth M2M principal with a secret reference that differs from every query principal, one valid tenant, a non-empty list of
/// allowed schemas inside the data source catalog and a bounded reconcile interval.
/// </summary>
public static class DatabricksProvisioningOptionsValidator
{
    public const int MinReconcileIntervalSeconds = 10;
    public const int MaxReconcileIntervalSeconds = 300;

    public static void Validate(GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // A provisioning principal never runs queries anywhere: the token cache is keyed by host and client id, so sharing a client
        // id with any query principal on any Databricks source would run that source's queries with MANAGE.
        var queryPrincipals = options.DataSources.Connections.Values
            .Where(c => c?.Databricks is not null && DataSourceProvider.Is(c.Provider, DatabaseDialect.Databricks))
            .SelectMany(c => QueryPrincipalIds(c!.Databricks!.Auth))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, connection) in options.DataSources.Connections)
        {
            if (connection?.Databricks?.Provisioning is not { } provisioning)
            {
                continue;
            }

            if (!DataSourceProvider.Is(connection.Provider, DatabaseDialect.Databricks))
            {
                throw Fail(name, "the Provisioning section is only valid on a Databricks data source.");
            }

            Validate(name, connection.Databricks, provisioning, options.Databricks, queryPrincipals);
        }
    }

    private static void Validate(string name, DatabricksDataSourceOptions db, DatabricksProvisioningOptions provisioning, DatabricksOptions global, IReadOnlySet<string> queryPrincipals)
    {
        if (!Enum.IsDefined(provisioning.Mode))
        {
            throw Fail(name, "Provisioning.Mode must be Off, DryRun or Enforce.");
        }

        if (provisioning.DriftMode != DatabricksDriftMode.Report)
        {
            throw Fail(name, "Provisioning.DriftMode must be Report; reverting foreign privileges is not available yet.");
        }

        if (provisioning.ReconcileIntervalSeconds is < MinReconcileIntervalSeconds or > MaxReconcileIntervalSeconds)
        {
            throw Fail(name, $"Provisioning.ReconcileIntervalSeconds must be between {MinReconcileIntervalSeconds} and {MaxReconcileIntervalSeconds}.");
        }

        if (string.IsNullOrWhiteSpace(provisioning.Tenant) || !IsValidTenant(provisioning.Tenant.Trim()))
        {
            throw Fail(name, "Provisioning.Tenant must name exactly one valid tenant.");
        }

        ValidateAllowedSchemas(name, db, provisioning);
        ValidateAuth(name, db, provisioning.Auth, global, queryPrincipals);
    }

    private static void ValidateAllowedSchemas(string name, DatabricksDataSourceOptions db, DatabricksProvisioningOptions provisioning)
    {
        if (provisioning.AllowedSchemas is not { Count: > 0 })
        {
            throw Fail(name, "Provisioning.AllowedSchemas must list at least one catalog.schema.");
        }

        if (!UcSecurableNames.TryNormalizeSegment(db.Catalog, out var catalog))
        {
            throw Fail(name, "the Catalog of a provisioning-managed data source must consist of letters, digits and underscores.");
        }

        foreach (var entry in provisioning.AllowedSchemas)
        {
            if (!UcSecurableNames.TryParseSchema(entry, out var entryCatalog, out _))
            {
                throw Fail(name, "every Provisioning.AllowedSchemas entry must be catalog.schema of letters, digits and underscores (no wildcards).");
            }

            if (!string.Equals(entryCatalog, catalog, StringComparison.Ordinal))
            {
                throw Fail(name, "every Provisioning.AllowedSchemas entry must lie in the data source Catalog.");
            }
        }
    }

    private static void ValidateAuth(string name, DatabricksDataSourceOptions db, DatabricksAuthOptions? auth, DatabricksOptions global, IReadOnlySet<string> queryPrincipals)
    {
        if (auth is null || auth.Mode != DatabricksAuthMode.OAuthM2M)
        {
            throw Fail(name, "Provisioning.Auth.Mode must be OAuthM2M; personal access tokens and on-behalf-of tokens are not accepted for provisioning.");
        }

        if (auth.Tenants.Count > 0 || auth.FallbackToGlobalPrincipal || auth.Obo is not null || !string.IsNullOrWhiteSpace(auth.PatRef))
        {
            throw Fail(name, "Provisioning.Auth names exactly one principal: ClientId and ClientSecretRef (no Tenants, Obo or PatRef).");
        }

        if (string.IsNullOrWhiteSpace(auth.ClientId) || string.IsNullOrWhiteSpace(auth.ClientSecretRef))
        {
            throw Fail(name, "Provisioning.Auth requires ClientId and ClientSecretRef.");
        }

        if (!SecretReferenceResolver.IsReference(auth.ClientSecretRef))
        {
            throw Fail(name, "Provisioning.Auth.ClientSecretRef must be a secret reference (e.g. 'kv://', 'vault:'); inline secrets are prohibited.");
        }

        if (queryPrincipals.Contains(auth.ClientId.Trim()))
        {
            throw Fail(name, "Provisioning.Auth.ClientId must differ from every principal that runs queries on any Databricks data source.");
        }

        if (!string.IsNullOrWhiteSpace(auth.TokenEndpoint) && !IsAllowedTokenEndpoint(auth.TokenEndpoint, db.Host, global))
        {
            throw Fail(name, "Provisioning.Auth.TokenEndpoint must be an absolute HTTPS URL on the workspace host or an allowlisted account token host, without user info, query or fragment.");
        }
    }

    private static string[] QueryPrincipalIds(DatabricksAuthOptions? auth)
    {
        if (auth is null)
        {
            return [];
        }

        return new[] { auth.ClientId, auth.Obo?.ClientId }
            .Concat(auth.Tenants.Values.Select(t => t?.ClientId))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .ToArray();
    }

    private static bool IsAllowedTokenEndpoint(string endpoint, string host, DatabricksOptions global)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return false;
        }

        return string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase) ||
               (global.AllowedAccountTokenHosts ?? []).Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidTenant(string tenant)
    {
        try
        {
            _ = new TenantId(tenant);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static ValidationException Fail(string name, string message) =>
        new($"Databricks data source '{name}': {message}");
}
