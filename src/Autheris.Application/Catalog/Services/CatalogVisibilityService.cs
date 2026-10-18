namespace Autheris.Application.Catalog.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #138 INC-1: the one discovery visibility entry point (always <see cref="CatalogSurface.Discovery"/>). Order of one evaluation:
/// <see cref="CallerScopeResolver"/> (once) -> <see cref="CatalogVisibility"/> engine -> shape (NotFound rows dropped) -> ReBAC
/// (<c>can_query</c> through <c>BatchCheckAsync</c>, only ever shrinks) -> memo. Every failure answers empty / null; nothing falls back
/// to an unfiltered list. Scoped: the memo lives and dies with the DI scope.
/// </summary>
public sealed class CatalogVisibilityService : ICatalogVisibilityService
{
    // SEC-CAT138-06: hard bounds, no per-request override.
    private const int ChunkSize = 32;
    private const int MaxParallelChunks = 8;
    private const int MaxCandidates = 256;
    private const string AnonymousMcpClientSid = "ANONYMOUS_MCP_CLIENT";

    private readonly ITableMetadataRepository? _metadataRepository;
    private readonly IConsentRepository? _consentRepository;
    private readonly GatewayOptions? _options;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly ILogger<CatalogVisibilityService> _logger;
    private readonly object _memoLock = new();
    private readonly Dictionary<MemoKey, CatalogVisibleSet> _memo = [];

    /// <summary>A null dependency is allowed at construction and answers empty (SEC-CAT138-01), so a mis-wired host never widens.</summary>
    public CatalogVisibilityService(
        ITableMetadataRepository? metadataRepository,
        IConsentRepository? consentRepository,
        IOptions<GatewayOptions>? gatewayOptions,
        IRebacEvaluator? rebacEvaluator = null,
        ILogger<CatalogVisibilityService>? logger = null)
    {
        _metadataRepository = metadataRepository;
        _consentRepository = consentRepository;
        _options = gatewayOptions?.Value;
        _rebacEvaluator = rebacEvaluator;
        _logger = logger ?? NullLogger<CatalogVisibilityService>.Instance;
    }

    /// <summary>Test seam; null in DI and never assigned by production code (SEC-CAT138-17).</summary>
    internal Func<IReadOnlyList<TableMetadata>, CallerScope, CancellationToken, Task<IReadOnlyList<TableMetadata>>>? EngineOutputForTesting { get; set; }

    private readonly record struct MemoKey(string Subject, string Claims, string Tenant, string Contract, string Surface);

    private sealed record Context(ClaimsPrincipal Principal, Sid Sid, CallerScope Scope, TenantId Tenant, TenancyMode Mode, IReadOnlyCollection<string> SharedTables)
    {
        public bool IsAdminView => CallerScopeResolver.IsAdminView(Scope);
    }

