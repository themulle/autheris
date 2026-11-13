namespace Autheris.Application.Catalog.Services;

using Autheris.Application.Audit;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Services;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class CatalogDiscoveryService : ICatalogDiscoveryService
{
    private readonly ITableMetadataRepository _metadataRepository;
    private readonly IAuditWriter _auditWriter;
    private readonly GatewayOptions _gatewayOptions;
    private readonly ICatalogVisibilityService? _visibility;
    private readonly IKeyVaultSecretProvider? _secretProvider;
    private readonly IOpenApiIngestionService? _openApiIngestionService;
    private readonly ICatalogSearchEngine? _searchEngine;
    private readonly IConsentResolutionService? _consentResolution;
    private readonly IConsentRepository? _consentRepository;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CatalogDiscoveryService> _logger;
    private readonly Autheris.Application.DataSources.IDataSourceOptionsResolver? _transportResolver;
    private readonly long _maxSpecDownloadBytes;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureCatalog? _procedureCatalog;
    private readonly Autheris.Application.Mcp.Interfaces.IGraphQlCatalogMap? _graphQlMap;

    /// <summary>
    /// The gateway options are required: the caller class and the visible set depend on them, so a
    /// missing dependency fails closed at construction instead of silently returning the unfiltered catalog.
    /// </summary>
    public CatalogDiscoveryService(
        ITableMetadataRepository metadataRepository,
        IAuditWriter auditWriter,
        IOptions<GatewayOptions> gatewayOptions,
        ICatalogVisibilityService? visibilityService = null,
        IKeyVaultSecretProvider? secretProvider = null,
        IOpenApiIngestionService? openApiIngestionService = null,
        ICatalogSearchEngine? searchEngine = null,
        ILogger<CatalogDiscoveryService>? logger = null,
        IOptions<CatalogDiscoveryOptions>? options = null,
        IHttpClientFactory? httpClientFactory = null,
        Autheris.Application.Procedures.Interfaces.IProcedureCatalog? procedureCatalog = null,
        IConsentResolutionService? consentResolution = null,
        IConsentRepository? consentRepository = null,
        TimeProvider? timeProvider = null,
        Autheris.Application.DataSources.IDataSourceOptionsResolver? transportResolver = null,
        Autheris.Application.Mcp.Interfaces.IGraphQlCatalogMap? graphQlMap = null)
    {
        _transportResolver = transportResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _consentResolution = consentResolution;
        _consentRepository = consentRepository;
        _maxSpecDownloadBytes = options?.Value.MaxSpecDownloadBytes ?? new CatalogDiscoveryOptions().MaxSpecDownloadBytes;
        _metadataRepository = metadataRepository ?? throw new ArgumentNullException(nameof(metadataRepository));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _gatewayOptions = (gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions))).Value
            ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _visibility = visibilityService;
        _secretProvider = secretProvider;
        _openApiIngestionService = openApiIngestionService;
        _searchEngine = searchEngine;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<CatalogDiscoveryService>.Instance;
        _httpClientFactory = httpClientFactory;
        _procedureCatalog = procedureCatalog;
        _graphQlMap = graphQlMap;
    }

    private TenantId? CatalogOwner => TenantResolver.EffectiveDefaultOf(_gatewayOptions.Tenancy);

    public async Task<IReadOnlyList<CatalogDatasetSummary>> ListDatasetsAsync(RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var (permitted, _) = await VisibleForAsync(context, ct).ConfigureAwait(false);
        return permitted.Select(t => ToSummary(t, t.Table.DataSourceType.ToString(), context.Language)).ToList();
    }

    public async Task<CatalogDatasetDetail?> GetDatasetDetailAsync(TableIdentifier table, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        if (string.Equals(table.Schema, "procedure", StringComparison.OrdinalIgnoreCase))
        {
            if (_procedureCatalog == null)
            {
                return null;
            }

            var proc = await _procedureCatalog.FindVisibleAsync(table.TableName, context.User, context.TenantId, ct).ConfigureAwait(false);
            if (proc == null ||
                proc.Definition.Mode != ProcedureMode.Read ||
                string.IsNullOrWhiteSpace(proc.Definition.ResultTable))
            {
                return null;
            }

            var def = proc.Definition;
            var columns = def.Parameters.Select(p => new CatalogColumnDetail(
                Name: p.Name,
                Type: p.SqlType,
                Sensitivity: "Public",
                MaskingState: "Clear",
                IsPrimaryKey: false,
                IsPiiIndicator: false)).ToList();

            return new CatalogDatasetDetail(
                DatasetId: $"procedure:{def.Name}",
                Domain: def.CatalogDomain,
                Schema: "procedure",
                Table: def.Name,
                Columns: columns,
                PrimaryKeys: [],
                Sensitivity: "NORMAL",
                IsActive: true,
                Indexes: []);
        }

        // An unknown table and one the caller may not see (consent, tier) take the same path and answer null (404): no existence oracle.
        var row = _visibility == null
            ? null
            : await _visibility.FindVisibleAsync(CallerOf(context), table, ct).ConfigureAwait(false);
        if (row is not { } visibleRow)
        {
            return null;
        }

        var metadata = visibleRow.Table;
        var lang = context.Language;
        var tableResolved = !string.IsNullOrWhiteSpace(lang) ? metadata.Table.Texts.Resolve(lang) : null;
        var tableDisplayName = tableResolved?.Title ?? metadata.Table.DisplayName;
        var tableDescription = tableResolved?.Description ?? metadata.Table.Description;
        var tableLongDescription = tableResolved?.LongDescription ?? metadata.Table.LongDescription;

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
                Indexes: [],
                DisplayName: tableDisplayName,
                Description: tableDescription,
                LongDescription: null,
                Language: lang,
                Size: DatasetSizeClassifier.Resolve(metadata));
        }

        // I404-SC-L1/L2/L4: declared keys and indexes are reported only as a whole and only when every column is Clear for this
        // caller: the caller's resolved access level (shared with describe_dataset) and the catalog masking state must both say Clear.
        var callerClear = await ResolveCallerClearColumnsAsync(metadata, context, ct).ConfigureAwait(false);
        var clear = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in metadata.Columns)
        {
            if (callerClear.Contains(column.ColumnName)
                && string.Equals(MaskingStateOf(metadata, column), "Clear", StringComparison.OrdinalIgnoreCase))
            {
                clear.Add(column.ColumnName);
            }
        }

        var keys = Autheris.Application.DataSources.Services.DatasetIndexProjection.Project(metadata, clear);
        var pkSet = new HashSet<string>(keys.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        var columnDetails = metadata.Columns.Select(c =>
        {
            var maskingState = MaskingStateOf(metadata, c);

            var colResolved = !string.IsNullOrWhiteSpace(lang) ? c.Texts.Resolve(lang) : null;
            return new CatalogColumnDetail(
                Name: c.ColumnName,
                Type: c.DataType,
                Sensitivity: c.IsSensitive ? "Sensitive" : "Public",
                MaskingState: maskingState,
                IsPrimaryKey: pkSet.Contains(c.ColumnName),
                IsPiiIndicator: CatalogPiiDetector.IsPii(c.ColumnName, c.IsSensitive),
                DisplayName: colResolved?.Title ?? c.DisplayName ?? c.ColumnName,
                Description: colResolved?.Description ?? c.Description
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
            Indexes: keys.Indexes,
            DisplayName: tableDisplayName,
            Description: tableDescription,
            LongDescription: tableLongDescription,
            Language: lang,
            // Issue #390: parallel to PrimaryKeys; null unless a column is descending, and never for a key with a hidden column (Restrict drops it).
            PrimaryKeyDirections: KeyDirections.AnyDescending(keys.PrimaryKeyDirections) ? KeyDirections.ToWire(keys.PrimaryKeyDirections) : null,
            Size: DatasetSizeClassifier.Resolve(metadata)
        );
    }

    /// <summary>
    /// The columns that are Clear for the REST caller, from the same resolution as describe_dataset
    /// (<see cref="CatalogCallerColumnLevels"/>). A missing dependency, no SID, or any failure except cancellation yields the empty
    /// set, which omits every key and index (fail closed).
    /// </summary>
    private async Task<IReadOnlySet<string>> ResolveCallerClearColumnsAsync(TableMetadata metadata, RequestContext context, CancellationToken ct)
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var principal = context.User;
            var scope = CallerScopeResolver.Resolve(principal, CatalogOwner);
            var resolution = _consentResolution ?? new ConsentResolutionService();
            if (CallerScopeResolver.IsAdminView(scope))
            {
                return Autheris.Application.DataSources.Services.DatasetIndexProjection.ClearSet(
                    CatalogCallerColumnLevels.Resolve(resolution, principal, metadata, [], isAdmin: true, isGranted: false, bypass: false, _logger));
            }

            var userSid = principal.GetUserSid();
            if (_consentRepository == null || userSid == null || scope == CallerScope.None)
            {
                return empty;
            }

            var tenantId = principal.GetTenantId();
            var subjects = principal.GetGroupSids().Append(userSid.Value).ToList();
            var consents = await _consentRepository.GetAllActiveConsentsForSubjectsAsync(
                subjects, principal.GetUserRoles(), _timeProvider.GetUtcNow(), tenantId, ct).ConfigureAwait(false);
            var granted = consents.Any(c => c.Effect == ConsentEffect.Allow && c.TableIdentifier == metadata.Identifier && c.TenantId == tenantId);
            return Autheris.Application.DataSources.Services.DatasetIndexProjection.ClearSet(
                CatalogCallerColumnLevels.Resolve(resolution, principal, metadata, consents, isAdmin: false, isGranted: granted, bypass: false, _logger));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Catalog detail key visibility lookup failed for {Dataset} ({ExceptionType}); keys and indexes omitted.", metadata.Identifier.ToString(), ex.GetType().Name);
            return empty;
        }
    }

    private static string MaskingStateOf(TableMetadata metadata, TableColumn c)
    {
        if (metadata.ColumnMaskingRules != null && metadata.ColumnMaskingRules.TryGetValue(c.ColumnName, out var rule))
        {
            return rule.RuleType;
        }

        return c.IsSensitive ? "Masked" : "Clear";
    }

    private async Task<IReadOnlyList<RegisteredProcedure>> GetVisibleProceduresAsync(RequestContext context, CancellationToken ct)
    {
        if (_procedureCatalog == null)
        {
            return [];
        }

        var visible = await _procedureCatalog.ListVisibleAsync(context.User, context.TenantId, ct).ConfigureAwait(false);
        return visible.Where(p => p.Definition.Mode == ProcedureMode.Read && !string.IsNullOrWhiteSpace(p.Definition.ResultTable)).ToList();
    }

    public async Task<IReadOnlyList<CatalogDatasetSummary>> SearchCatalogAsync(string query, string? domain, RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var (permitted, all) = await VisibleForAsync(context, ct).ConfigureAwait(false);
        var visibleProcedures = await GetVisibleProceduresAsync(context, ct).ConfigureAwait(false);
        var q = query?.Trim();

        var procSummaries = visibleProcedures
            .Where(p => string.IsNullOrWhiteSpace(domain) || string.Equals(p.Definition.CatalogDomain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.IsNullOrWhiteSpace(q) ||
                        p.Definition.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.Definition.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (p.Definition.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        p.Definition.Parameters.Any(param => param.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new CatalogDatasetSummary(
                DatasetId: $"procedure:{p.Definition.Name}",
                Domain: p.Definition.CatalogDomain,
                Schema: "procedure",
                Table: p.Definition.Name,
                SourceType: "procedure",
                Sensitivity: "NORMAL",
                Description: p.Definition.Summary,
                IsActive: true))
            .ToList();

        if (string.IsNullOrWhiteSpace(q))
        {
            return permitted
                .Where(d => string.IsNullOrWhiteSpace(domain) || string.Equals(d.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
                .Select(t => ToSummary(t, context.Language))
                .Concat(procSummaries)
                .ToList();
        }

        if (_searchEngine?.IsIndexReady == true)
        {
            var permittedById = permitted.ToDictionary(t => t.Identifier);
            var allById = all.ToDictionary(t => t.Identifier);
            var terms = QueryTerms(q);
            var singleIdentifier = IsSingleIdentifierQuery(q, terms);
            var hits = await _searchEngine.SearchAsync(
                new CatalogSearchQuery(q, domain, Limit: 25, Mode: CatalogSearchMode.Hybrid),
                tableId => permittedById.ContainsKey(tableId),
                ct).ConfigureAwait(false);

            // Result hygiene: rebuilt from the caller's filtered metadata; hits that exist only because of a hidden column are dropped.
            var tableHits = hits
                .Select(h => ShapeHit(h, permittedById, allById, terms, singleIdentifier))
                .Where(h => h != null)
                .Select(h => ToSummary(permittedById[h!.TableIdentifier], "Catalog", context.Language))
                .ToList();

            return tableHits.Concat(procSummaries).ToList();
        }

        var matches = permitted
            .Where(d => string.IsNullOrWhiteSpace(domain) || string.Equals(d.Identifier.Domain, domain, StringComparison.OrdinalIgnoreCase))
            .Where(d => d.Identifier.TableName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        d.Identifier.Domain.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (d.Table.Description != null && d.Table.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(t => ToSummary(t, context.Language))
            .Concat(procSummaries)
            .ToList();

        return matches;
    }

    public async Task<IReadOnlyList<CatalogSearchHit>> SearchCatalogDetailedAsync(
        CatalogSearchQuery query, 
        RequestContext context, 
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        var (permitted, all) = await VisibleForAsync(context, ct).ConfigureAwait(false);
        var permittedById = permitted.ToDictionary(t => t.Identifier);
        var visibleProcedures = await GetVisibleProceduresAsync(context, ct).ConfigureAwait(false);
        var q = query.QueryText?.Trim();

        var procHits = visibleProcedures
            .Where(p => string.IsNullOrWhiteSpace(query.DomainFilter) || string.Equals(p.Definition.CatalogDomain, query.DomainFilter, StringComparison.OrdinalIgnoreCase))
            .Where(p => string.IsNullOrWhiteSpace(q) ||
                        p.Definition.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.Definition.Summary.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (p.Definition.Description?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                        p.Definition.Parameters.Any(param => param.Name.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Select(p => new CatalogSearchHit(
                TableIdentifier: new TableIdentifier(p.Definition.CatalogDomain, "procedure", p.Definition.Name),
                DisplayName: p.Definition.Summary,
                Description: p.Definition.Description ?? p.Definition.Summary,
                Domain: p.Definition.CatalogDomain,
                Sensitivity: "NORMAL",
                CombinedScore: 1.0,
                Bm25Score: 1.0,
                VectorScore: null,
                MatchedTerms: string.IsNullOrWhiteSpace(q) ? [] : [q],
                RelevantColumns: p.Definition.Parameters.Select(param => param.Name).ToList(),
                RelatedJoinPaths: [],
                SuggestedGraphQlField: null,
                Kind: "procedure",
                DatasetId: $"procedure:{p.Definition.Name}"))
            .ToList();

        if (_searchEngine?.IsIndexReady == true)
        {
            var allById = all.ToDictionary(t => t.Identifier);
            var terms = QueryTerms(query.QueryText);
            var singleIdentifier = IsSingleIdentifierQuery(query.QueryText, terms);
            var rawHits = await _searchEngine.SearchAsync(query, tableId => permittedById.ContainsKey(tableId), ct).ConfigureAwait(false);
            var tableHits = rawHits
                .Select(h => ShapeHit(h, permittedById, allById, terms, singleIdentifier))
                .Where(h => h != null)
                .Select(h => h!)
                .Take(query.Limit);

            return (await WithGraphQlFieldsAsync(tableHits, permittedById, ct).ConfigureAwait(false)).Concat(procHits).Take(query.Limit).ToList();
        }

        var fallbackHits = permitted
            .Where(d => string.IsNullOrWhiteSpace(query.DomainFilter) || string.Equals(d.Identifier.Domain, query.DomainFilter, StringComparison.OrdinalIgnoreCase))
            .Where(d => string.IsNullOrWhiteSpace(q) ||
                        d.Identifier.TableName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        d.Identifier.Domain.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        (d.Table.Description != null && d.Table.Description.Contains(q, StringComparison.OrdinalIgnoreCase)))
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
                SuggestedGraphQlField: null))
            .Take(query.Limit);

        return (await WithGraphQlFieldsAsync(fallbackHits, permittedById, ct).ConfigureAwait(false)).Concat(procHits).Take(query.Limit).ToList();
    }

    /// <summary>
    /// Issue #682 A: the suggested GraphQL field of a table hit is its root field in the catalog GraphQL schema, never derived
    /// from the id (the root field includes the schema and is sanitized). Null for a table without a root field, for a Summary
    /// row and when no GraphQL map is registered.
    /// </summary>
    private async Task<List<CatalogSearchHit>> WithGraphQlFieldsAsync(
        IEnumerable<CatalogSearchHit> hits,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> permitted,
        CancellationToken ct)
    {
        var result = new List<CatalogSearchHit>();
        foreach (var hit in hits)
        {
            var field = _graphQlMap != null &&
                        permitted.TryGetValue(hit.TableIdentifier, out var row) &&
                        row.CatalogView != CatalogView.Summary
                ? await _graphQlMap.GetRootFieldAsync(hit.TableIdentifier, ct).ConfigureAwait(false)
                : null;
            result.Add(hit with { SuggestedGraphQlField = field });
        }

        return result;
    }

    public async Task<IReadOnlyList<CatalogDatasourceSummary>> ListDatasourcesAsync(RequestContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();

        // Administrator metadata (endpoint URLs, authentication state): only the administrator classes get an answer; everybody else
        // (including a GovernanceAdmin without a resolvable tenant) gets the empty list. A tenant-scoped GovernanceAdmin sees the data
        // sources of the tables of its tenant-confined set, grouped per table, so a foreign table in the same domain never leaks its BaseUrl.
        var scope = CallerScopeResolver.Resolve(context.User, CatalogOwner);
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
                    CreatedAt: DateTimeOffset.UtcNow,
                    AllowedTransports: EffectiveTransportsOf(table));
            }
        }

        return sources.Values.ToList();
    }

    /// <summary>
    /// Issue #447: the effective<c>AllowedTransports</c> of the table's source (a source without a profile inherits
    /// <c>Defaults</c>). Administrator-only data; null when no resolver is wired or the profile cannot be read.
    /// </summary>
    private IReadOnlyList<string>? EffectiveTransportsOf(TableMetadata table)
    {
        if (_transportResolver is null)
        {
            return null;
        }

        try
        {
            var source = _transportResolver.CanonicalName(Autheris.Application.Sql.Services.DataSourceAllowlistGuard.LogicalDataSource(table));
            var profile = _transportResolver.TryResolve(source, out var explicitProfile) && explicitProfile is not null
                ? explicitProfile
                : _transportResolver.Resolve(source);
            return Autheris.Application.DataSources.TransportFlagNames.Of(profile.AllowedTransports);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// The channel caller. The REST subject (<c>context.SubjectId</c>) and the principal SID must agree (the stricter of
    /// both applies); a mismatch answers as no caller, so the service returns the empty set.
    /// </summary>
    private static CatalogCaller CallerOf(RequestContext context)
    {
        var sid = context.User.GetUserSid();
        var principal = sid != null && string.Equals(sid.Value.Value, context.SubjectId.Value, StringComparison.Ordinal) ? context.User : null;
        return new CatalogCaller(principal, context.TenantId, CatalogChannel.Rest);
    }

    /// <summary>
    /// The tables the caller may discover (one visibility service: consent and tier shaping) together with the unfiltered
    /// catalog (only used to tell which columns a caller cannot see). Any failure answers the empty set, never the unfiltered catalog.
    /// </summary>
    private async Task<(IReadOnlyList<TableMetadata> Permitted, IReadOnlyList<TableMetadata> All)> VisibleForAsync(RequestContext context, CancellationToken ct)
    {
        if (_visibility == null)
        {
            return ([], []);
        }

        var set = await _visibility.GetVisibleAsync(CallerOf(context), contract: null, ct).ConfigureAwait(false);
        CallerScopeDiagnostics.ReportIfClaimlessAdmin(_logger, context.User, set.Scope);
        return (set.Tables, set.AllTables);
    }

    private static CatalogDatasetSummary ToSummary(TableMetadata table, string? language = null) => ToSummary(table, table.Table.DataSourceType.ToString(), language);

    private static CatalogDatasetSummary ToSummary(TableMetadata table, string sourceType, string? language = null)
    {
        var resolved = !string.IsNullOrWhiteSpace(language) ? table.Table.Texts.Resolve(language) : null;
        return new(
            DatasetId: table.Identifier.ToString(),
            Domain: table.Identifier.Domain,
            Schema: table.Identifier.Schema,
            Table: table.Identifier.TableName,
            // A Summary row discloses only the allow-listed fields; the source type is not one of them.
            SourceType: table.CatalogView == CatalogView.Summary ? string.Empty : sourceType,
            Sensitivity: table.Table.Sensitivity.ToString(),
            Description: resolved?.Description ?? table.Table.Description,
            IsActive: table.Table.IsActive,
            DisplayName: resolved?.Title ?? table.Table.DisplayName,
            Language: language);
    }

    private static IReadOnlyList<string> QueryTerms(string? query) =>
        (query ?? string.Empty)
            .Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length > 0)
            .Select(t => t.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

    private static bool IsSingleIdentifierQuery(string? query, IReadOnlyList<string> terms) =>
        terms.Count > 1 &&
        !string.IsNullOrWhiteSpace(query) &&
        !query.Trim().Any(char.IsWhiteSpace);

    private static readonly char[] QuerySeparators = [' ', '\t', '\r', '\n', ',', ';', '.', '(', ')', '"', '\'', '/', '\\', '-', '_', ':'];

    /// <summary>
    /// Result hygiene for one index hit (W0-C1). The index is built from the whole catalog including column names, so a hit
    /// and everything derived from it is rebuilt from the caller's filtered metadata:
    /// <c>RelevantColumns</c> only from visible columns, <c>MatchedTerms</c> only terms found in the identifier, the description or a visible
    /// column, join paths only over visible tables and visible columns, description and sensitivity from the filtered row.
    /// For a table with at least one non-visible column a hit that has no lexical match in visible text (a hit caused by a hidden
    /// column, or a vector-only hit) is dropped; semantic-only recall is therefore reduced for column-restricted callers.
    /// </summary>
    internal static CatalogSearchHit? ShapeHit(
        CatalogSearchHit hit,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> permitted,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> all,
        IReadOnlyList<string> queryTerms,
        bool isSingleIdentifierQuery = false)
    {
        if (!permitted.TryGetValue(hit.TableIdentifier, out var visible))
        {
            return null;
        }

        var visibleColumnNames = visible.Columns.Select(c => c.ColumnName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var restricted = !all.TryGetValue(hit.TableIdentifier, out var full) ||
                         full.Columns.Any(c => !visibleColumnNames.Contains(c.ColumnName));

        IReadOnlyList<string> matchedTerms = hit.MatchedTerms;
        double combinedScore = hit.CombinedScore;
        double? bm25Score = hit.Bm25Score;
        double? vectorScore = hit.VectorScore;

        if (restricted)
        {
            var text = VisibleTokens(visible);
            matchedTerms = hit.MatchedTerms
                .Where(t => !string.IsNullOrWhiteSpace(t) && text.Contains(t))
                .ToList();
            if (matchedTerms.Count == 0)
            {
                return null;
            }

            // When all query terms came from a single underscore-joined identifier (e.g. a column name like tier_secret_col),
            // every query term must be present in the visible text so a shared prefix (like "tier" in table description)
            // cannot make a hidden column name match a restricted/Summary row.
            if (isSingleIdentifierQuery && queryTerms.Any(t => !text.Contains(t)))
            {
                return null;
            }

            // GHSA-95f9-ffc7-vw2c: restricted-table search scores
            // to prevent text-oracle inference attacks on hidden columns.
            bm25Score = null;
            vectorScore = null;
            var visibleRatio = queryTerms.Count > 0 ? (double)matchedTerms.Count / queryTerms.Count : 1.0;
            combinedScore = Math.Round(visibleRatio, 1);
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
            CombinedScore = combinedScore,
            Bm25Score = bm25Score,
            VectorScore = vectorScore,
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
        sb.Append(table.Identifier.Domain).Append(' ')
          .Append(table.Identifier.Schema).Append(' ')
          .Append(table.Identifier.TableName).Append(' ')
          .Append(table.Table.DisplayName).Append(' ')
          .Append(table.Table.Description).Append(' ');

        if (table.Table.Texts is not null)
        {
            foreach (var (_, entry) in table.Table.Texts.Entries)
            {
                sb.Append(entry.DisplayName).Append(' ')
                  .Append(entry.Description).Append(' ')
                  .Append(entry.LongDescription).Append(' ');
            }
        }

        foreach (var c in table.Columns)
        {
            sb.Append(c.ColumnName).Append(' ')
              .Append(c.DataType).Append(' ')
              .Append(c.DisplayName).Append(' ')
              .Append(c.Description).Append(' ');

            if (c.Texts is not null)
            {
                foreach (var (_, entry) in c.Texts.Entries)
                {
                    sb.Append(entry.DisplayName).Append(' ')
                      .Append(entry.Description).Append(' ');
                }
            }
        }

        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in sb.ToString().Split(TokenSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            tokens.Add(t);
            var folded = DiacriticNormalizer.FoldGermanUmlauts(t);
            if (!string.Equals(t, folded, StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(folded);
            }
            var stripped = DiacriticNormalizer.StripAccents(t);
            if (!string.Equals(t, stripped, StringComparison.OrdinalIgnoreCase))
            {
                tokens.Add(stripped);
            }
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
        // GHSA-v5c2: the namespace carries the owning tenant, so a ref can never address another tenant's secret.
        var secretNamespace = $"{DatasourceSecretRefPolicy.DomainPrefix(request.Domain)}{context.TenantId.Value.ToLowerInvariant()}:{request.Name.ToLowerInvariant()}:";
        DatasourceSecretRefPolicy.ValidateClientRef(request.Auth?.SecretRef, secretNamespace);

        var isConfigured = request.Auth != null &&
            (!string.IsNullOrEmpty(request.Auth.Value) ||
             !string.IsNullOrEmpty(request.Auth.ClientSecret) ||
             !string.IsNullOrEmpty(request.Auth.SecretRef));

        var datasourceId = $"ds-{Guid.NewGuid():N}";

        if (!request.DryRun)
        {
            // Write-ahead: the required record exists before the secret is stored and the spec is ingested.
            // SEC REVIEW G5 / R-55: Never leak raw secrets in AuditLog
            var auditEntry = new AuditLogEntry
            {
                TenantId = context.TenantId,
                ActorSid = context.SubjectId,
                EventType = AuditEventTypes.DatasourceRegistered,
                TargetTable = $"{request.Domain}.*",
                Decision = "ALLOW",
                TraceId = context.CorrelationId ?? Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    DatasourceId = datasourceId,
                    DatasourceName = request.Name,
                    Domain = request.Domain,
                    IsConfigured = isConfigured,
                    AuthType = request.Auth?.Type
                })
            };

            await _auditWriter.RecordRequiredAsync(auditEntry, ct).ConfigureAwait(false);
        }

        // R-55 / SEC REVIEW G5: Securely vault credentials in IKeyVaultSecretProvider and strip plaintext
        var effectiveAuth = request.Auth;
        if (!request.DryRun && request.Auth != null && _secretProvider != null)
        {
            var rawSecret = request.Auth.Value ?? request.Auth.ClientSecret;
            if (!string.IsNullOrEmpty(rawSecret))
            {
                var secretRef = request.Auth.SecretRef ?? $"{secretNamespace}secret";
                DatasourceSecretRefPolicy.ThrowIfExists(_secretProvider, secretRef);

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
