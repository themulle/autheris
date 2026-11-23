namespace Autheris.Application.Catalog.Services;

using Autheris.Application.Tenancy;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Visibility;
using Autheris.Application.Policy.Protection;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Contracts;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// Issue #138 INC-1: the data-access-side catalog visibility entry point (consents, admin and owner views; authorized discovery has its own path). Order of one evaluation:
/// <see cref="CallerScopeResolver"/> (once) -> <see cref="CatalogVisibility"/> engine -> shape (NotFound rows dropped) -> transport
/// filter -> memo (no further filter; hidden objects stay hidden, a grant is not needed to discover). Every failure answers empty / null; nothing falls back
/// to an unfiltered list. Scoped: the memo lives and dies with the DI scope.
/// </summary>
public sealed class CatalogVisibilityService : ICatalogVisibilityService
{
    // hard bounds, no per-request override.
    private const int MaxParallelChunks = 8;
    private const int MaxCandidates = 256;
    private const string AnonymousMcpClientSid = "ANONYMOUS_MCP_CLIENT";

    private readonly ITableMetadataRepository? _metadataRepository;
    private readonly IConsentRepository? _consentRepository;
    private readonly GatewayOptions? _options;
    private readonly ILogger<CatalogVisibilityService> _logger;
    private readonly IIngressTransportContext? _ingress;
    private readonly IDataSourceOptionsResolver? _transportResolver;
    private readonly ICatalogSnapshotProvider? _snapshotProvider;
    private readonly IProtectionSnapshotProvider? _protectionProvider;
    private readonly IContainerVisibilitySnapshotProvider? _containerVisibility;
    private readonly IUnmaskGrantResolver? _unmaskResolver;
    private readonly Lock _memoLock = new();
    private readonly Dictionary<MemoKey, CatalogVisibleSet> _memo = [];

    /// <summary>A null dependency is allowed at construction and answers empty, so a mis-wired host never widens.</summary>
    public CatalogVisibilityService(
        ITableMetadataRepository? metadataRepository,
        IConsentRepository? consentRepository,
        IOptions<GatewayOptions>? gatewayOptions,
        ILogger<CatalogVisibilityService>? logger = null,
        IIngressTransportContext? ingress = null,
        IDataSourceOptionsResolver? transportResolver = null,
        ICatalogSnapshotProvider? snapshotProvider = null,
        IProtectionSnapshotProvider? protectionProvider = null,
        IContainerVisibilitySnapshotProvider? containerVisibility = null,
        IUnmaskGrantResolver? unmaskResolver = null)
    {
        _containerVisibility = containerVisibility;
        _unmaskResolver = unmaskResolver;
        _ingress = ingress;
        _transportResolver = transportResolver;
        _snapshotProvider = snapshotProvider;
        _protectionProvider = protectionProvider;
        _metadataRepository = metadataRepository;
        _consentRepository = consentRepository;
        _options = gatewayOptions?.Value;
        _logger = logger ?? NullLogger<CatalogVisibilityService>.Instance;
    }

    /// <summary>Test seam; null in DI and never assigned by production code.</summary>
    internal Func<IReadOnlyList<TableMetadata>, CallerScope, CancellationToken, Task<IReadOnlyList<TableMetadata>>>? EngineOutputForTesting { get; set; }

    private readonly record struct MemoKey(string Subject, string Claims, string Tenant, string Contract, string Surface);

