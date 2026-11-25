namespace Autheris.Application.Catalog.Discovery;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Tenancy;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Unified authorized data discovery core shared by REST, GraphQL, MCP, and Search channels.
/// Delegates all allow, row-scope, and column-level decisions solely to <see cref="IAccessDecisionPoint.DecideManyAsync"/>.
/// </summary>
public sealed partial class DiscoveryService : IDiscoveryService
{
    public const int MaxPageSize = 200;
    private const int MaxSearchWindows = 5;
    private const int MaxSearchWindowSize = 1000;
    public const int MaxAuditObjectIds = 50;
    public const int ExpiredLookbackDays = 90;

    private static readonly IReadOnlySet<TableIdentifier> EmptyTableSet = new HashSet<TableIdentifier>();

    private readonly ITableMetadataRepository? _metadataRepository;
    private readonly IAccessDecisionPoint? _decisionPoint;
    private readonly IConsentRepository? _consentRepository;
    private readonly IConsentApprovalRepository? _approvalRepository;
    private readonly IContainerOwnership? _ownershipRepository;
    private readonly IDiscoveryArtifactStore? _artifactStore;
    private readonly IAuditWriter? _auditWriter;
    private readonly GatewayOptions _gatewayOptions;
    private readonly CatalogDiscoveryOptions _discoveryOptions;
    private readonly IRateLimiterService? _rateLimiter;
    private readonly ICatalogSearchEngine? _searchEngine;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<DiscoveryService> _logger;
    private readonly IClassificationRepository? _classificationRepository;
    private readonly Autheris.Application.Procedures.Interfaces.IProcedureCatalog? _procedureCatalog;
    private readonly Autheris.Application.Docs.Services.IDocLinkDiscoveryReader? _docLinks;
    private readonly ICatalogSnapshotProvider? _snapshotProvider;
    private readonly IProtectionSnapshotProvider? _protectionProvider;
    private readonly Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? _accessPolicySettings;
    private readonly CatalogAccessViewCache? _accessViewCache;
    private readonly ICatalogVisibilityEvaluator _visibilityEvaluator;

    // Issue #1044: set for the rest of this (scoped) service once the page guard found a stale cached view.
    private bool _viewCacheBypassed;

    private readonly Lock _memoLock = new();
    private readonly Dictionary<ScopeMemoKey, ScopedCatalog> _scopeMemo = [];
    private readonly Dictionary<ObjectMemoKey, DiscoveryObject> _objectMemo = [];
    private readonly Dictionary<ScopeMemoKey, CallerAccessMemo> _accessMemo = [];
    private readonly Dictionary<(TenantId, TableIdentifier), EffectiveProtection> _protectionMemo = [];

    private EffectiveProtection? TryGetMemoizedProtection(TenantId tenant, TableIdentifier table)
    {
        lock (_memoLock) return _protectionMemo.GetValueOrDefault((tenant, table));
    }

    private readonly record struct ScopeMemoKey(string Subject, string ClaimsHash, string Tenant, CatalogChannel Channel);
    private readonly record struct ObjectMemoKey(ScopeMemoKey ScopeKey, DiscoveryObjectKind Kind, TableIdentifier TableId, string LanguageKey);
    private sealed record ScopedCatalog(
        IReadOnlyList<TableMetadata> VisibleTables,
        IReadOnlySet<TableIdentifier> OwnedTables);

