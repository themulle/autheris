namespace Autheris.Extensions.Lakehouse.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-DATA-05: Implementation of Iceberg REST Catalog (IRC) Federation &amp; Dynamic STS Credential Vending.
/// </summary>
public sealed class IcebergRestCatalogFederationService : IIcebergRestCatalogFederationService
{
    private readonly IIcebergMetadataReader _metadataReader;
    private readonly ITableMetadataRepository _metadataRepo;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<IcebergRestCatalogFederationService> _logger;
    private readonly IConsentRepository? _consentRepo;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly IAuditLogRepository _auditRepository;
    private readonly IAccessDecisionPoint _accessDecisionPoint;

    public IcebergRestCatalogFederationService(
        IIcebergMetadataReader metadataReader,
        ITableMetadataRepository metadataRepo,
        IOptions<GatewayOptions> options,
        ILogger<IcebergRestCatalogFederationService> logger,
        IAuditLogRepository auditRepository,
        IConsentResolutionService? consentService = null,
        IConsentRepository? consentRepo = null,
        IPolicyEnforcementService? policyEnforcementService = null,
        IClientIpResolver? clientIpResolver = null,
        Autheris.Application.VirtualFilters.IMandatoryRowFilterResolver? mandatoryFilters = null,
        IRebacEvaluator? rebacEvaluator = null,
        Autheris.Application.DataSources.IDataSourceTransportGate? transportGate = null,
        IAccessDecisionPoint? accessDecisionPoint = null)
    {
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
        _consentRepo = consentRepo;
        _rebacEvaluator = rebacEvaluator;
        _accessDecisionPoint = accessDecisionPoint ?? new AccessDecisionPoint(
            _options,
            consentService,
            consentRepo,
            policyEnforcementService,
            clientIpResolver,
            mandatoryFilters,
            rebacEvaluator,
            metadataRepo,
            transportGate);
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
    /// (same rule as the GraphQL catalog and MCP; no consent repository -> nothing listed).
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

        var visible = await CatalogVisibility.VisibleTablesAsync(
            tenantTables,
            principal,
            new TenantId(tenantId),
            _consentRepo,
            contract: null,
            Autheris.Application.Tenancy.TenantResolver.EffectiveDefaultOf(_options.Value.Tenancy),
            _options.Value.Catalog.SharedTables,
            ct).ConfigureAwait(false);

        if (RebacTableGate.IsEnforcedOnQueryPaths(_options.Value))
        {
            var userSid = principal.GetUserSid() ?? new Sid(principal.Identity?.Name ?? "anonymous");
            var filtered = new List<TableMetadata>(visible.Count);
            foreach (var table in visible)
            {
                if (await RebacTableGate.IsAllowedAsync(_rebacEvaluator, new TenantId(tenantId), userSid, table.Identifier, ct).ConfigureAwait(false))
                {
                    filtered.Add(table);
                }
            }
            return filtered;
        }

        return visible;
    }