    private sealed record Context(
        ClaimsPrincipal Principal, Sid Sid, CallerScope Scope, TenantId Tenant, TenantId? CatalogOwner, IReadOnlyCollection<string> SharedTables, MemoKey BaseKey)
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
            if (_snapshotProvider is not null)
            {
                var snapshot = await _snapshotProvider.GetSnapshotAsync(ct).ConfigureAwait(false);
                all = snapshot.Tables.Where(static t => t.Table.IsActive).ToList();
            }
            else
            {
                all = await CatalogTablePageReader.ReadAllAsync(
                    _metadataRepository!,
                    new CatalogTableQuery(IncludeInactive: false),
                    static t => t.Table.IsActive,
                    ct: ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
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
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailure(ex);
            return null;
        }

        // The repository does not filter is_active on a single read.
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

        // De-duplicated and capped: the excess is denied, never evaluated.
        var wanted = new HashSet<TableIdentifier>(Math.Min(candidates.Count, MaxCandidates));
        foreach (var candidate in candidates)
        {
            if (wanted.Count == MaxCandidates)
            {
                break;
            }

            wanted.Add(candidate);
        }

        if (TryGetMemo(KeyOf(context, null), out var memoised))
        {
            var fromMemo = new HashSet<TableIdentifier>(wanted.Count);
            foreach (var id in wanted)
            {
                if (memoised.Contains(id))
                {
                    fromMemo.Add(id);
                }
            }

            return fromMemo;
        }

        // Bounded read: only the candidates are loaded (no whole-catalog read), at most MaxParallelChunks reads in flight.
        var ordered = wanted.ToArray();
        var loaded = new TableMetadata?[ordered.Length];
        var failedReads = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, ordered.Length),
            new ParallelOptions { MaxDegreeOfParallelism = MaxParallelChunks, CancellationToken = ct },
            async (index, token) =>
            {
                try
                {
                    loaded[index] = await _metadataRepository!.GetTableMetadataAsync(ordered[index], token).ConfigureAwait(false);
                }
                catch (Exception) when (!token.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failedReads);
                }
            }).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (failedReads > 0)
        {
            _logger.LogError("Catalog visibility could not read {FailedReads} candidate tables; they are denied (fail-closed).", failedReads);
        }

        var rows = new List<TableMetadata>(ordered.Length);
        for (var i = 0; i < ordered.Length; i++)
        {
            // The repository does not filter is_active on a single read, and the row must be the one asked for.
            if (loaded[i] is { Table.IsActive: true } row && row.Identifier == ordered[i])
            {
                rows.Add(row);
            }
        }

        var visible = rows.Count == 0 ? null : await EvaluateAsync(context, rows, null, ct).ConfigureAwait(false);
        var result = new HashSet<TableIdentifier>(visible?.Count ?? 0);
        if (visible != null)
        {
            foreach (var table in visible)
            {
                result.Add(table.Identifier);
            }
        }

        return result;
    }

    /// <summary>One resolution of the caller class; null means "empty answer" (no repository or memo is touched).</summary>
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

        var catalogOwner = TenantResolver.EffectiveDefaultOf(_options.Tenancy);
        var resolution = CallerScopeResolver.ResolutionOf(principal, catalogOwner);
        var scope = CallerScopeResolver.Resolve(principal, resolution, catalogOwner);
        if (scope == CallerScope.None || resolution.Outcome == Autheris.Domain.Tenancy.TenantResolutionOutcome.Invalid)
        {
            // An invalid tenant claim answers empty for every caller class, the ClusterAdmin included (fail closed, never a wider class).
            return null;
        }

        // The memo key reads the tenant claim, which throws on an invalid value: computed once, here, under the fail-closed rule.
        MemoKey baseKey;
        try
        {
            baseKey = BaseKeyOf(principal, sid.Value, scope, resolution.Tenant ?? caller.Tenant, caller.Tenant);
        }
        catch (Exception ex)
        {
            LogFailure(ex);
            return null;
        }

        // the memo is per scope, but the key still carries the ingress so a listing never crosses transports.
        baseKey = baseKey with { Claims = $"{baseKey.Claims}|i:{(_ingress?.Current ?? DataSourceTransportFlags.None)}" };
        return new Context(principal, sid.Value, scope, caller.Tenant, catalogOwner, _options.Catalog.SharedTables ?? [], baseKey);
    }

    /// <summary>Engine -> shape. Null = a dependency failed (already logged); cancellation propagates.</summary>
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
                Dictionary<TableIdentifier, Autheris.Domain.Security.ProtectionClass>? tableClasses = null;
                if (contract is not null &&
                    (contract.IncludedClasses.Count > 0 || contract.ExcludedClasses.Count > 0) &&
                    _protectionProvider is not null)
                {
                    tableClasses = new Dictionary<TableIdentifier, Autheris.Domain.Security.ProtectionClass>(rows.Count);
                    foreach (var row in rows)
                    {
                        var prot = await _protectionProvider.GetAsync(context.Tenant, row.Identifier, ct).ConfigureAwait(false);
                        tableClasses[row.Identifier] = prot.TableClass;
                    }
                }

                engineOutput = await CatalogVisibility.VisibleTablesAsync(
                    rows, context.Principal, context.Tenant, _consentRepository, contract, context.CatalogOwner, context.SharedTables,
                    tableClasses, ct).ConfigureAwait(false);
                engineOutput = await AddContainerVisibleAsync(context, rows, engineOutput, contract, ct).ConfigureAwait(false);
            }

            // F-C11 / a row that does not exist for this caller class never reaches the evaluator or the memo.
            var isAdminView = context.IsAdminView;
            var shaped = engineOutput
                .Where(t => CatalogViewShaping.Resolve(isAdminView, t.CatalogView) != CatalogOutputShape.NotFound)
                .ToList();

            // Issue #447 a table whose source does not allow the ingress transport is hidden, never an error. No ingress
            // (an admin context) leaves the list unfiltered.
            var transport = _ingress?.Current ?? DataSourceTransportFlags.None;
            if (transport != DataSourceTransportFlags.None && _transportResolver is not null)
            {
                shaped = [.. TransportDiscoveryFilter.Apply(_transportResolver, transport, shaped)];
            }

            return shaped;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogFailure(ex);
            return null;
        }
    }

    /// <summary>
    /// Container visibility rule: besides the consent path, a table is visible when its container is <c>Open</c>, the caller holds a
    /// catalog grant on the container, an unmask grant on the table, or is an owner-group member (Data Governance and IT see everything
    /// through their own caller scope). All column definitions are listed; the rows carry no data access. An object that is not
    /// provably visible stays absent, exactly like a missing one. Any failure adds nothing (fail-closed).
    /// </summary>
    private async Task<IReadOnlyList<TableMetadata>> AddContainerVisibleAsync(
        Context context,
        IReadOnlyList<TableMetadata> rows,
        IReadOnlyList<TableMetadata> engineOutput,
        SchemaContractDefinition? contract,
        CancellationToken ct)
    {
        if (_containerVisibility is null || _protectionProvider is null || context.Scope != CallerScope.Subject || rows.Count == 0)
        {
            return engineOutput;
        }

        var snapshot = await _containerVisibility.GetAsync(context.Tenant, context.Principal, ct).ConfigureAwait(false);
        var already = new HashSet<TableIdentifier>(engineOutput.Count);
        foreach (var table in engineOutput)
        {
            already.Add(table.Identifier);
        }

        var candidates = CatalogVisibility.DropForeignVirtualTables(rows.Where(r => !already.Contains(r.Identifier)).ToList(), context.Tenant);
        var extras = new List<TableMetadata>();
        var classes = new Dictionary<TableIdentifier, Autheris.Domain.Security.ProtectionClass>();
        foreach (var row in candidates)
        {
            EffectiveProtection protection;
            try
            {
                protection = await _protectionProvider.GetAsync(context.Tenant, row.Identifier, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogFailure(ex);
                continue;
            }

            var visible = snapshot.IsTableVisible(protection.Container, row.Identifier)
                || await HasUnmaskGrantAsync(context, snapshot, protection, row, ct).ConfigureAwait(false);
            if (!visible)
            {
                continue;
            }

            classes[row.Identifier] = protection.TableClass;
            extras.Add(row with { CatalogView = CatalogView.Granted });
        }

        if (extras.Count == 0)
        {
            return engineOutput;
        }

        var allowed = CatalogVisibility.FilterByContract(extras, contract, classes);
        return allowed.Count == 0 ? engineOutput : [.. engineOutput, .. allowed];
    }

    private async ValueTask<bool> HasUnmaskGrantAsync(
        Context context, ContainerVisibilitySnapshot snapshot, EffectiveProtection protection, TableMetadata row, CancellationToken ct)
    {
        if (_unmaskResolver is null || !snapshot.GroupsComplete)
        {
            return false;
        }

        // An unmask grant can only exist on a table that has a mask category.
        if (string.IsNullOrWhiteSpace(protection.TableMaskCategory)
            && !protection.Columns.Values.Any(static c => !string.IsNullOrWhiteSpace(c.EffectiveMaskCategory)))
        {
            return false;
        }

        var subjects = new List<Sid>(context.Principal.GetGroupSids()) { context.Sid };
        var coverage = await _unmaskResolver.ResolveAsync(context.Tenant, subjects, snapshot.GroupsComplete, row.Identifier, ct).ConfigureAwait(false);
        return !coverage.IsEmpty;
    }

    private bool TryGetMemo(MemoKey key, out CatalogVisibleSet set)
    {
        lock (_memoLock)
        {
            return _memo.TryGetValue(key, out set!);
        }
    }

    /// <summary>
    /// subject SID, everything the consent engine reads from the principal (groups, roles, token scopes, caller class),
    /// the channel-supplied tenant, the contract and the surface. Never the principal reference alone.
    /// </summary>
    private static MemoKey BaseKeyOf(ClaimsPrincipal principal, Sid sid, CallerScope scope, TenantId resolvedTenant, TenantId tenant)
    {
        var groups = string.Join(',', principal.GetGroupSids().Select(g => g.Value).OrderBy(x => x, StringComparer.Ordinal));
        var roles = string.Join(',', principal.GetUserRoles().OrderBy(x => x, StringComparer.Ordinal));
        var scopes = string.Join(',', Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims).OrderBy(x => x, StringComparer.Ordinal));
        var claims = $"{scope}|{resolvedTenant.Value}|g:{groups}|r:{roles}|s:{scopes}";
        return new MemoKey(sid.Value, claims, tenant.Value, string.Empty, "data-access");
    }

    private static MemoKey KeyOf(Context context, SchemaContractDefinition? contract) =>
        contract == null ? context.BaseKey : context.BaseKey with { Contract = ContractKey(contract) };

    private static string ContractKey(SchemaContractDefinition? contract) => contract == null
        ? string.Empty
        : string.Join(
            '|',
            contract.Name,
            contract.ExcludeInaccessible,
            string.Join(',', contract.IncludedClasses.Select(static c => c.ToString()).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(',', contract.ExcludedClasses.Select(static c => c.ToString()).OrderBy(x => x, StringComparer.Ordinal)),
            string.Join(',', contract.AllowedTables.OrderBy(x => x, StringComparer.Ordinal)));

    /// <summary>type name only; an exception message may carry a SID or a table name.</summary>
    private void LogFailure(Exception ex) =>
        _logger.LogError("Catalog visibility could not be evaluated ({FailureType}); answering empty (fail-closed).", ex.GetType().Name);
}
