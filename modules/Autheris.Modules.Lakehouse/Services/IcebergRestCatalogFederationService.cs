namespace Autheris.Extensions.Lakehouse.Services;

using Autheris.Modules.Lakehouse.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
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
    private readonly IAuditLogRepository _auditRepository;
    private readonly IAccessDecisionPoint _accessDecisionPoint;
    private readonly Autheris.Application.DataSources.IDataSourceTransportGate _transportGate;

    public IcebergRestCatalogFederationService(
        IIcebergMetadataReader metadataReader,
        ITableMetadataRepository metadataRepo,
        IOptions<GatewayOptions> options,
        ILogger<IcebergRestCatalogFederationService> logger,
        IAuditLogRepository auditRepository,
        IAccessDecisionPoint accessDecisionPoint,
        IConsentRepository? consentRepo = null,
        Autheris.Application.DataSources.IDataSourceTransportGate? transportGate = null)
    {
        _transportGate = transportGate ?? Autheris.Application.DataSources.TransportGateFallback.Current;
        _metadataReader = metadataReader ?? throw new ArgumentNullException(nameof(metadataReader));
        _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _auditRepository = auditRepository ?? throw new ArgumentNullException(nameof(auditRepository));
        ArgumentNullException.ThrowIfNull(accessDecisionPoint);
        _accessDecisionPoint = accessDecisionPoint;
        _consentRepo = consentRepo;
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

        if (principal.GetUserSid() is not { } userSid)
        {
            return Array.Empty<TableMetadata>();
        }

        var tenantTables = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
            _metadataRepo,
            new Autheris.Application.Catalog.CatalogTableQuery(
                DataSource: tenantId,
                Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
            t => string.Equals(t.Identifier.Domain, tenantId, StringComparison.OrdinalIgnoreCase) &&
                 (t.Table.DataSourceType == DataSourceType.LakehouseIceberg || t.Table.DataSourceType == DataSourceType.LakehouseDelta),
            ct: ct).ConfigureAwait(false);

        var visible = await CatalogVisibility.VisibleTablesAsync(
            tenantTables,
            principal,
            new TenantId(tenantId),
            _consentRepo,
            contract: null,
            Autheris.Application.Tenancy.TenantResolver.EffectiveDefaultOf(_options.Value.Tenancy),
            _options.Value.Catalog.SharedTables,
            ct).ConfigureAwait(false);

        return await FilterByTransportAsync(visible, ct).ConfigureAwait(false);
    }

    /// <summary>Hides tables of data sources whose transport policy denies this ingress (same gate as load-table); Iceberg is served as the REST transport.</summary>
    private async ValueTask<IReadOnlyList<TableMetadata>> FilterByTransportAsync(IReadOnlyList<TableMetadata> tables, CancellationToken ct)
    {
        if (tables.Count == 0)
        {
            return tables;
        }

        var verdicts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var allowed = new List<TableMetadata>(tables.Count);
        foreach (var table in tables)
        {
            var source = Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(table);
            var key = source ?? string.Empty;
            if (!verdicts.TryGetValue(key, out var ok))
            {
                try
                {
                    await _transportGate.EnsureCatalogSourceAsync(source, ct).ConfigureAwait(false);
                    ok = true;
                }
                catch (Autheris.Domain.Exceptions.TransportNotAllowedException)
                {
                    ok = false;
                }

                verdicts[key] = ok;
            }

            if (ok)
            {
                allowed.Add(table);
            }
        }

        return allowed;
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

    private const string MissingSubjectReason = "missing subject identifier";

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

        var target = $"{tenantId}.{@namespace}.{table}";

        if (principal.Identity?.IsAuthenticated != true)
        {
            _logger.LogWarning(
                "Unauthenticated attempt to load Iceberg table '{Namespace}.{Table}' for tenant '{Tenant}'.",
                LogSanitizer.Sanitize(@namespace),
                LogSanitizer.Sanitize(table),
                LogSanitizer.Sanitize(tenantId));
            await AuditDenyAsync("Iceberg.LoadTable", tenantId, principal, target, "Unauthenticated caller", ct).ConfigureAwait(false);
            throw new SecurityException($"Unauthorized access to table '{@namespace}.{table}'.");
        }

        if (principal.GetUserSid() is not { } actorSid)
        {
            await AuditDenyAsync("Iceberg.LoadTable", tenantId, principal, target, MissingSubjectReason, ct).ConfigureAwait(false);
            throw new SecurityException(MissingSubjectReason);
        }

        TableMetadata tableMeta;
        try
        {
            tableMeta = await EnsureConsentedRawAccessAsync(tenantId, @namespace, table, principal, ct).ConfigureAwait(false);
        }
        catch (SecurityException ex)
        {
            await AuditDenyAsync("Iceberg.LoadTable", tenantId, principal, target, ex.Message, ct).ConfigureAwait(false);
            throw;
        }

        var location = tableMeta.Table.Location ?? $"lakehouse/{tenantId}/{@namespace}/{table}";
        LakehouseLocationGuard.EnsureNoTraversal(location, location);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var metadataLocation = IcebergMetadataReader.IsExplicitMetadataFileLocation(location)
            ? location
            : await _metadataReader.ResolveMetadataLocationAsync(location, ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(metadataLocation))
        {
            throw new InvalidOperationException($"Iceberg metadata location could not be resolved for '{location}'.");
        }

        LakehouseLocationGuard.EnsureNoTraversal(metadataLocation, location);

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

        var target = $"{tenantId}.{@namespace}.{table}";

        if (principal.Identity?.IsAuthenticated != true)
        {
            await AuditDenyAsync("Iceberg.VendCredential", tenantId, principal, target, "Unauthenticated caller", ct).ConfigureAwait(false);
            throw new SecurityException($"Unauthorized access to table '{@namespace}.{table}'.");
        }

        if (principal.GetUserSid() is null)
        {
            await AuditDenyAsync("Iceberg.VendCredential", tenantId, principal, target, MissingSubjectReason, ct).ConfigureAwait(false);
            throw new SecurityException(MissingSubjectReason);
        }

        try
        {
            await EnsureConsentedRawAccessAsync(tenantId, @namespace, table, principal, ct).ConfigureAwait(false);
        }
        catch (SecurityException ex)
        {
            await AuditDenyAsync("Iceberg.VendCredential", tenantId, principal, target, ex.Message, ct).ConfigureAwait(false);
            throw;
        }

        await AuditDenyAsync("Iceberg.VendCredential", tenantId, principal, target, "Direct credential vending not supported.", ct).ConfigureAwait(false);

        throw new NotSupportedException("Direct storage STS/SAS credential vending is not supported; access lakehouse datasets via governed SQL endpoints.");
    }

    private async ValueTask<TableMetadata> EnsureConsentedRawAccessAsync(
        string tenantId,
        string @namespace,
        string table,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        var tableId = new TableIdentifier(tenantId, @namespace, table);
        var tableMeta = await _metadataRepo.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (tableMeta == null || !tableMeta.Table.IsActive)
        {
            throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
        }

        // The source must allow the lakehouse (REST) transport before any decision (same order as the other transports).
        await _transportGate.EnsureCatalogSourceAsync(
            Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(tableMeta),
            ct).ConfigureAwait(false);

        var request = new AccessRequest(
            SecurityPrincipalContext.FromPrincipal(principal, new TenantId(tenantId)),
            tableMeta,
            AccessOperation.Read,
            AccessTransport.Lakehouse);

        var decision = await _accessDecisionPoint.DecideAsync(request, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            var reason = decision.DeniedReasons.FirstOrDefault() ?? $"Access to table '{@namespace}.{table}' denied by policy.";
            throw new SecurityException(reason);
        }

        // The catalog path cannot enforce row filters or column restrictions; deny rather than ignore them.
        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql)
            || !string.IsNullOrWhiteSpace(decision.MandatoryRowPredicateSql)
            || decision.RowPolicy != null
            || decision.ColumnAccess.Values.Any(level => level != ColumnAccessLevel.Clear)
            || !decision.HasUnconstrainedColumnAllow
            || tableMeta.Columns.Any(column => decision.GetEffectiveColumnAccess(column.ColumnName, tableMeta) != ColumnAccessLevel.Clear)
            || tableMeta.ColumnMaskingRules.Keys.Any(column => decision.GetEffectiveColumnAccess(column, tableMeta) != ColumnAccessLevel.Clear))
        {
            throw new SecurityException($"Access to table '{@namespace}.{table}' denied by policy.");
        }

        return tableMeta;
    }

    private Task AuditDenyAsync(
        string eventType,
        string tenantId,
        ClaimsPrincipal principal,
        string target,
        string reason,
        CancellationToken ct) =>
        _auditRepository.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = new TenantId(tenantId),
            EventType = eventType,
            ActorSid = principal.GetUserSid() ?? new Sid("anonymous"),
            TargetTable = target,
            Decision = "DENY",
            DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { reason })
        }, ct);

    private async ValueTask<IcebergTableMetadata> LoadDeclaredColumnMetadataAsync(
        TableMetadata tableMeta,
        string metadataLocation,
        CancellationToken ct)
    {
        var loadedMetadata = await _metadataReader.LoadTableMetadataAsync(metadataLocation, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Iceberg table metadata could not be loaded from '{metadataLocation}'.");

        if (loadedMetadata.CurrentSchema?.Fields == null)
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