    public async ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, SchemaContractDefinition? contract = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Prepare(caller) is not { } context)
        {
            return CatalogVisibleSet.Empty;
        }

        var key = KeyOf(context, contract);
        if (TryGetMemo(key, out var memoised))
        {
            return memoised;
        }

        IReadOnlyList<TableMetadata> all;
        try
        {
            all = await _metadataRepository!.GetAllTablesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure(ex);
            return CatalogVisibleSet.Empty;
        }

        var visible = await EvaluateAsync(context, all, contract, ct).ConfigureAwait(false);
        if (visible == null)
        {
            return CatalogVisibleSet.Empty;
        }

        var set = new CatalogVisibleSet(visible, context.Scope, context.IsAdminView, all);
        lock (_memoLock)
        {
            _memo[key] = set;
        }

        return set;
    }

    public async ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (Prepare(caller) is not { } context)
        {
            return null;
        }

        TableMetadata? row;
        try
        {
            row = await _metadataRepository!.GetTableMetadataAsync(table, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure(ex);
            return null;
        }

        // The repository does not filter is_active on a single read (SEC-CAT138-07).
        if (row is not { Table.IsActive: true } || row.Identifier != table)
        {
            return null;
        }

        var visible = await EvaluateAsync(context, [row], null, ct).ConfigureAwait(false);
        var match = visible?.FirstOrDefault(r => r.Identifier == table);
        if (match == null)
        {
            return null;
        }

        var shape = CatalogViewShaping.Resolve(context.IsAdminView, match.CatalogView);
        return shape == CatalogOutputShape.NotFound ? null : new CatalogVisibleRow(match, shape);
    }

    public async ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(
        CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (candidates is null or { Count: 0 })
        {
            return new HashSet<TableIdentifier>();
        }

        if (Prepare(caller) is not { } context)
        {
            return new HashSet<TableIdentifier>();
        }

        // De-duplicated and capped: the excess is denied, never evaluated (SEC-CAT138-06).
        var wanted = candidates.Distinct().Take(MaxCandidates).ToHashSet();
        if (TryGetMemo(KeyOf(context, null), out var memoised))
        {
            return wanted.Where(memoised.Contains).ToHashSet();
        }

        IReadOnlyList<TableMetadata> all;
        try
        {
            all = await _metadataRepository!.GetAllTablesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure(ex);
            return new HashSet<TableIdentifier>();
        }

        var rows = all.Where(t => t.Table.IsActive && wanted.Contains(t.Identifier)).ToList();
        var visible = rows.Count == 0 ? null : await EvaluateAsync(context, rows, null, ct).ConfigureAwait(false);
        return visible == null ? new HashSet<TableIdentifier>() : visible.Select(t => t.Identifier).ToHashSet();
    }

    /// <summary>One resolution of the caller class; null means "empty answer" (no repository, ReBAC or memo is touched).</summary>
    private Context? Prepare(CatalogCaller? caller)
    {
        if (caller?.Principal is not { } principal || _metadataRepository == null || _consentRepository == null || _options == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(caller.Tenant.Value))
        {
            return null;
        }

        var sid = principal.GetUserSid();
        if (sid == null || string.Equals(sid.Value.Value, AnonymousMcpClientSid, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var mode = _options.Tenancy.EffectiveMode;
        var scope = CallerScopeResolver.Resolve(principal, mode);
        return scope == CallerScope.None
            ? null
            : new Context(principal, sid.Value, scope, caller.Tenant, mode, _options.Catalog.SharedTables ?? []);
    }

    /// <summary>Engine -> shape -> ReBAC. Null = a dependency failed (already logged); cancellation propagates.</summary>
    private async Task<IReadOnlyList<TableMetadata>?> EvaluateAsync(
        Context context, IReadOnlyList<TableMetadata> rows, SchemaContractDefinition? contract, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<TableMetadata> engineOutput;
            if (EngineOutputForTesting is { } seam)
            {
                engineOutput = await seam(rows, context.Scope, ct).ConfigureAwait(false);
            }
            else
            {
                engineOutput = await CatalogVisibility.VisibleTablesAsync(
                    rows, context.Principal, context.Tenant, _consentRepository, contract, context.Mode, context.SharedTables,
                    CatalogSurface.Discovery, ct).ConfigureAwait(false);
            }

            // F-C11 / SEC-CAT138-15: a row that does not exist for this caller class never reaches the evaluator or the memo.
            var isAdminView = context.IsAdminView;
            var shaped = engineOutput
                .Where(t => CatalogViewShaping.Resolve(isAdminView, t.CatalogView) != CatalogOutputShape.NotFound)
                .ToList();

            return await ApplyRebacAsync(context, shaped, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFailure(ex);
            return null;
        }
    }

    private async Task<IReadOnlyList<TableMetadata>> ApplyRebacAsync(Context context, List<TableMetadata> rows, CancellationToken ct)
    {
        var evaluator = _rebacEvaluator;
        if (evaluator is not { IsEnabled: true })
        {
            // Configured but not resolvable: deny instead of treating it as "off" (SEC-CAT138-02).
            return evaluator == null && _options?.Rebac?.Enabled == true ? [] : rows;
        }

        if (rows.Count == 0)
        {
            return rows;
        }

        var tenant = context.Tenant.Value;
        var subject = context.Sid.Value;
        var chunks = rows.Chunk(ChunkSize).ToArray();
        var allowed = new bool[chunks.Length][];
        var failedTables = 0;

        await Parallel.ForEachAsync(
            Enumerable.Range(0, chunks.Length),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelChunks, CancellationToken = ct },
            async (index, token) =>
            {
                var chunk = chunks[index];
                var checks = chunk
                    .Select(t => new RebacCheckRequest(tenant, subject, RebacTableGate.Relation, RebacTableGate.ObjectId(t.Identifier)))
                    .ToList();
                var flags = new bool[chunk.Length];
                try
                {
                    var result = await evaluator.BatchCheckAsync(new RebacBatchCheckRequest(tenant, checks), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    for (var i = 0; i < checks.Count; i++)
                    {
                        // A decision absent from the result is a deny.
                        flags[i] = result?.Decisions != null && result.Decisions.TryGetValue(checks[i], out var ok) && ok;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Add(ref failedTables, chunk.Length);
                    Array.Clear(flags);
                    _ = ex;
                }

                allowed[index] = flags;
            }).ConfigureAwait(false);

        // The real evaluator stops quietly on cancellation: a partial result must never be returned or memoised.
        ct.ThrowIfCancellationRequested();

        if (failedTables > 0)
        {
            _logger.LogWarning("ReBAC evaluation failed for {HiddenTables} catalog tables; they are hidden (fail-closed).", failedTables);
        }

        var visible = new List<TableMetadata>(rows.Count);
        for (var c = 0; c < chunks.Length; c++)
        {
            for (var i = 0; i < chunks[c].Length; i++)
            {
                if (allowed[c][i])
                {
                    visible.Add(chunks[c][i]);
                }
            }
        }

        return visible;
    }

    private bool TryGetMemo(MemoKey key, out CatalogVisibleSet set)
    {
        lock (_memoLock)
        {
            return _memo.TryGetValue(key, out set!);
        }
    }

    /// <summary>
    /// SEC-CAT138-05: subject SID, everything the consent engine reads from the principal (groups, roles, token scopes, caller class),
    /// the channel-supplied tenant, the contract and the surface. Never the principal reference alone.
    /// </summary>
    private static MemoKey KeyOf(Context context, SchemaContractDefinition? contract)
    {
        var principal = context.Principal;
        var groups = string.Join(',', principal.GetGroupSids().Select(g => g.Value).OrderBy(x => x, StringComparer.Ordinal));
        var roles = string.Join(',', principal.GetUserRoles().OrderBy(x => x, StringComparer.Ordinal));
        var scopes = string.Join(',', Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims).OrderBy(x => x, StringComparer.Ordinal));
        var claims = $"{context.Scope}|{principal.GetTenantId().Value}|g:{groups}|r:{roles}|s:{scopes}";
        return new MemoKey(context.Sid.Value, claims, context.Tenant.Value, ContractKey(contract), nameof(CatalogSurface.Discovery));
    }

    private static string ContractKey(SchemaContractDefinition? contract) => contract == null
        ? string.Empty
        : string.Join(
            '|',
            contract.Name,
            contract.ExcludeInaccessible,
            string.Join(',', contract.IncludedTags.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(',', contract.ExcludedTags.OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(',', contract.AllowedTables.OrderBy(x => x, StringComparer.Ordinal)));

    /// <summary>SEC-CAT138-09: type name only; an exception message may carry a SID or a table name.</summary>
    private void LogFailure(Exception ex) =>
        _logger.LogError("Catalog visibility could not be evaluated ({FailureType}); answering empty (fail-closed).", ex.GetType().Name);
}
