namespace Autheris.Application.Mcp.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class GoldenQueryService : IGoldenQueryService
{
    private readonly ConcurrentDictionary<string, GoldenQuery> _queries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILogger<GoldenQueryService> _logger;
    private readonly int _maxResults;
    private readonly bool _enabled;
    private readonly IGoldenQueryRepository? _repository;
    private readonly ICatalogVersionProvider? _versionProvider;
    private readonly ConcurrentDictionary<string, StoreSnapshot> _storeCache = new(StringComparer.Ordinal);

    /// <summary>Tenant-keyed store snapshots kept at once; the cache is cleared when exceeded.</summary>
    private const int MaxCachedTenants = 1024;

    /// <summary>Caller independent store entries of one tenant at one catalog version; caller filters run after this read.</summary>
    private sealed record StoreSnapshot(CatalogVersion Version, IReadOnlyList<GoldenQuery> Entries, IReadOnlySet<string> ConfigCopyIds);

    /// <param name="demoData">Without a switch (direct construction) the built-in demo queries are registered, as before.</param>
    /// <param name="repository">Without a store only configured entries are served.</param>
    /// <param name="versionProvider">Keys the store cache; without it the store is read on every call.</param>
    public GoldenQueryService(
        IOptions<GatewayOptions>? options,
        ILogger<GoldenQueryService> logger,
        IDemoDataSwitch? demoData = null,
        IGoldenQueryRepository? repository = null,
        ICatalogVersionProvider? versionProvider = null)
    {
        _logger = logger;
        _repository = repository;
        _versionProvider = versionProvider;
        if (repository is not null && versionProvider is null)
        {
            _logger.LogWarning("GOLDEN_QUERY_STORE_UNCACHED: no catalog version provider is available; stored golden queries are read on every call.");
        }
        var goldenOpts = options?.Value.GoldenQueries ?? new GoldenQueryOptions();
        _enabled = true;
        _maxResults = goldenOpts.MaxResultsPerRequest > 0 ? goldenOpts.MaxResultsPerRequest : 20;

        InitializeQueries(goldenOpts.InitialQueries, demoData?.Enabled ?? true);
    }

    private const int MaxQueryStoreCapacity = 5000;
    private const int MaxQueryTextLength = 65536;

    public void RegisterGoldenQuery(GoldenQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.Domain);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.TableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(query.QueryText);

        if (query.QueryText.Length > MaxQueryTextLength)
        {
            throw new ArgumentException($"Query text exceeds maximum allowed size of {MaxQueryTextLength} characters.", nameof(query));
        }

        if (_queries.Count >= MaxQueryStoreCapacity && !_queries.ContainsKey(query.Id))
        {
            throw new InvalidOperationException($"Golden query registry capacity limit ({MaxQueryStoreCapacity}) reached.");
        }

