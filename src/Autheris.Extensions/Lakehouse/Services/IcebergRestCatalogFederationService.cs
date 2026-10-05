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

        var tableId = new TableIdentifier(tenantId, @namespace, table);
        var tableMeta = await _metadataRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (tableMeta == null)
        {
            // SEC H-3: Never fall back to tables from other tenants. Return 404.
            throw new KeyNotFoundException($"Table '{@namespace}.{table}' not found in tenant '{tenantId}'.");
        }

        // Restpunkt H-3: Enforce consent before vending table location
        if (_consentService != null)
        {
            var userSid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "anonymous");
            var groupSids = principal.GetGroupSids();
            var roles = principal.GetUserRoles();
            var allSubjects = groupSids.Append(userSid).ToList();
            var activeConsents = _consentRepo != null
                ? await _consentRepo.GetActiveConsentsForSubjectsAsync(allSubjects, tableId, DateTimeOffset.UtcNow, new TenantId(tenantId), ct).ConfigureAwait(false)
                : (IReadOnlyList<Consent>)Array.Empty<Consent>();

            var decision = _consentService.ResolveAccess(userSid, groupSids, roles, tableId, activeConsents, tableMeta.Dialect);
            if (!decision.IsAllowed)
            {
                _logger.LogWarning("Consent denied for user {User} accessing Iceberg table {TableId}", userSid, tableId);
                throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
            }
            if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
                decision.ColumnAccess.Values.Any(v => v == ColumnAccessLevel.Mask || v == ColumnAccessLevel.Deny))
            {
                throw new SecurityException($"Direct Iceberg catalog access not permitted: Table '{@namespace}.{table}' requires row-level filtering or column masking which cannot be enforced via raw metadata vending.");
            }
        }

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

    public ValueTask<VendedStorageCredential> VendCredentialAsync(
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

        // SEC H-3: Return 501 Not Implemented instead of vending forgeable random/unsigned fake keys.
        throw new NotSupportedException("Direct storage STS/SAS credential vending is not supported; access lakehouse datasets via governed SQL endpoints.");
    }
}