    public DiscoveryService(
        ITableMetadataRepository? metadataRepository,
        IAccessDecisionPoint? decisionPoint,
        IConsentRepository? consentRepository,
        IConsentApprovalRepository? approvalRepository,
        IContainerOwnership? ownershipRepository,
        IDiscoveryArtifactStore? artifactStore,
        IAuditWriter? auditWriter,
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<CatalogDiscoveryOptions>? discoveryOptions = null,
        IRateLimiterService? rateLimiter = null,
        ICatalogSearchEngine? searchEngine = null,
        TimeProvider? timeProvider = null,
        ILogger<DiscoveryService>? logger = null,
        IClassificationRepository? classificationRepository = null,
        Autheris.Application.Procedures.Interfaces.IProcedureCatalog? procedureCatalog = null,
        Autheris.Application.Docs.Services.IDocLinkDiscoveryReader? docLinks = null,
        ICatalogSnapshotProvider? snapshotProvider = null,
        IProtectionSnapshotProvider? protectionProvider = null,
        Autheris.Application.Governance.Policy.IAccessPolicySettingsProvider? accessPolicySettings = null,
        Autheris.Application.Catalog.Visibility.IContainerVisibilitySnapshotProvider? containerVisibility = null,
        CatalogAccessViewCache? accessViewCache = null,
        Autheris.Application.Policy.Protection.IUnmaskGrantResolver? unmaskResolver = null,
        ICatalogVisibilityEvaluator? visibilityEvaluator = null)
    {
        _containerVisibility = containerVisibility;
        _accessViewCache = accessViewCache;
        _unmaskResolver = unmaskResolver;
        _docLinks = docLinks;
        _snapshotProvider = snapshotProvider;
        _protectionProvider = protectionProvider;
        _accessPolicySettings = accessPolicySettings;
        ArgumentNullException.ThrowIfNull(gatewayOptions);
        _metadataRepository = metadataRepository;
        _decisionPoint = decisionPoint;
        _consentRepository = consentRepository;
        _approvalRepository = approvalRepository;
        _ownershipRepository = ownershipRepository;
        _artifactStore = artifactStore;
        _auditWriter = auditWriter;
        _gatewayOptions = gatewayOptions.Value ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _discoveryOptions = discoveryOptions?.Value ?? new CatalogDiscoveryOptions();
        _rateLimiter = rateLimiter;
        _searchEngine = searchEngine;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<DiscoveryService>.Instance;
        _classificationRepository = classificationRepository;
        _procedureCatalog = procedureCatalog;
        _visibilityEvaluator = visibilityEvaluator ?? new CatalogVisibilityEvaluator(gatewayOptions, timeProvider: _timeProvider);
    }

    public async ValueTask<DiscoveryPage> ListAsync(
        DiscoveryQuery query,
        CatalogCaller caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();

        var sid = await DiscoveryCallerGate.EnsureAllowedAsync(
            caller, _discoveryOptions, _gatewayOptions, _rateLimiter, ct).ConfigureAwait(false);

        if (_metadataRepository is null || _decisionPoint is null || _consentRepository is null)
        {
            RecordFailure("Required discovery dependency missing.");
            return new DiscoveryPage([], 0, false);
        }

        return await ListCoreAsync(query, caller, sid, ct).ConfigureAwait(false);
    }