        _queries[query.Id] = query;
        _logger.LogDebug("Registered golden query '{Id}' for domain '{Domain}', table '{TableName}'",
            query.Id, query.Domain, query.TableName);
    }

    public ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(
        string? domain,
        string? tableName,
        Func<GoldenQuery, bool>? visible,
        CancellationToken ct)
    {
        if (!_enabled)
        {
            return ValueTask.FromResult<IReadOnlyList<GoldenQuery>>([]);
        }

        IEnumerable<GoldenQuery> query = _queries.Values;

        if (!string.IsNullOrWhiteSpace(domain))
        {
            query = query.Where(q => string.Equals(q.Domain, domain, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tableName))
        {
            query = query.Where(q => string.Equals(q.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        }

        if (visible is not null)
        {
            // MCP-15: visibility first, cap second; a throwing predicate excludes the entry (fail closed).
            query = query.Where(q => IsVisible(visible, q));
        }

        var results = query.Take(_maxResults).ToList();
        return ValueTask.FromResult<IReadOnlyList<GoldenQuery>>(results);
    }

    public async ValueTask<GoldenQueryCandidateSet> GetCandidateQueriesAsync(
        TenantId? tenant,
        string? domain,
        string? tableName,
        CancellationToken ct = default)
    {
        if (!_enabled)
        {
            return GoldenQueryCandidateSet.Known([]);
        }

        var candidates = await LoadMergedCandidatesAsync(tenant, domain, tableName, ct).ConfigureAwait(false);
        return GoldenQueryCandidateSet.Known(candidates);
    }

    public async ValueTask<IReadOnlyList<GoldenQuery>> GetGoldenQueriesAsync(
        GoldenQueryReadContext context,
        string? domain,
        string? tableName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!_enabled)
        {
            return [];
        }

        var candidates = await LoadMergedCandidatesAsync(context.Tenant, domain, tableName, ct).ConfigureAwait(false);

        // Caller visibility first, cap second: hidden entries cannot crowd out visible ones.
        return candidates
            .Where(q => IsVisibleToCaller(context, q))
            .Take(_maxResults)
            .ToList();
    }

    private async ValueTask<IReadOnlyList<GoldenQuery>> LoadMergedCandidatesAsync(
        TenantId? tenant,
        string? domain,
        string? tableName,
        CancellationToken ct)
    {
        var snapshot = await LoadStoreSnapshotAsync(tenant, ct).ConfigureAwait(false);
        var merged = new Dictionary<string, GoldenQuery>(StringComparer.OrdinalIgnoreCase);
        foreach (var stored in snapshot.Entries)
        {
            merged[stored.Id] = stored;
        }

        foreach (var configured in _queries.Values)
        {
            // Config wins on an equal id, except against the persisted copy of a config entry (the store wins on an equal source key).
            if (!snapshot.ConfigCopyIds.Contains(configured.Id))
            {
                merged[configured.Id] = configured;
            }
        }

        IEnumerable<GoldenQuery> query = merged.Values;
        if (!string.IsNullOrWhiteSpace(domain))
        {
            query = query.Where(q => string.Equals(q.Domain, domain, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tableName))
        {
            query = query.Where(q => string.Equals(q.TableName, tableName, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderBy(q => q.Domain, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.TableName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(q => q.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsVisibleToCaller(GoldenQueryReadContext context, GoldenQuery query)
    {
        try
        {
            // Default deny: without a visibility delegate nothing is visible.
            if (context.IsDatasetVisible is not { } datasetVisible || !datasetVisible(query.Domain, query.TableName))
            {
                return false;
            }

            IReadOnlyList<(string Domain, string Table)> references = [];
            if (context.ReferencedDatasets is { } referenced)
            {
                // A null result means the entry could not be proven safe (unresolvable field, unparseable text).
                if (referenced(query)?.ToList() is not { } reached || reached.Any(r => !datasetVisible(r.Domain, r.Table)))
                {
                    return false;
                }

                references = reached;
            }

            if (context.HiddenIdentifiers is { } hiddenOf)
            {
                var hidden = hiddenOf(query.Domain, query.TableName);
                if (hidden is { Count: > 0 } && MentionsAnyIdentifier(query.QueryText, hidden))
                {
                    return false;
                }

                foreach (var (refDomain, refTable) in references)
                {
                    var refHidden = hiddenOf(refDomain, refTable);
                    if (refHidden is { Count: > 0 } && MentionsAnyIdentifier(query.QueryText, refHidden))
                    {
                        return false;
                    }
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    private async ValueTask<StoreSnapshot> LoadStoreSnapshotAsync(TenantId? caller, CancellationToken ct)
    {
        var empty = new StoreSnapshot(default, [], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        // No tenant, no entries: a missing, default or reserved (platform) tenant never reads the store.
        if (_repository is null || caller is not { } tenant || string.IsNullOrWhiteSpace(tenant.Value) || TenantId.IsReserved(tenant))
        {
            return empty;
        }

        try
        {
            CatalogVersion? version = _versionProvider is null
                ? null
                : await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false);
            if (version is { } current && _storeCache.TryGetValue(tenant.Value, out var hit) && hit.Version == current)
            {
                return hit;
            }

            var rows = await _repository.GetForTenantAsync(tenant, ct).ConfigureAwait(false);
            var entries = rows
                .Select(r => new GoldenQuery(r.SourceKey, r.Domain, r.TableName, r.Title, r.Description ?? string.Empty, r.QueryText, r.VariablesJson, [r.Source.ToString().ToLowerInvariant()], r.UpdatedAt) { FromStore = true })
                .ToList();
            var configCopies = rows
                .Where(r => r.Source == GoldenQuerySource.Config)
                .Select(r => r.SourceKey)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var snapshot = new StoreSnapshot(version ?? default, entries, configCopies);
            if (version is not null)
            {
                if (_storeCache.Count >= MaxCachedTenants)
                {
                    _storeCache.Clear();
                }

                _storeCache[tenant.Value] = snapshot;
            }

            return snapshot;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Fail closed: a store failure serves configured entries only.
            _logger.LogWarning("GOLDEN_QUERY_STORE_READ_FAILED: stored golden queries unavailable ({Error}).", ex.GetType().Name);
            return empty;
        }
    }

    public static IReadOnlyList<GoldenQuery> ExcludeHiddenColumnQueries(
        IReadOnlyList<GoldenQuery> queries,
        IReadOnlySet<string>? hiddenIdentifiers)
    {
        if (queries.Count == 0 || hiddenIdentifiers is null or { Count: 0 })
        {
            return queries;
        }

        return queries.Where(q => !MentionsAnyIdentifier(q.QueryText, hiddenIdentifiers)).ToList();
    }

    public static IReadOnlySet<string> BuildHiddenIdentifierSet(
        IReadOnlySet<string>? hiddenColumns,
        IReadOnlyDictionary<string, GraphQlColumnMapping>? graphQlColumns = null)
    {
        if (hiddenColumns is null or { Count: 0 })
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var set = new HashSet<string>(hiddenColumns, StringComparer.OrdinalIgnoreCase);
        if (graphQlColumns is not null)
        {
            foreach (var col in hiddenColumns)
            {
                if (graphQlColumns.TryGetValue(col, out var gql) && !string.IsNullOrWhiteSpace(gql.Field))
                {
                    set.Add(gql.Field);
                }
            }
        }

        return set;
    }

    /// <summary>
    /// Conservative word match: every run of letters, digits and underscores in <paramref name="text"/> is compared with the identifiers
    /// (case-insensitive), so a hidden name inside a string literal, a comment or an unrelated field of the same name also matches. Over-hiding
    /// an example is intended; it is never the other way round.
    /// </summary>
    public static bool MentionsAnyIdentifier(string? text, IReadOnlySet<string> identifiers)
    {
        if (string.IsNullOrWhiteSpace(text) || identifiers.Count == 0)
        {
            return false;
        }

        int i = 0;
        while (i < text.Length)
        {
            if (IsIdentifierChar(text[i]))
            {
                int start = i;
                while (i < text.Length && IsIdentifierChar(text[i])) i++;
                if (identifiers.Contains(text[start..i]))
                {
                    return true;
                }
            }
            else
            {
                i++;
            }
        }

        return false;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsVisible(Func<GoldenQuery, bool> visible, GoldenQuery query)
    {
        try
        {
            return visible(query);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    public ValueTask<GoldenQuery?> GetByIdAsync(string id, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(id))
        {
            return ValueTask.FromResult<GoldenQuery?>(null);
        }

        _queries.TryGetValue(id, out var query);
        return ValueTask.FromResult(query);
    }

    private void InitializeQueries(IReadOnlyList<GoldenQueryDefinition>? initialQueries, bool includeDemoQueries)
    {
        if (initialQueries != null && initialQueries.Count > 0)
        {
            foreach (var def in initialQueries)
            {
                RegisterGoldenQuery(new GoldenQuery(
                    Id: def.Id,
                    Domain: def.Domain,
                    TableName: def.TableName,
                    Title: def.Title,
                    Description: def.Description,
                    QueryText: def.QueryText,
                    VariablesJson: def.VariablesJson,
                    Tags: def.Tags,
                    CreatedAt: DateTimeOffset.UtcNow
                ));
            }
            return;
        }

        // Built-in Golden Queries (finance demo catalog) only with demo data enabled
        if (!includeDemoQueries)
        {
            return;
        }

        RegisterGoldenQuery(new GoldenQuery(
            Id: "golden_customers_active",
            Domain: "finance",
            TableName: "customers",
            Title: "Get Active Customers with Account Details",
            Description: "Returns customer identifier, masked IBAN, and account status with standard pagination.",
            QueryText: """
            query GetActiveCustomers($limit: Int) {
              customers(filter: { status: "ACTIVE" }, limit: $limit) {
                id
                name
                email
                iban
                accountStatus
              }
            }
            """,
            VariablesJson: """{ "limit": 25 }""",
            Tags: ["finance", "customers", "active", "recommended"],
            CreatedAt: DateTimeOffset.UtcNow
        ));

        RegisterGoldenQuery(new GoldenQuery(
            Id: "golden_invoices_unpaid",
            Domain: "finance",
            TableName: "invoices",
            Title: "Retrieve Overdue and Unpaid Invoices",
            Description: "Extracts unpaid customer invoices filtered by dueDate with currency formatting.",
            QueryText: """
            query GetUnpaidInvoices($dueDateBefore: String) {
              invoices(filter: { status: "UNPAID", dueDateBefore: $dueDateBefore }) {
                invoiceNumber
                customerId
                amount
                currency
                dueDate
              }
            }
            """,
            VariablesJson: """{ "dueDateBefore": "2026-12-31" }""",
            Tags: ["finance", "invoices", "overdue"],
            CreatedAt: DateTimeOffset.UtcNow
        ));
    }
}
