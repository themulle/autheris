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
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class CatalogDiscoveryService : ICatalogDiscoveryService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IAuditLogRepository _auditLogRepository;
    private readonly GatewayOptions _gatewayOptions;
    private readonly ICatalogVisibilityService? _visibility;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IOpenApiIngestionService? _openApiIngestionService;
    private readonly ICatalogSearchEngine? _searchEngine;
    private readonly ILogger<CatalogDiscoveryService> _logger;
    private readonly long _maxSpecDownloadBytes;
    private readonly IHttpClientFactory? _httpClientFactory;

    /// <summary>
    /// The consent repository and the gateway options are required: the caller class and the visible set depend on them, so a
    /// missing dependency fails closed at construction instead of silently returning the unfiltered catalog.
    /// </summary>
    public CatalogDiscoveryService(
        ITableMetadataRepository metadataRepository,
        IAuditLogRepository auditLogRepository,
        IConsentRepository consentRepository,
        IOptions<GatewayOptions> gatewayOptions,
        ICatalogVisibilityService? visibilityService = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IOpenApiIngestionService? openApiIngestionService = null,
        ICatalogSearchEngine? searchEngine = null,
        ILogger<CatalogDiscoveryService>? logger = null,
        IOptions<CatalogDiscoveryOptions>? options = null,
        IHttpClientFactory? httpClientFactory = null)
    {
        _maxSpecDownloadBytes = options?.Value.MaxSpecDownloadBytes ?? new CatalogDiscoveryOptions().MaxSpecDownloadBytes;
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _auditLogRepository = auditLogRepository ?? throw new ArgumentNullException(nameof(auditLogRepository));
        ArgumentNullException.ThrowIfNull(consentRepository);
        _gatewayOptions = (gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions))).Value
            ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _visibility = visibilityService;
        _secretProvider = secretProvider;
        _openApiIngestionService = openApiIngestionService;
        _searchEngine = searchEngine;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogDiscoveryService>.Instance;
        _httpClientFactory = httpClientFactory;
    }

    private TenancyMode TenancyMode => _gatewayOptions.Tenancy.EffectiveMode;

    public async Task<IReadOnlyList<CatalogDatasetSummary>> ListDatasetsAsync(RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var (permitted, _) = await ResolvePermittedAsync(context, ct).ConfigureAwait(false);
        return permitted.Select(ToSummary).ToList();
    }

    public async Task<CatalogDatasetDetail?> GetDatasetDetailAsync(TableIdentifier table, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        // An unknown table and one the caller may not see (consent, tier, ReBAC) take the same path and answer null (404): no existence oracle.
        var row = _visibility == null
            ? null
            : await _visibility.FindVisibleAsync(CallerOf(context), table, ct).ConfigureAwait(false);
        if (row is not { } visibleRow)
        {
            return null;
        }

        var metadata = visibleRow.Table;

        // Summary (issue #111): identifier, sensitivity and activity only; no columns, keys or indexes.
        if (visibleRow.Shape == CatalogOutputShape.Summary)
        {
            return new CatalogDatasetDetail(
                DatasetId: metadata.Identifier.ToString(),
                Domain: metadata.Identifier.Domain,
                Schema: metadata.Identifier.Schema,
                Table: metadata.Identifier.TableName,
                Columns: [],
                PrimaryKeys: [],
                Sensitivity: metadata.Table.Sensitivity.ToString(),
                IsActive: metadata.Table.IsActive,
                Indexes: []);
        }

        // SD-4: declared keys and indexes are reported only as a whole and only over the columns this response lists.
        var keys = Autheris.Application.Data.Services.TableKeyVisibility.Restrict(metadata, metadata.Columns);
        var pkSet = new HashSet<string>(keys.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

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
            PrimaryKeys: keys.PrimaryKeyColumns,
            Sensitivity: metadata.Table.Sensitivity.ToString(),
            IsActive: metadata.Table.IsActive,
            Indexes: keys.Indexes
        );
    }

    public async Task<IReadOnlyList<CatalogDatasetSummary>> SearchCatalogAsync(string query, string? domain, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var (permitted, all) = await ResolvePermittedAsync(context, ct).ConfigureAwait(false);
        var q = query?.Trim();

        if (string.IsNullOrWhiteSpace(q))
        {
            return permitted
                .Where(d => string.IsNullOrWhiteSpace(domain) || string.Equals(d.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .Select(ToSummary)
                .ToList();
        }

        if (_searchEngine?.IsIndexReady == true)
        {
            var permittedById = permitted.ToDictionary(t => t.Identifier);
            var allById = all.ToDictionary(t => t.Identifier);
            var terms = QueryTerms(q);
            var hits = _searchEngine.Search(
                new CatalogSearchQuery(q, domain, Limit: 25, Mode: CatalogSearchMode.Hybrid),
                tableId => permittedById.ContainsKey(tableId));

            // Result hygiene: rebuilt from the caller's filtered metadata; hits that exist only because of a hidden column are dropped.
            return hits
                .Select(h => ShapeHit(h, permittedById, allById, terms))
                .Where(h => h != null)
                .Select(h => ToSummary(permittedById[h!.TableIdentifier], "Catalog"))
                .ToList();
        }

        return permitted
            .Where(d => string.IsNullOrWhiteSpace(domain) || string.Equals(d.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Identifier.TableName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        d.Identifier.Domain.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (d.Table.Description != null && d.Table.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(ToSummary)
            .ToList();
    }

    public async Task<IReadOnlyList<CatalogSearchHit>> SearchCatalogDetailedAsync(
        CatalogSearchQuery query, 
        RequestContext context, 
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var (permitted, all) = await ResolvePermittedAsync(context, ct).ConfigureAwait(false);
        var permittedById = permitted.ToDictionary(t => t.Identifier);

        if (_searchEngine?.IsIndexReady == true)
        {
            var allById = all.ToDictionary(t => t.Identifier);
            var terms = QueryTerms(query.QueryText);
            return _searchEngine.Search(query, tableId => permittedById.ContainsKey(tableId))
                .Select(h => ShapeHit(h, permittedById, allById, terms))
                .Where(h => h != null)
                .Select(h => h!)
                .ToList();
        }

        var q = query.QueryText?.Trim();
        return permitted
            .Where(d => string.IsNullOrWhiteSpace(query.DomainFilter) || string.Equals(d.Identifier.Domain, query.DomainFilter, StringComparison.OrdinalIgnoreCase))
            .Where(d => string.IsNullOrWhiteSpace(q) ||
                        d.Identifier.TableName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        d.Identifier.Domain.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (d.Table.Description != null && d.Table.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(query.Limit)
            .Select(d => new CatalogSearchHit(
                TableIdentifier: d.Identifier,
                DisplayName: $"{d.Identifier.Domain}.{d.Identifier.TableName}",
                Description: d.Table.Description,
                Domain: d.Identifier.Domain,
                Sensitivity: d.Table.Sensitivity.ToString(),
                CombinedScore: 1.0,
                Bm25Score: 1.0,
                VectorScore: null,
                MatchedTerms: string.IsNullOrWhiteSpace(q) ? [] : [q],
                RelevantColumns: [],
                RelatedJoinPaths: [],
                SuggestedGraphQlField: d.CatalogView == CatalogView.Summary ? null : $"{d.Identifier.Domain}_{d.Identifier.TableName}"
            ))
            .ToList();
    }

    public async Task<IReadOnlyList<CatalogDatasourceSummary>> ListDatasourcesAsync(RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        // Administrator metadata (endpoint URLs, authentication state): only the administrator classes get an answer; everybody else
        // (including a claim-less GovernanceAdmin in Multi mode) gets the empty list. A tenant-scoped GovernanceAdmin sees the data
        // sources of the tables of its tenant-confined set, grouped per table, so a foreign table in the same domain never leaks its BaseUrl.
        var scope = CallerScopeResolver.Resolve(context.User, TenancyMode);
        if (!CallerScopeResolver.IsAdminView(scope))
        {
            CallerScopeDiagnostics.ReportIfClaimlessAdmin(_logger, context.User, scope);
            return [];
        }

        var tables = _visibility == null
            ? []
            : (await _visibility.GetVisibleAsync(CallerOf(context), contract: null, ct).ConfigureAwait(false)).Tables;

        // Group by distinct endpoint base URL and domain
        var sources = new Dictionary<string, CatalogDatasourceSummary>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables)
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

    /// <summary>
    /// The channel caller. The REST subject (<c>context.SubjectId</c>) and the principal SID must agree (SEC-CAT138-03: the stricter of
    /// both applies); a mismatch answers as no caller, so the service returns the empty set.
    /// </summary>
    private static CatalogCaller CallerOf(RequestContext context)
    {
        var sid = context.User.GetUserSid();
        var principal = sid != null && string.Equals(sid.Value.Value, context.SubjectId.Value, StringComparison.Ordinal) ? context.User : null;
        return new CatalogCaller(principal, context.TenantId, CatalogChannel.Rest);
    }

    /// <summary>
    /// The tables the caller may discover (one visibility service: consent, tier shaping and ReBAC) together with the unfiltered
    /// catalog (only used to tell which columns a caller cannot see). Any failure answers the empty set, never the unfiltered catalog.
    /// </summary>
    private async Task<(IReadOnlyList<TableMetadata> Permitted, IReadOnlyList<TableMetadata> All)> ResolvePermittedAsync(RequestContext context, CancellationToken ct)
    {
        if (_visibility == null)
        {
            return ([], []);
        }

        var set = await _visibility.GetVisibleAsync(CallerOf(context), contract: null, ct).ConfigureAwait(false);
        CallerScopeDiagnostics.ReportIfClaimlessAdmin(_logger, context.User, set.Scope);
        return (set.Tables, set.AllTables);
    }

    private static CatalogDatasetSummary ToSummary(TableMetadata table) => ToSummary(table, table.Table.DataSourceType.ToString());

    private static CatalogDatasetSummary ToSummary(TableMetadata table, string sourceType) => new(
        DatasetId: table.Identifier.ToString(),
        Domain: table.Identifier.Domain,
        Schema: table.Identifier.Schema,
        Table: table.Identifier.TableName,
        // A Summary row discloses only the allow-listed fields; the source type is not one of them.
        SourceType: table.CatalogView == CatalogView.Summary ? string.Empty : sourceType,
        Sensitivity: table.Table.Sensitivity.ToString(),
        Description: table.Table.Description,
        IsActive: table.Table.IsActive);

    private static IReadOnlyList<string> QueryTerms(string? query) =>
        (query ?? string.Empty)
            .Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static readonly char[] QuerySeparators = [' ', '\t', '\r', '\n', ',', ';', '.', '(', ')', '"', '\'', '/', '\\', '-', '_', ':'];

    /// <summary>
    /// Result hygiene for one index hit (W0-C1). The index is built from the whole catalog including column names, so a hit
    /// and everything derived from it is rebuilt from the caller's filtered metadata:
    /// <c>RelevantColumns</c> only from visible columns, <c>MatchedTerms</c> only terms found in the identifier, the description or a visible
    /// column, join paths only over visible tables and visible columns, description and sensitivity from the filtered row.
    /// For a table with at least one non-visible column a hit that has no lexical match in visible text (a hit caused by a hidden
    /// column, or a vector-only hit) is dropped; semantic-only recall is therefore reduced for column-restricted callers.
    /// </summary>
    private static CatalogSearchHit? ShapeHit(
        CatalogSearchHit hit,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> permitted,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> all,
        IReadOnlyList<string> queryTerms)
    {
        if (!permitted.TryGetValue(hit.TableIdentifier, out var visible))
        {
            return null;
        }

        var visibleColumnNames = visible.Columns.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restricted = !all.TryGetValue(hit.TableIdentifier, out var full) ||
                         full.Columns.Any(c => !visibleColumnNames.Contains(c.ColumnName));

        IReadOnlyList<string> matchedTerms = hit.MatchedTerms;
        if (restricted)
        {
            var text = VisibleTokens(visible);
            matchedTerms = hit.MatchedTerms
                .Where(t => !string.IsNullOrWhiteSpace(t) && text.Contains(t))
                .ToList();
            var lexical = matchedTerms.Count > 0 || queryTerms.Any(text.Contains);
            if (!lexical)
            {
                return null;
            }
        }

        var relevantColumns = visible.Columns
            .Where(c => matchedTerms.Any(term => c.ColumnName.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.ColumnName)
            .Take(5)
            .ToList();

        var relations = hit.RelatedJoinPaths
            .Where(r => visibleColumnNames.Contains(r.FromColumn) &&
                        permitted.TryGetValue(r.ToTable, out var target) &&
                        target.Columns.Any(c => string.Equals(c.ColumnName, r.ToColumn, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return hit with
        {
            Description = visible.Table.Description,
            SuggestedGraphQlField = visible.CatalogView == CatalogView.Summary ? null : hit.SuggestedGraphQlField,
            Sensitivity = visible.Table.Sensitivity?.ToString() ?? "NORMAL",
            MatchedTerms = matchedTerms,
            RelevantColumns = relevantColumns,
            RelatedJoinPaths = relations
        };
    }

    // Whole tokens of the visible text (identifier, description, visible columns with type and description), split like the query terms
    // and additionally on underscores, so a short term never matches inside an unrelated hidden-column word.
    private static HashSet<string> VisibleTokens(TableMetadata table)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(table.Identifier.Domain).Append(' ').Append(table.Identifier.Schema).Append(' ').Append(table.Identifier.TableName).Append(' ')
          .Append(table.Table.Description).Append(' ');
        foreach (var c in table.Columns)
        {
            sb.Append(c.ColumnName).Append(' ').Append(c.DataType).Append(' ').Append(c.Description).Append(' ');
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in sb.ToString().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tokens.Add(t);
        }

        return tokens;
    }

    private static readonly char[] TokenSeparators = [' ', '\t', '\r', '\n', ',', ';', '.', '(', ')', '"', '\'', '/', '\\', '-', '_', ':'];

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

            await EgressUrlPolicy.ValidateResolvedAsync(specUri, isDev: false, ct).ConfigureAwait(false);

            if (_httpClientFactory != null)
            {
                using var httpClient = _httpClientFactory.CreateClient("OpenApiIngestion");
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                using var specRequest = new HttpRequestMessage(HttpMethod.Get, specUri);
                using var specResponse = await httpClient.SendAsync(specRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                specResponse.EnsureSuccessStatusCode();
                specContent = await BoundedHttpContentReader.ReadStringAsync(specResponse.Content, _maxSpecDownloadBytes, ct).ConfigureAwait(false);
            }
            else
            {
                using var handler = SecureOutboundHttp.CreatePrimaryHandler(
                    isDevelopment: false,
                    allowlist: EgressAllowlist.Create(null, null),
                    allowUntrustedCertificates: false,
                    resolver: SecureOutboundHttp.DefaultResolver);

                using var httpClient = new HttpClient(handler);
                httpClient.Timeout = TimeSpan.FromSeconds(30);
                using var specRequest = new HttpRequestMessage(HttpMethod.Get, specUri);
                using var specResponse = await httpClient.SendAsync(specRequest, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                specResponse.EnsureSuccessStatusCode();
                specContent = await BoundedHttpContentReader.ReadStringAsync(specResponse.Content, _maxSpecDownloadBytes, ct).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrWhiteSpace(specContent))
        {
            throw new ArgumentException("Either SpecContent or SpecUrl must be provided.", nameof(request));
        }

        // SR-H-03: a client-supplied SecretRef is never trusted as vault key; it must lie inside the datasource's own namespace.
        var secretNamespace = $"{DatasourceSecretRefPolicy.DomainPrefix(request.Domain)}{request.Name.ToLowerInvariant()}:";
        DatasourceSecretRefPolicy.ValidateClientRef(request.Auth?.SecretRef, secretNamespace);

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
                var secretRef = request.Auth.SecretRef ?? $"{secretNamespace}secret";
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