    private async ValueTask<DiscoveryPage> ListCoreAsync(DiscoveryQuery query, CatalogCaller caller, Sid sid, CancellationToken ct)
    {
        try
        {
            var scopeKey = BuildScopeMemoKey(sid, caller);
            var canFilterAtStore = !query.IncludeProcedures || _procedureCatalog is null;
            var scoped = canFilterAtStore
                ? await LoadScopedCatalogAsync(caller, sid, scopeKey, query.Domain, query.Schema, ct).ConfigureAwait(false)
                : await LoadScopedCatalogAsync(caller, sid, scopeKey, ct).ConfigureAwait(false);

            IEnumerable<TableMetadata> filtered = scoped.VisibleTables;
            if (!string.IsNullOrWhiteSpace(query.Domain))
            {
                var d = query.Domain.Trim();
                filtered = filtered.Where(t => string.Equals(t.Identifier.Domain, d, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Schema))
            {
                var s = query.Schema.Trim();
                filtered = filtered.Where(t => string.Equals(t.Identifier.Schema, s, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(query.Tag))
            {
                var tag = query.Tag.Trim();
                filtered = filtered.Where(t => t.CatalogTags.Any(tg => string.Equals(tg, tag, StringComparison.OrdinalIgnoreCase)));
            }

            var matchedTables = filtered.ToList();

            // Issue #798: the access filter narrows the matched set before the total and the page are taken. It runs before the
            // caller-visible text check (#813) so only the tables in the wanted state are projected; both only narrow the set.
            if (query.Access is { } wantedAccess)
            {
                var view = await LoadAccessViewAsync(caller, sid, scopeKey, matchedTables, wantedAccess, ct).ConfigureAwait(false);
                matchedTables = matchedTables.FindAll(t => view.StateOf(t.Identifier) == wantedAccess);
            }

            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                var searchText = query.Search.Trim();
                matchedTables = await FilterAndRankBySearchAsync(
                    caller, sid, scopeKey, matchedTables, searchText, query.Domain, mode: null, query.Language, orderByRelevance: false, ct).ConfigureAwait(false);
            }

            var eligibleProcedures = query.IncludeProcedures
                ? (await LoadEligibleProceduresAsync(caller, scoped.VisibleTables, ct).ConfigureAwait(false))
                    .Where(p => DiscoveryProcedureResolver.MatchesQuery(p.Procedure, query.Domain, query.Schema, query.Tag, query.Search))
                    .ToList()
                : [];
            if (query.Access is { } wantedProcedureAccess)
            {
                eligibleProcedures = await FilterProceduresByAccessAsync(caller, sid, scopeKey, eligibleProcedures, wantedProcedureAccess, ct).ConfigureAwait(false);
            }

            var totalCount = matchedTables.Count + eligibleProcedures.Count;
            var first = Math.Clamp(query.First <= 0 ? 50 : query.First, 1, MaxPageSize);
            var after = Math.Max(0, query.After);

            var pageTables = matchedTables.Skip(after).Take(first).ToList();
            var remainingSlots = Math.Max(0, first - pageTables.Count);
            var procSkip = Math.Max(0, after - matchedTables.Count);
            var pageProcedures = remainingSlots > 0
                ? eligibleProcedures.Skip(procSkip).Take(remainingSlots).ToList()
                : [];
            var hasNext = after + pageTables.Count + pageProcedures.Count < totalCount;

            var tableObjects = await ResolveObjectsBatchAsync(
                caller, sid, scopeKey, pageTables, query.Language, ct).ConfigureAwait(false);
            if (query.Access is { } listedAccess && ContradictsCachedView(scopeKey, listedAccess, tableObjects))
            {
                return await ListCoreAsync(query, caller, sid, ct).ConfigureAwait(false);
            }

            var procObjects = await ResolveProceduresBatchAsync(
                caller, sid, scopeKey, pageProcedures, ct).ConfigureAwait(false);

            var objects = new List<DiscoveryObject>(tableObjects.Count + procObjects.Count);
            objects.AddRange(tableObjects);
            objects.AddRange(procObjects);

            await WriteDiscoveryAuditAsync(
                caller.Tenant, sid, query.Surface, objects, queryText: query.Search, ct).ConfigureAwait(false);

            return new DiscoveryPage(objects, totalCount, hasNext);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DiscoveryStatusException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex, "Discovery ListAsync failed closed.");
            return new DiscoveryPage([], 0, false);
        }
    }

    public async ValueTask<DiscoveryObject?> GetAsync(
        TableIdentifier id,
        CatalogCaller caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();

        var sid = await DiscoveryCallerGate.EnsureAllowedAsync(
            caller, _discoveryOptions, _gatewayOptions, _rateLimiter, ct).ConfigureAwait(false);

        if (_metadataRepository is null || _decisionPoint is null || _consentRepository is null)
        {
            RecordFailure("Required discovery dependency missing.");
            return null;
        }

        try
        {
            var scopeKey = BuildScopeMemoKey(sid, caller);

            if (string.Equals(id.Schema, "procedure", StringComparison.OrdinalIgnoreCase))
            {
                var scoped = await LoadScopedCatalogAsync(caller, sid, scopeKey, ct).ConfigureAwait(false);
                var matchedProc = (await LoadEligibleProceduresAsync(caller, scoped.VisibleTables, ct).ConfigureAwait(false))
                    .FirstOrDefault(p =>
                        string.Equals(p.Procedure.Definition.CatalogDomain, id.Domain, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(p.Procedure.Definition.Name, id.TableName, StringComparison.OrdinalIgnoreCase));

                if (matchedProc is not null)
                {
                    var procObjects = await ResolveProceduresBatchAsync(
                        caller, sid, scopeKey, [matchedProc], ct).ConfigureAwait(false);
                    await WriteDiscoveryAuditAsync(
                        caller.Tenant, sid, DiscoverySurfaceOf(caller.Channel), procObjects, queryText: null, ct).ConfigureAwait(false);
                    return procObjects.Count > 0 ? procObjects[0] : null;
                }

                await WriteDiscoveryAuditAsync(
                    caller.Tenant, sid, DiscoverySurfaceOf(caller.Channel), [], queryText: null, ct).ConfigureAwait(false);
                return null;
            }

            var match = await FindSingleScopedTableAsync(id, caller, sid, scopeKey, ct).ConfigureAwait(false);
            if (match is null)
            {
                await WriteDiscoveryAuditAsync(
                    caller.Tenant, sid, DiscoverySurfaceOf(caller.Channel), [], queryText: null, ct).ConfigureAwait(false);
                return null;
            }

            var objects = await ResolveObjectsBatchAsync(
                caller, sid, scopeKey, [match], language: null, ct).ConfigureAwait(false);

            await WriteDiscoveryAuditAsync(
                caller.Tenant, sid, DiscoverySurfaceOf(caller.Channel), objects, queryText: null, ct).ConfigureAwait(false);

            return objects.Count > 0 ? objects[0] : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DiscoveryStatusException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex, "Discovery GetAsync failed closed.");
            return null;
        }
    }

    public async ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(
        string text,
        DiscoverySearchOptions options,
        CatalogCaller caller,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(caller);
        ct.ThrowIfCancellationRequested();

        var sid = await DiscoveryCallerGate.EnsureAllowedAsync(
            caller, _discoveryOptions, _gatewayOptions, _rateLimiter, ct).ConfigureAwait(false);

        if (_metadataRepository is null || _decisionPoint is null || _consentRepository is null)
        {
            RecordFailure("Required discovery dependency missing.");
            return [];
        }

        return await SearchCoreAsync(text, options, caller, sid, ct).ConfigureAwait(false);
    }

    private async ValueTask<IReadOnlyList<DiscoveryObject>> SearchCoreAsync(string? text, DiscoverySearchOptions options, CatalogCaller caller, Sid sid, CancellationToken ct)
    {
        try
        {
            var scopeKey = BuildScopeMemoKey(sid, caller);
            var canFilterAtStore = _procedureCatalog is null;
            var scoped = canFilterAtStore
                ? await LoadScopedCatalogAsync(caller, sid, scopeKey, options.Domain, schemaFilter: null, ct).ConfigureAwait(false)
                : await LoadScopedCatalogAsync(caller, sid, scopeKey, ct).ConfigureAwait(false);

            var limit = Math.Clamp(options.Limit <= 0 ? 25 : options.Limit, 1, MaxPageSize);
            var trimmed = text?.Trim() ?? string.Empty;

            // Issue #804: the access filter narrows the searchable set before ranking and the limit.
            // The domain filter runs first, so only the tables of that domain are decided.
            var searchable = scoped.VisibleTables;
            if (options.Access is { } wantedAccess)
            {
                var accessDomain = options.Domain?.Trim();
                var inDomain = string.IsNullOrEmpty(accessDomain)
                    ? scoped.VisibleTables
                    : scoped.VisibleTables.Where(t => string.Equals(t.Identifier.Domain, accessDomain, StringComparison.OrdinalIgnoreCase)).ToList();
                var view = await LoadAccessViewAsync(caller, sid, scopeKey, inDomain, wantedAccess, ct).ConfigureAwait(false);
                searchable = inDomain.Where(t => view.StateOf(t.Identifier) == wantedAccess).ToList();
            }

            var visibleById = searchable.ToDictionary(static t => t.Identifier);

            List<TableMetadata> matchedTables;
            if (!string.IsNullOrWhiteSpace(trimmed) && _searchEngine?.IsIndexReady == true && _searchEngine.IndexedTableCount > 0)
            {
                var mode = ParseSearchMode(options.Mode);
                // Candidates are verified against the caller-visible text before the limit applies, so the index window grows
                // (bounded) until enough verified hits exist or the index has no more candidates.
                var window = Math.Min(MaxPageSize, Math.Max(limit * 4, 50));
                var restrictedScores = new Dictionary<TableIdentifier, int>();
                // A caller-visible table registered after the last index build or delta is not in the index yet. As in the listing search
                // (FilterAndRankBySearchAsync) it is matched on its non-hidden text and follows the index hits, so search never drops a table
                // that list_datasets returns while the index catches up. The caller-visible verification below applies to it as well.
                var searchDomain = options.Domain?.Trim();
                var unindexed = searchable
                    .Where(t => !_searchEngine.ContainsTable(t.Identifier) &&
                                (string.IsNullOrEmpty(searchDomain) || string.Equals(t.Identifier.Domain, searchDomain, StringComparison.OrdinalIgnoreCase)) &&
                                DiscoveryVisibleTextMatcher.MatchesNonHiddenTextTerms(t, trimmed))
                    .ToList();
                matchedTables = [];
                for (var attempt = 0; attempt < MaxSearchWindows; attempt++)
                {
                    var hits = await _searchEngine.SearchAsync(
                        new CatalogSearchQuery(
                            QueryText: trimmed,
                            DomainFilter: options.Domain,
                            Limit: window,
                            Mode: mode,
                            Language: options.Language),
                        id => visibleById.ContainsKey(id),
                        isColumnRestricted: null,
                        ct).ConfigureAwait(false);

                    var candidates = new List<TableMetadata>(hits.Count + unindexed.Count);
                    var seen = new HashSet<TableIdentifier>();
                    foreach (var hit in hits)
                    {
                        if (visibleById.TryGetValue(hit.TableIdentifier, out var meta) && seen.Add(meta.Identifier))
                        {
                            candidates.Add(meta);
                        }
                    }

                    foreach (var table in unindexed)
                    {
                        if (seen.Add(table.Identifier))
                        {
                            candidates.Add(table);
                        }
                    }

                    restrictedScores.Clear();
                    matchedTables = await KeepCallerVisibleMatchesAsync(
                        caller, sid, scopeKey, candidates, options.Language,
                        (table, projected) => DiscoveryVisibleTextMatcher.MatchesAnyTerm(table, projected, trimmed), ct,
                        restrictedScores, (table, projected) => DiscoveryVisibleTextMatcher.VisibleTermMatchCount(table, projected, trimmed)).ConfigureAwait(false);

                    if (matchedTables.Count >= limit || hits.Count < window)
                    {
                        break;
                    }

                    window = Math.Min(MaxSearchWindowSize, window * 2);
                }

                // The index score of a column-restricted table was computed from its whole text, so its position would reveal hidden text.
                // Such hits follow the unrestricted ones (engine order) and are ordered by caller-visible term matches, then by identifier.
                if (restrictedScores.Count > 0)
                {
                    var unrestricted = matchedTables.Where(t => !restrictedScores.ContainsKey(t.Identifier));
                    var restrictedOrdered = matchedTables
                        .Where(t => restrictedScores.ContainsKey(t.Identifier))
                        .OrderByDescending(t => restrictedScores[t.Identifier])
                        .ThenBy(t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase);
                    matchedTables = unrestricted.Concat(restrictedOrdered).ToList();
                }

                if (matchedTables.Count > limit)
                {
                    matchedTables.RemoveRange(limit, matchedTables.Count - limit);
                }
            }
            else
            {
                IEnumerable<TableMetadata> q = searchable;
                if (!string.IsNullOrWhiteSpace(options.Domain))
                {
                    var d = options.Domain.Trim();
                    q = q.Where(t => string.Equals(t.Identifier.Domain, d, StringComparison.OrdinalIgnoreCase));
                }

                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    q = q.Where(t => MatchesNonHiddenText(t, trimmed));
                }

                // Without search text there is nothing to verify: the plain listing needs no per-caller projection here.
                matchedTables = string.IsNullOrWhiteSpace(trimmed)
                    ? q.Take(limit).ToList()
                    : await KeepCallerVisibleMatchesAsync(
                        caller, sid, scopeKey, q.ToList(), options.Language,
                        (table, projected) => DiscoveryVisibleTextMatcher.ContainsQuery(table, projected, trimmed), ct).ConfigureAwait(false);
                if (matchedTables.Count > limit)
                {
                    matchedTables.RemoveRange(limit, matchedTables.Count - limit);
                }
            }

            var remainingForProcs = Math.Max(0, limit - matchedTables.Count);
            List<EligibleDiscoveryProcedure> matchedProcedures = [];
            if (remainingForProcs > 0)
            {
                matchedProcedures = (await LoadEligibleProceduresAsync(caller, scoped.VisibleTables, ct).ConfigureAwait(false))
                    .Where(p => DiscoveryProcedureResolver.MatchesQuery(p.Procedure, options.Domain, schema: null, tag: null, trimmed))
                    .ToList();
                if (options.Access is { } wantedProcedureAccess)
                {
                    matchedProcedures = await FilterProceduresByAccessAsync(caller, sid, scopeKey, matchedProcedures, wantedProcedureAccess, ct).ConfigureAwait(false);
                }

                if (matchedProcedures.Count > remainingForProcs)
                {
                    matchedProcedures.RemoveRange(remainingForProcs, matchedProcedures.Count - remainingForProcs);
                }
            }

            var tableObjects = await ResolveObjectsBatchAsync(
                caller, sid, scopeKey, matchedTables, options.Language, ct).ConfigureAwait(false);
            if (options.Access is { } listedAccess && ContradictsCachedView(scopeKey, listedAccess, tableObjects))
            {
                return await SearchCoreAsync(text, options, caller, sid, ct).ConfigureAwait(false);
            }

            var procObjects = await ResolveProceduresBatchAsync(
                caller, sid, scopeKey, matchedProcedures, ct).ConfigureAwait(false);

            var objects = new List<DiscoveryObject>(tableObjects.Count + procObjects.Count);
            objects.AddRange(tableObjects);
            objects.AddRange(procObjects);

            await WriteDiscoveryAuditAsync(
                caller.Tenant, sid, options.Surface == DiscoverySurface.Rest ? DiscoverySurface.Search : options.Surface, objects, trimmed, ct).ConfigureAwait(false);

            return objects;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DiscoveryStatusException)
        {
            throw;
        }
        catch (GatewayInvalidQueryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure(ex, "Discovery SearchAsync failed closed.");
            return [];
        }
    }