    public ValueTask<IcebergLoadTableResponse> LoadTableAsync(
        string tenantId,
        TableIdentifier table,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
        => LoadTableAsync(tenantId, table.Schema, table.TableName, principal, ct);

    public ValueTask<VendedStorageCredential> VendCredentialAsync(
        string tenantId,
        TableIdentifier table,
        ClaimsPrincipal principal,
        CancellationToken ct = default)
        => VendCredentialAsync(tenantId, table.Schema, table.TableName, principal, ct);

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

        if (principal.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning(
                "Unauthenticated attempt to load Iceberg table '{Namespace}.{Table}' for tenant '{Tenant}'.",
                LogSanitizer.Sanitize(@namespace),
                LogSanitizer.Sanitize(table),
                LogSanitizer.Sanitize(tenantId));
            await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = new TenantId(tenantId),
                EventType = "Iceberg.LoadTable",
                ActorSid = actorSid,
                TargetTable = $"{tenantId}.{@namespace}.{table}",
                Decision = "DENY",
                DetailsJson = "{\"reason\":\"Unauthenticated caller\"}"
            }, ct).ConfigureAwait(false);
            throw new SecurityException($"Unauthorized access to table '{@namespace}.{table}'.");
        }

        TableMetadata tableMeta;
        try
        {
            tableMeta = await EnsureConsentedRawAccessAsync(tenantId, @namespace, table, principal, AccessOperation.SchemaRead, ct).ConfigureAwait(false);
        }
        catch (SecurityException ex)
        {
            await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
            {
                TenantId = new TenantId(tenantId),
                EventType = "Iceberg.LoadTable",
                ActorSid = actorSid,
                TargetTable = $"{tenantId}.{@namespace}.{table}",
                Decision = "DENY",
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { reason = ex.Message })
            }, ct).ConfigureAwait(false);
            throw;
        }

        var location = tableMeta.Table.Location ?? $"lakehouse/{tenantId}/{@namespace}/{table}";
        LakehouseLocationGuard.EnsureNoTraversal(location, location);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadataLocation = location.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase)
            ? location
            : $"{location.TrimEnd('/')}/metadata/v2.metadata.json";

        var filteredMetadata = await LoadDeclaredColumnMetadataAsync(tableMeta, metadataLocation, ct).ConfigureAwait(false);

        await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = new TenantId(tenantId),
            EventType = "Iceberg.LoadTable",
            ActorSid = actorSid,
            TargetTable = tableMeta.Identifier.ToString(),
            Decision = "ALLOW",
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { metadataLocation })
        }, ct).ConfigureAwait(false);

        return new IcebergLoadTableResponse(
            metadataLocation,
            filteredMetadata,
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

        if (principal.Identity?.IsAuthenticated != true)
        {
            throw new SecurityException($"Unauthorized access to table '{@namespace}.{table}'.");
        }

        await EnsureConsentedRawAccessAsync(tenantId, @namespace, table, principal, AccessOperation.VendCredentials, ct).ConfigureAwait(false);

        await _auditRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = new TenantId(tenantId),
            EventType = "Iceberg.VendCredential",
            ActorSid = principal.GetUserSid() ?? new Sid("anonymous"),
            TargetTable = $"{tenantId}.{@namespace}.{table}",
            Decision = "DENY",
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { reason = "Direct credential vending not supported." })
        }, ct).ConfigureAwait(false);

        throw new NotSupportedException("Direct storage STS/SAS credential vending is not supported; access lakehouse datasets via governed SQL endpoints.");
    }

    private async ValueTask<TableMetadata> EnsureConsentedRawAccessAsync(
        string tenantId,
        string @namespace,
        string table,
        ClaimsPrincipal principal,
        AccessOperation operation,
        CancellationToken ct)
    {
        var tableId = new TableIdentifier(tenantId, @namespace, table);
        var tableMeta = await _metadataRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);

        var request = new AccessRequest(
            Principal: principal,
            Tenant: new TenantId(tenantId),
            Table: tableId,
            Operation: operation,
            Columns: tableMeta?.Columns.Select(c => c.ColumnName).ToList() ?? [],
            Transport: AccessTransport.Lakehouse,
            TableMetadata: tableMeta);

        var decision = await _accessDecisionPoint.DecideAsync(request, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            var reason = decision.DeniedReasons.FirstOrDefault() ?? $"Access to table '{@namespace}.{table}' denied by policy.";
            throw new SecurityException(reason);
        }

        if (tableMeta == null || !tableMeta.Table.IsActive)
        {
            throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
        }

        return tableMeta;
    }

    private async ValueTask<IcebergTableMetadata?> LoadDeclaredColumnMetadataAsync(
        TableMetadata tableMeta,
        string metadataLocation,
        CancellationToken ct)
    {
        IcebergTableMetadata? loadedMetadata = null;
        try
        {
            loadedMetadata = await _metadataReader.LoadTableMetadataAsync(metadataLocation, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not SecurityException and not OperationCanceledException)
        {
            loadedMetadata = null;
        }

        if (loadedMetadata?.CurrentSchema?.Fields == null)
        {
            return loadedMetadata;
        }

        var declaredSet = new HashSet<string>(
            tableMeta.Columns.Select(c => c.ColumnName),
            StringComparer.OrdinalIgnoreCase);

        var filteredFields = loadedMetadata.CurrentSchema.Fields
            .Where(f => declaredSet.Contains(f.Name))
            .ToList();

        return loadedMetadata with
        {
            CurrentSchema = loadedMetadata.CurrentSchema with { Fields = filteredFields }
        };
    }
}
