namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

public sealed class CatalogDiscoveryService : ICatalogDiscoveryService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IOpenApiIngestionService? _openApiIngestionService;
    private readonly ILogger<CatalogDiscoveryService> _logger;

    public CatalogDiscoveryService(
        ITableMetadataRepository metadataRepository,
        IAuditLogRepository auditLogRepository,
        IRebacEvaluator? rebacEvaluator = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IOpenApiIngestionService? openApiIngestionService = null,
        ILogger<CatalogDiscoveryService>? logger = null)
    {
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        _rebacEvaluator = rebacEvaluator;
        _secretProvider = secretProvider;
        _openApiIngestionService = openApiIngestionService;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogDiscoveryService>.Instance;
    }

    public async Task<IReadOnlyList<CatalogDatasetSummary>> ListDatasetsAsync(RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var visibleTables = new List<CatalogDatasetSummary>();

        foreach (var table in allTables)
        {
            ct.ThrowIfCancellationRequested();

            if (_rebacEvaluator is { IsEnabled: true })
            {
                var checkReq = new RebacCheckRequest(
                    TenantId: context.TenantId.Value,
                    User: context.SubjectId.Value,
                    Relation: RebacTableGate.Relation,
                    Object: RebacTableGate.ObjectId(table.Identifier)
                );

                var checkResult = await _rebacEvaluator.CheckAsync(checkReq, ct).ConfigureAwait(false);
                if (!checkResult.Allowed)
                {
                    continue;
                }
            }

            visibleTables.Add(new CatalogDatasetSummary(
                DatasetId: table.Identifier.ToString(),
                Domain: table.Identifier.Domain,
                Schema: table.Identifier.Schema,
                Table: table.Identifier.TableName,
                SourceType: table.Table.DataSourceType.ToString(),
                Sensitivity: table.Table.Sensitivity.ToString(),
                Description: table.Table.Description,
                IsActive: table.Table.IsActive
            ));
        }

        return visibleTables;
    }

    public async Task<CatalogDatasetDetail?> GetDatasetDetailAsync(TableIdentifier table, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var metadata = await _metadataRepository.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        if (metadata == null)
        {
            return null;
        }

        if (_rebacEvaluator is { IsEnabled: true })
        {
            var checkReq = new RebacCheckRequest(
                TenantId: context.TenantId.Value,
                User: context.SubjectId.Value,
                Relation: RebacTableGate.Relation,
                Object: RebacTableGate.ObjectId(table)
            );

            var checkResult = await _rebacEvaluator.CheckAsync(checkReq, ct).ConfigureAwait(false);
            if (!checkResult.Allowed)
            {
                return null;
            }
        }

        var pkSet = new HashSet<string>(metadata.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        var columnDetails = metadata.Columns.Select(c =>
        {
            var maskingState = "Clear";
            if (metadata.ColumnMaskingRules != null &&
                metadata.ColumnMaskingRules.TryGetValue(c.ColumnName, out var rule))
            {
                maskingState = rule.RuleType;
            }
            else if (c.IsSensitive)
            {
                maskingState = "Masked";
            }

            return new CatalogColumnDetail(
                Name: c.ColumnName,
                Type: c.DataType,
                Sensitivity: c.IsSensitive ? "Sensitive" : "Public",
                MaskingState: maskingState,
                IsPrimaryKey: pkSet.Contains(c.ColumnName),
                IsPiiIndicator: CatalogPiiDetector.IsPii(c.ColumnName, c.IsSensitive)
            );
        }).ToList();

        return new CatalogDatasetDetail(
            DatasetId: metadata.Identifier.ToString(),
            Domain: metadata.Identifier.Domain,
            Schema: metadata.Identifier.Schema,
            Table: metadata.Identifier.TableName,
            Columns: columnDetails,
            PrimaryKeys: metadata.PrimaryKeyColumns,
            Sensitivity: metadata.Table.Sensitivity.ToString(),
            IsActive: metadata.Table.IsActive
        );
    }

    public async Task<IReadOnlyList<CatalogDatasetSummary>> SearchCatalogAsync(string query, string? domain, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var permitted = await ListDatasetsAsync(context, ct).ConfigureAwait(false);
        var q = query?.Trim();

        return permitted
            .Where(d => string.IsNullOrWhiteSpace(domain) || string.Equals(d.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(d => string.IsNullOrWhiteSpace(q) ||
                        d.Table.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        d.Domain.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (d.Description != null && d.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task<IReadOnlyList<CatalogDatasourceSummary>> ListDatasourcesAsync(RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var allTables = await _metadataRepository.GetAllTablesAsync(ct).ConfigureAwait(false);

        // Group by distinct endpoint base URL and domain
        var sources = new Dictionary<string, CatalogDatasourceSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in allTables)
        {
            var domain = table.Identifier.Domain;
            var endpoint = table.Table.HttpEndpoint;
            var key = endpoint != null && !string.IsNullOrWhiteSpace(endpoint.BaseUrl)
                ? $"{domain}:{endpoint.BaseUrl}"
                : $"{domain}:{table.Table.DataSourceType}";

            if (!sources.ContainsKey(key))
            {
                var isConfigured = endpoint?.AuthMode != HttpAuthMode.None ||
                                   !string.IsNullOrEmpty(endpoint?.ApiKeySecretName);

                sources[key] = new CatalogDatasourceSummary(
                    Id: key,
                    Name: $"{domain} data source",
                    Domain: domain,
                    Type: table.Table.DataSourceType.ToString(),
                    BaseUrl: endpoint?.BaseUrl,
                    IsConfigured: isConfigured,
                    Status: table.Table.IsActive ? "active" : "inactive",
                    CreatedAt: DateTimeOffset.UtcNow
                );
            }
        }

        return sources.Values.ToList();
    }

    public async Task<DatasourceRegistrationResult> RegisterDatasourceAsync(DatasourceRegistrationRequest request, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (_openApiIngestionService == null)
        {
            throw new InvalidOperationException("OpenApiIngestionService is not available.");
        }

        var specContent = request.SpecContent;
        if (string.IsNullOrWhiteSpace(specContent) && !string.IsNullOrWhiteSpace(request.SpecUrl))
        {
            if (!Uri.TryCreate(request.SpecUrl, UriKind.Absolute, out var specUri))
            {
                throw new ArgumentException($"Invalid spec URL: {request.SpecUrl}", nameof(request));
            }

            EgressUrlPolicy.ValidateStatic(specUri, isDev: false, enforceHttps: false);

            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromSeconds(30);
            specContent = await httpClient.GetStringAsync(specUri, ct).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(specContent))
        {
            throw new ArgumentException("Either SpecContent or SpecUrl must be provided.", nameof(request));
        }

        var isConfigured = request.Auth != null &&
            (!string.IsNullOrEmpty(request.Auth.Value) ||
             !string.IsNullOrEmpty(request.Auth.ClientSecret) ||
             !string.IsNullOrEmpty(request.Auth.SecretRef));

        // R-55 / SEC REVIEW G5: Securely vault credentials in IKeyVaultSecretProvider and strip plaintext
        var effectiveAuth = request.Auth;
        if (!request.DryRun && request.Auth != null && _secretProvider != null)
        {
            var rawSecret = request.Auth.Value ?? request.Auth.ClientSecret;
            if (!string.IsNullOrEmpty(rawSecret))
            {
                var secretRef = request.Auth.SecretRef ?? $"datasource:{request.Domain}:{request.Name.ToLowerInvariant()}:secret";
                _secretProvider.SetSecret(secretRef, System.Text.Encoding.UTF8.GetBytes(rawSecret));
                effectiveAuth = request.Auth with { SecretRef = secretRef, Value = null, ClientSecret = null };
            }
        }

        // Forward to OpenApiIngestionService with stripped credentials
        var ingestionResult = await _openApiIngestionService.IngestOpenApiJsonAsync(
            openApiJson: specContent,
            domain: request.Domain,
            defaultBaseUrl: request.BaseUrl,
            auth: effectiveAuth,
            dryRun: request.DryRun,
            ct: ct
        ).ConfigureAwait(false);

        var datasourceId = $"ds-{Guid.NewGuid():N}";

        if (!request.DryRun && _auditLogRepository != null)
        {
            // SEC REVIEW G5 / R-55: Never leak raw secrets in AuditLog
            var auditEntry = new AuditLogEntry
            {
                TenantId = context.TenantId,
                ActorSid = context.SubjectId,
                EventType = "DATASOURCE_REGISTERED",
                TargetTable = $"{request.Domain}.*",
                Decision = "ALLOW",
                TraceId = context.CorrelationId ?? Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    DatasourceId = datasourceId,
                    DatasourceName = request.Name,
                    Domain = request.Domain,
                    IsConfigured = isConfigured,
                    AuthType = request.Auth?.Type,
                    CreatedDatasetsCount = ingestionResult.IngestedTablesCount
                })
            };

            await _auditLogRepository.RecordAuditEventAsync(auditEntry, ct).ConfigureAwait(false);
        }

        return new DatasourceRegistrationResult(
            DatasourceId: datasourceId,
            Name: request.Name,
            Domain: request.Domain,
            Success: ingestionResult.Success,
            DryRun: request.DryRun,
            CreatedDatasetsCount: ingestionResult.IngestedTablesCount,
            CreatedDatasets: ingestionResult.IngestedTableNames,
            SkippedDatasets: ingestionResult.SkippedTableNames ?? [],
            Warnings: ingestionResult.Warnings,
            IsConfigured: isConfigured,
            ErrorMessage: ingestionResult.ErrorMessage
        );
    }
}

internal static class CatalogPiiDetector
{
    private static readonly string[] PiiKeywords = ["name", "email", "phone", "iban", "birth", "address", "lat", "lon"];

    public static bool IsPii(string columnName, bool isSensitive = false)
    {
        if (isSensitive) return true;
        if (string.IsNullOrWhiteSpace(columnName)) return false;
        var lower = columnName.ToLowerInvariant();
        return PiiKeywords.Any(k => lower.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
