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
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
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
    private readonly IPolicyEnforcementService? _policyEnforcementService;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly IAuditLogRepository? _auditRepository;

    private readonly Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver _mandatoryFilters;

    public IcebergRestCatalogFederationService(
        IIcebergMetadataReader metadataReader,
        ITableMetadataRepository metadataRepo,
        IOptions<GatewayOptions> options,
        ILogger<IcebergRestCatalogFederationService> logger,
        IConsentResolutionService? consentService = null,
        IConsentRepository? consentRepo = null,
        IPolicyEnforcementService? policyEnforcementService = null,
        IClientIpResolver? clientIpResolver = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        IRebacEvaluator? rebacEvaluator = null,
        IAuditLogRepository? auditRepository = null)
    {
        _mandatoryFilters = mandatoryFilters ?? Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver.Instance;
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _consentService = consentService;
        _consentRepo = consentRepo;
        _policyEnforcementService = policyEnforcementService;
        _clientIpResolver = clientIpResolver;
        _rebacEvaluator = rebacEvaluator;
        _auditRepository = auditRepository;
    }

    public async ValueTask<IReadOnlyList<string>> ListNamespacesAsync(string tenantId, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentNullException.ThrowIfNull(principal);

        var namespaces = (await VisibleLakehouseTablesAsync(tenantId, principal, ct).ConfigureAwait(false))
            .Select(t => t.Identifier.Schema)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return namespaces;
    }

    public async ValueTask<IReadOnlyList<string>> ListTablesAsync(string tenantId, string @namespace, ClaimsPrincipal principal, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ArgumentNullException.ThrowIfNull(principal);

        var tables = (await VisibleLakehouseTablesAsync(tenantId, principal, ct).ConfigureAwait(false))
            .Where(t => string.Equals(t.Identifier.Schema, @namespace, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Identifier.TableName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return tables;
    }

    /// <summary>
    /// SEC H-3: lakehouse tables of the caller's tenant only. Wunsch 9: of those, only tables the caller may discover
    /// (same rule as the GraphQL catalog and MCP; no consent repository → nothing listed).
    /// </summary>
    private async ValueTask<IReadOnlyList<TableMetadata>> VisibleLakehouseTablesAsync(string tenantId, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            return Array.Empty<TableMetadata>();
        }

        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var tenantTables = allTables
            .Where(t => string.Equals(t.Identifier.Domain, tenantId, StringComparison.OrdinalIgnoreCase))
            .Where(t => t.Table.DataSourceType == DataSourceType.LakehouseIceberg || t.Table.DataSourceType == DataSourceType.LakehouseDelta)
            .ToList();

        return await CatalogVisibility.VisibleTablesAsync(tenantTables, principal, new TenantId(tenantId), _consentRepo, ct).ConfigureAwait(false);
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

        var actorSid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "anonymous");

        // SEC-IRC-01: Authenticate caller and enforce fail-closed authorization
        if (principal.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning("Unauthenticated attempt to load Iceberg table '{Namespace}.{Table}' for tenant '{Tenant}'.", @namespace, table, tenantId);
            if (_auditRepository != null)
            {
                await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = new TenantId(tenantId),
                    EventType = "Iceberg.LoadTable",
                    ActorSid = actorSid,
                    TargetTable = $"{tenantId}.{@namespace}.{table}",
                    Decision = "DENY",
                    DetailsJson = "{\"reason\":\"Unauthenticated caller\"}"
                }, ct).ConfigureAwait(false);
            }
            throw new SecurityException($"Unauthorized access to table '{@namespace}.{table}'.");
        }

        TableMetadata tableMeta;
        try
        {
            tableMeta = await EnsureConsentedRawAccessAsync(tenantId, @namespace, table, principal, ct).ConfigureAwait(false);
        }
        catch (SecurityException ex)
        {
            if (_auditRepository != null)
            {
                await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
                {
                    TenantId = new TenantId(tenantId),
                    EventType = "Iceberg.LoadTable",
                    ActorSid = actorSid,
                    TargetTable = $"{tenantId}.{@namespace}.{table}",
                    Decision = "DENY",
                    DetailsJson = $"{{\"reason\":\"{ex.Message.Replace("\"", "\\\"")}\"}}"
                }, ct).ConfigureAwait(false);
            }
            throw;
        }

        var location = tableMeta.Table.Location ?? $"lakehouse/{tenantId}/{@namespace}/{table}";

        // SEC-IRC-02: Path traversal validation on lakehouse location
        LakehouseLocationGuard.EnsureNoTraversal(location, location);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadataLocation = $"{location.TrimEnd('/')}/metadata/v2.metadata.json";

        // SR-P2-06 / SR15-18: Record ALLOW audit log
        if (_auditRepository != null)
        {
            await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = new TenantId(tenantId),
                EventType = "Iceberg.LoadTable",
                ActorSid = actorSid,
                TargetTable = tableMeta.Identifier.ToString(),
                Decision = "ALLOW",
                DetailsJson = $"{{\"metadataLocation\":\"{metadataLocation}\"}}"
            }, ct).ConfigureAwait(false);
        }

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
        // SEC H-3: never fall back to tables from other tenants. Review G5: deactivated tables are not served.
        // Wunsch 9: unknown and inactive tables answer exactly like denied ones, so the endpoint cannot probe the catalog.
        if (tableMeta == null || !tableMeta.Table.IsActive)
        {
            _logger.LogWarning("Iceberg table {Namespace}.{Table} not found or inactive in tenant {Tenant}; answering as denied.", @namespace, table, tenantId);
            throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
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

        // Virtual filters restrict rows; raw metadata vending cannot enforce that (same as any row filter below).
        var mandatory = await _mandatoryFilters.ResolveAsync(
            new Autheris.Application.VirtualFilters.MandatoryFilterQuery(userSid, groupSids, roles, tenant, tableMeta), ct).ConfigureAwait(false);
        if (mandatory.IsDenied || mandatory.PredicateSql != null)
        {
            _logger.LogWarning("Virtual filters apply to user {User} on Iceberg table {TableId}; raw access refused", userSid, tableId);
            throw new SecurityException($"Direct Iceberg catalog access not permitted: Table '{@namespace}.{table}' is restricted by virtual filters.");
        }

        // SR-P2-06 / SR15-18: ReBAC evaluation on lakehouse Iceberg table access
        if (RebacTableGate.IsEnforcedOnQueryPaths(_options.Value))
        {
            var rebacAllowed = await RebacTableGate.IsAllowedAsync(_rebacEvaluator, tenant, userSid, tableId, ct).ConfigureAwait(false);
            if (!rebacAllowed)
            {
                _logger.LogWarning("ReBAC denied user {User} access to Iceberg table {TableId}", userSid, tableId);
                throw new SecurityException($"Access to table '{@namespace}.{table}' denied by ReBAC policy.");
            }
        }

        // Review G5: Casbin ABAC applies to raw access as well; an ABAC row filter cannot be enforced on raw metadata.
        string? abacRowFilter = null;
        if (_policyEnforcementService != null && _policyEnforcementService.HasPolicies(tenant))
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in principal.Claims)
            {
                attributes[claim.Type] = claim.Value;
            }

            var secContext = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: groupSids,
                Tenant: tenant,
                TargetTable: tableId,
                RequestedColumns: tableMeta.Columns.Select(c => c.ColumnName).ToList(),
                ClientIp: _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None,
                Timestamp: now,
                PurposeId: principal.FindFirst("purpose")?.Value ?? principal.FindFirst("purpose_id")?.Value,
                Attributes: attributes);

            var abac = await _policyEnforcementService.EvaluatePolicyAsync(secContext, ct).ConfigureAwait(false);
            if (!abac.IsAllowed)
            {
                _logger.LogWarning("Casbin ABAC denied user {User} raw access to Iceberg table {TableId}", userSid, tableId);
                throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
            }

            abacRowFilter = abac.CombinedRowFilterSql;
        }

        bool restricted = !string.IsNullOrWhiteSpace(abacRowFilter) ||
                          !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
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