    /// <summary>
    /// Keeps the candidates a search may return for this caller: a table with nothing hidden from the caller passes as is; a
    /// column-restricted table (a denied column, a withheld column text, or no access at all) passes only when
    /// <paramref name="matchesVisibleText"/> holds for the caller-projected object, the same projection describe_dataset returns.
    /// The projection resolves all column decisions in one batch for the candidates; any mismatch fails closed.
    /// </summary>
    private async Task<List<TableMetadata>> KeepCallerVisibleMatchesAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        List<TableMetadata> candidates,
        string? language,
        Func<TableMetadata, DiscoveryObject, bool> matchesVisibleText,
        CancellationToken ct,
        Dictionary<TableIdentifier, int>? restrictedScores = null,
        Func<TableMetadata, DiscoveryObject, int>? visibleScore = null)
    {
        if (candidates.Count == 0)
        {
            return candidates;
        }

        var projected = await ResolveObjectsBatchAsync(caller, sid, scopeKey, candidates, language, ct).ConfigureAwait(false);
        if (projected.Count != candidates.Count)
        {
            throw new InvalidOperationException("Discovery projection returned a mismatched object count.");
        }

        var kept = new List<TableMetadata>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            var restricted = DiscoveryVisibleTextMatcher.IsRestricted(candidates[i], projected[i]);
            if (!restricted || matchesVisibleText(candidates[i], projected[i]))
            {
                kept.Add(candidates[i]);
                if (restricted)
                {
                    restrictedScores?[candidates[i].Identifier] = visibleScore?.Invoke(candidates[i], projected[i]) ?? 0;
                }
            }
        }

        return kept;
    }

    private async ValueTask<List<EligibleDiscoveryProcedure>> LoadEligibleProceduresAsync(
        CatalogCaller caller,
        IReadOnlyList<TableMetadata> visibleTables,
        CancellationToken ct)
    {
        if (_procedureCatalog is null || visibleTables.Count == 0 || caller.Principal is not { } principal)
        {
            return [];
        }

        var visibleProcedures = await _procedureCatalog
            .ListVisibleAsync(principal, caller.Tenant, ct)
            .ConfigureAwait(false);
        return DiscoveryProcedureResolver.FilterEligible(visibleProcedures, visibleTables, caller.Tenant);
    }

    private async Task<IReadOnlyList<DiscoveryObject>> ResolveProceduresBatchAsync(
        CatalogCaller caller,
        Sid sid,
        ScopeMemoKey scopeKey,
        IReadOnlyList<EligibleDiscoveryProcedure> items,
        CancellationToken ct)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var results = new DiscoveryObject?[items.Count];
        var uncached = new List<(int Index, EligibleDiscoveryProcedure Item)>(items.Count);

        lock (_memoLock)
        {
            for (var i = 0; i < items.Count; i++)
            {
                var def = items[i].Procedure.Definition;
                var procId = new TableIdentifier(def.CatalogDomain, "procedure", def.Name);
                var objKey = new ObjectMemoKey(scopeKey, DiscoveryObjectKind.Procedure, procId, string.Empty);
                if (_objectMemo.TryGetValue(objKey, out var cached))
                {
                    results[i] = cached;
                }
                else
                {
                    uncached.Add((i, items[i]));
                }
            }
        }

        if (uncached.Count == 0)
        {
            return results.Where(static r => r is not null).Select(static r => r!).ToList();
        }

        var pendingTask = LoadPendingRequestTablesAsync(sid, caller.Tenant, ct);
        var expiredTask = LoadRecentlyExpiredTablesAsync(sid, caller.Tenant, ct);
        await Task.WhenAll(pendingTask, expiredTask).ConfigureAwait(false);

        var subject = SecurityPrincipalContext.FromPrincipal(caller.Principal!, caller.Tenant);
        Dictionary<TableIdentifier, ProtectionClass>? classesByTable = null;
        if (_protectionProvider is not null)
        {
            classesByTable = new Dictionary<TableIdentifier, ProtectionClass>();
            var distinctTables = new List<TableMetadata>();
            var distinctIds = new HashSet<TableIdentifier>();
            foreach (var item in uncached)
            {
                foreach (var gt in item.Item.GovernedTables)
                {
                    if (distinctIds.Add(gt.Identifier))
                    {
                        distinctTables.Add(gt);
                    }
                }
            }

            if (distinctTables.Count > 0)
            {
                var batchProtections = await _protectionProvider.ResolveManyAsync(caller.Tenant, distinctTables, ct).ConfigureAwait(false);
                foreach (var gt in distinctTables)
                {
                    var prot = batchProtections.TryGetValue(gt.Identifier, out var batchProtection)
                        ? batchProtection
                        : await _protectionProvider.GetAsync(caller.Tenant, gt.Identifier, ct).ConfigureAwait(false);
                    classesByTable[gt.Identifier] = prot.TableClass;
                }
            }
        }

        var resolved = await DiscoveryProcedureResolver.ResolveBatchAsync(
            _decisionPoint!,
            _ownershipRepository,
            subject,
            uncached.Select(static x => x.Item).ToList(),
            await pendingTask.ConfigureAwait(false),
            await expiredTask.ConfigureAwait(false),
            ct,
            classesByTable).ConfigureAwait(false);

        lock (_memoLock)
        {
            for (var i = 0; i < uncached.Count; i++)
            {
                var (targetIdx, item) = uncached[i];
                var mapped = resolved[i];
                results[targetIdx] = mapped;
                var procId = new TableIdentifier(item.Procedure.Definition.CatalogDomain, "procedure", item.Procedure.Definition.Name);
                _objectMemo[new ObjectMemoKey(scopeKey, DiscoveryObjectKind.Procedure, procId, string.Empty)] = mapped;
            }
        }

        return results.Where(static r => r is not null).Select(static r => r!).ToList();
    }

    private async Task<IReadOnlySet<TableIdentifier>> LoadPendingRequestTablesAsync(Sid sid, TenantId tenantId, CancellationToken ct)
    {
        if (_approvalRepository is null)
        {
            return new HashSet<TableIdentifier>();
        }

        try
        {
            var requests = await _approvalRepository.GetRequestsByRequesterSidAsync(sid, tenantId, status: null, ct).ConfigureAwait(false);
            var pending = new HashSet<TableIdentifier>();
            foreach (var req in requests)
            {
                if (req.Status.StartsWith("PENDING", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.Status, "ESCALATED", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(req.Status, "SUBMITTED", StringComparison.OrdinalIgnoreCase))
                {
                    pending.Add(req.TableIdentifier);
                }
            }
            return pending;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Pending consent request status lookup failed during discovery.");
            return new HashSet<TableIdentifier>();
        }
    }

    private async Task<IReadOnlySet<TableIdentifier>> LoadRecentlyExpiredTablesAsync(Sid sid, TenantId tenantId, CancellationToken ct)
    {
        try
        {
            var since = _timeProvider.GetUtcNow().AddDays(-ExpiredLookbackDays);
            return await _consentRepository!.GetRecentlyExpiredTableIdsAsync(sid, tenantId, since, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Recently expired consent lookup failed during discovery.");
            return new HashSet<TableIdentifier>();
        }
    }

    private async Task WriteDiscoveryAuditAsync(
        TenantId tenant,
        Sid actorSid,
        DiscoverySurface surface,
        IReadOnlyList<DiscoveryObject> objects,
        string? queryText,
        CancellationToken ct)
    {
        if (_auditWriter is null)
        {
            return;
        }

        var objectIds = objects
            .Take(MaxAuditObjectIds)
            .Select(static o => $"{o.Domain}.{o.Schema}.{o.Name}")
            .ToList();

        var queryHash = string.IsNullOrWhiteSpace(queryText)
            ? null
            : ComputeQueryHash(queryText);

        var entry = new AuditLogEntry
        {
            TenantId = tenant,
            OccurredAt = _timeProvider.GetUtcNow(),
            ActorSid = actorSid,
            EventType = AuditEventTypes.CatalogRead,
            TargetTable = objectIds.Count == 1 ? objectIds[0] : "*",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new
            {
                surface = SurfaceName(surface),
                objectCount = objects.Count,
                objectIds,
                queryHash,
                tenant = tenant.Value
            })
        };

        await _auditWriter.RecordAsync(entry, ct).ConfigureAwait(false);
    }

    private static string SurfaceName(DiscoverySurface surface) => surface switch
    {
        DiscoverySurface.Rest => "rest",
        DiscoverySurface.GraphQl => "graphql",
        DiscoverySurface.Mcp => "mcp",
        DiscoverySurface.Search => "search",
        _ => "rest"
    };

    private static DiscoverySurface DiscoverySurfaceOf(CatalogChannel channel) => channel switch
    {
        CatalogChannel.GraphQl => DiscoverySurface.GraphQl,
        CatalogChannel.Mcp => DiscoverySurface.Mcp,
        _ => DiscoverySurface.Rest
    };

    private static string ComputeQueryHash(string queryText)
    {
        var normalized = queryText.Trim().ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static CatalogSearchMode ParseSearchMode(string? mode) =>
        mode?.Trim().ToLowerInvariant() switch
        {
            "semantic" or "semanticvector" => CatalogSearchMode.SemanticVector,
            "keyword" or "keywordbm25" => CatalogSearchMode.KeywordBm25,
            _ => CatalogSearchMode.Hybrid
        };

    private static bool MatchesNonHiddenText(TableMetadata table, string query)
    {
        if (table.Identifier.TableName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            table.Identifier.Domain.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            table.Identifier.Schema.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (table.Table.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (table.Table.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        foreach (var col in table.Columns)
        {
            if (col.DiscoveryHidden)
            {
                continue;
            }

            if (col.ColumnName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (col.DisplayName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (col.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasEffectiveTag(TableMetadata table, string tag)
    {
        var primary = DiscoveryTextSanitizer.SanitizeTags(table.CatalogTags);
        var effective = primary.Count > 0 ? primary : table.Table.Tags;
        return effective.Any(tg => string.Equals(tg, tag, StringComparison.OrdinalIgnoreCase));
    }

    private ScopeMemoKey BuildScopeMemoKey(Sid sid, CatalogCaller caller)
    {
        var sb = new StringBuilder();
        var roles = caller.Principal!.GetUserRoles().OrderBy(static r => r, StringComparer.OrdinalIgnoreCase);
        foreach (var role in roles)
        {
            sb.Append("r:").Append(role).Append('|');
        }

        var groups = caller.Principal!.GetGroupSids().Select(static g => g.Value).OrderBy(static g => g, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            sb.Append("g:").Append(group).Append('|');
        }

        foreach (var claim in caller.Principal!.Claims.Where(static c =>
            string.Equals(c.Type, "scope", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c.Type, "scp", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static c => c.Value, StringComparer.Ordinal))
        {
            sb.Append("s:").Append(claim.Value).Append('|');
        }

        var claimsHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
        return new ScopeMemoKey(sid.Value, claimsHash, caller.Tenant.Value, caller.Channel);
    }

    private void RecordFailure(string message)
    {
        GatewayDiagnostics.DiscoveryFailuresCounter.Add(1);
        _logger.LogWarning("{Message}", message);
    }

    private void RecordFailure(Exception ex, string message)
    {
        GatewayDiagnostics.DiscoveryFailuresCounter.Add(1);
        _logger.LogWarning(ex, "{Message} ({ExceptionType})", message, ex.GetType().Name);
    }
}
