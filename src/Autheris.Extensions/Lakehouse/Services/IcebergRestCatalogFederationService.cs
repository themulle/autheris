namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-DATA-05: Implementation of Iceberg REST Catalog (IRC) Federation & Dynamic STS Credential Vending.
/// </summary>
public sealed class IcebergRestCatalogFederationService : IIcebergRestCatalogFederationService
{
    private readonly IIcebergMetadataReader _metadataReader;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<IcebergRestCatalogFederationService> _logger;
    private readonly IConsentResolutionService? _consentService;
    private readonly IConsentRepository? _consentRepo;

    public IcebergRestCatalogFederationService(
        IIcebergMetadataReader metadataReader,
        ITableMetadataRepository metadataRepo,
        IOptions<GatewayOptions> options,
        ILogger<IcebergRestCatalogFederationService> logger,
        IConsentResolutionService? consentService = null,
        IConsentRepository? consentRepo = null)
    {
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _consentService = consentService;
        _consentRepo = consentRepo;
    }

    public async ValueTask<IReadOnlyList<string>> ListNamespacesAsync(string tenantId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        // SEC H-3: Scoped strictly to caller's tenant
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var namespaces = allTables
            .Where(t => string.Equals(t.Identifier.Domain, tenantId, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Table.DataSourceType == DataSourceType.LakehouseIceberg || t.Table.DataSourceType == DataSourceType.LakehouseDelta)
            .Select(t => t.Identifier.Schema)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return namespaces;
    }

    public async ValueTask<IReadOnlyList<string>> ListTablesAsync(string tenantId, string @namespace, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);

        // SEC H-3: Scoped strictly to caller's tenant
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var tables = allTables
            .Where(t => string.Equals(t.Identifier.Domain, tenantId, StringComparison.OrdinalIgnoreCase))
            .Where(t => string.Equals(t.Identifier.Schema, @namespace, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Table.DataSourceType == DataSourceType.LakehouseIceberg || t.Table.DataSourceType == DataSourceType.LakehouseDelta)
            .Select(t => t.Identifier.TableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tables;
    }

    public async ValueTask<IcebergLoadTableResponse> LoadTableAsync(
        string tenantId,
        string @namespace,
        string table,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(principal);

        // SEC-IRC-01: Authenticate caller and enforce fail-closed authorization
        if (principal.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning("Unauthenticated attempt to load Iceberg table '{Namespace}.{Table}' for tenant '{Tenant}'.", @namespace, table, tenantId);
            throw new SecurityException($"Unauthorized access to table '{@namespace}.{table}'.");
        }

        var tableMeta = await EnsureConsentedRawAccessAsync(tenantId, @namespace, table, principal, ct).ConfigureAwait(false);

        var location = tableMeta.Table.Location ?? $"lakehouse/{tenantId}/{@namespace}/{table}";

        // SEC-IRC-02: Path traversal validation on lakehouse location
        LakehouseLocationGuard.EnsureNoTraversal(location, location);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadataLocation = $"{location.TrimEnd('/')}/metadata/v2.metadata.json";

        return new IcebergLoadTableResponse(
            metadataLocation,
            null,
            config);
    }

    public async ValueTask<VendedStorageCredential> VendCredentialAsync(
        string tenantId,
        string @namespace,
        string table,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentException.ThrowIfNullOrWhiteSpace(table);
        ArgumentNullException.ThrowIfNull(principal);

        // Review R3-2: the same full consent check as LoadTable runs before anything is vended (or even before a
        // caller can learn that vending is unsupported for a table it may not access).
        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new SecurityException($"Unauthorized access to table '{@namespace}.{table}'.");
        }

        await EnsureConsentedRawAccessAsync(tenantId, @namespace, table, principal, ct).ConfigureAwait(false);

        // SEC H-3: Return 501 Not Implemented instead of vending forgeable random/unsigned fake keys.
        throw new NotSupportedException("Direct storage STS/SAS credential vending is not supported; access lakehouse datasets via governed SQL endpoints.");
    }

    /// <summary>
    /// Review R3-2: raw metadata or storage credentials bypass the SQL engine (row filters, masking, column rules), so they
    /// are only released when the caller holds an active, tenant-scoped ALLOW consent that covers the whole table:
    /// no row filter, every catalog column readable in clear (including columns the consents never mention and catalog-sensitive
    /// or masked columns). Missing consent infrastructure fails closed.
    /// </summary>
    private async ValueTask<TableMetadata> EnsureConsentedRawAccessAsync(
        string tenantId,
        string @namespace,
        string table,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var tableId = new TableIdentifier(tenantId, @namespace, table);
        var tableMeta = await _metadataRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (tableMeta == null)
        {
            // SEC H-3: Never fall back to tables from other tenants. Return 404.
            throw new KeyNotFoundException($"Table '{@namespace}.{table}' not found in tenant '{tenantId}'.");
        }

        if (_consentService == null || _consentRepo == null)
        {
            _logger.LogError("Iceberg table {TableId} requested but consent services are not configured; denying (fail-closed).", tableId);
            throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
        }

        var userSid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "anonymous");
        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();
        var allSubjects = groupSids.Append(userSid).ToList();
        var now = DateTimeOffset.UtcNow;
        var tenant = new TenantId(tenantId);
        var loaded = await _consentRepo.GetActiveConsentsForSubjectsAsync(allSubjects, tableId, now, tenant, ct).ConfigureAwait(false);

        // Defence in depth: only consents of this tenant, for exactly this table, that are active right now.
        var activeConsents = loaded
            .Where(c => c.TenantId == tenant && c.TableIdentifier == tableId && c.IsActive(now))
            .ToList();

        var decision = _consentService.ResolveAccess(userSid, groupSids, roles, tableId, activeConsents, tableMeta.Dialect);
        if (!decision.IsAllowed)
        {
            _logger.LogWarning("Consent denied for user {User} accessing Iceberg table {TableId}", userSid, tableId);
            throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
        }

        bool restricted = !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
                          decision.ColumnAccess.Values.Any(v => v != ColumnAccessLevel.Clear) ||
                          !decision.HasUnconstrainedColumnAllow ||
                          tableMeta.Columns.Any(c => decision.GetEffectiveColumnAccess(c.ColumnName, tableMeta) != ColumnAccessLevel.Clear) ||
                          tableMeta.ColumnMaskingRules.Keys.Any(c => decision.GetEffectiveColumnAccess(c, tableMeta) != ColumnAccessLevel.Clear);
        if (restricted)
        {
            throw new SecurityException($"Direct Iceberg catalog access not permitted: Table '{@namespace}.{table}' requires row-level filtering, column masking or column restrictions which cannot be enforced via raw metadata vending.");
        }

        return tableMeta;
    }
}
