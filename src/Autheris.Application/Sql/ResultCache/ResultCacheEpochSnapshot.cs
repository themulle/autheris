namespace Autheris.Application.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Policy.Services;
using Autheris.Domain.Common;

/// <summary>Names of the invalidation causes counted by the result cache metrics.</summary>
public static class WebSqlResultCacheInvalidationReasons
{
    public const string GovernanceEpoch = "governance_epoch";
    public const string TableEpoch = "table_epoch";
    public const string Ttl = "ttl";
    public const string Evicted = "evicted";
    public const string UserCap = "user_cap";
}

/// <summary>
/// The epochs a cached result depends on: the durable governance epoch of the tenant, the authoritative epochs of the accessed
/// tables, and the stamp of the in-process invalidation log. Any component that cannot be read makes the snapshot unavailable, and
/// the cache then neither serves nor stores (fail closed).
/// </summary>
public sealed class ResultCacheEpochSnapshot
{
    public ResultCacheEpochSnapshot(
        long governanceEpoch, IReadOnlyDictionary<TableIdentifier, long> tableEpochs, long generation, long clusterSequence)
    {
        GovernanceEpoch = governanceEpoch;
        TableEpochs = tableEpochs ?? throw new ArgumentNullException(nameof(tableEpochs));
        Generation = generation;
        ClusterSequence = clusterSequence;
    }

    public long GovernanceEpoch { get; }

    public IReadOnlyDictionary<TableIdentifier, long> TableEpochs { get; }

    public long Generation { get; }

    public long ClusterSequence { get; }

    /// <summary>Reads the stamp. Returns null when any source is missing, not authoritative or throws.</summary>
    public static async ValueTask<ResultCacheEpochSnapshot?> TryCaptureAsync(
        GovernanceEpochProvider? governance,
        IEpochValidationService? epochs,
        TenantId tenant,
        IReadOnlyList<TableIdentifier> tables,
        CancellationToken ct)
    {
        if (governance is null || epochs is null)
        {
            return null;
        }

        try
        {
            if (epochs.InvalidationGeneration is not { } generation)
            {
                return null;
            }

            var governanceEpoch = await governance.GetEpochAsync(tenant, ct).ConfigureAwait(false);
            var tableEpochs = await epochs.TryGetAuthoritativeEpochsAsync(tables, ct).ConfigureAwait(false);
            if (tableEpochs is null)
            {
                return null;
            }

            foreach (var table in tables)
            {
                if (!tableEpochs.ContainsKey(table))
                {
                    return null;
                }
            }

            if (await epochs.GetClusterInvalidationSequenceAsync(ct).ConfigureAwait(false) is not { } cluster)
            {
                return null;
            }

            return new ResultCacheEpochSnapshot(governanceEpoch, tableEpochs, generation, cluster);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Null when a result stored under <paramref name="stored"/> is still valid at <paramref name="current"/>, otherwise the
    /// invalidation reason. The check fails closed: a log that cannot prove completeness invalidates.
    /// </summary>
    public static string? FindInvalidation(
        ResultCacheEpochSnapshot stored,
        ResultCacheEpochSnapshot current,
        IEpochValidationService epochs,
        IReadOnlyList<TableIdentifier> tables)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(epochs);
        ArgumentNullException.ThrowIfNull(tables);

        if (stored.GovernanceEpoch != current.GovernanceEpoch)
        {
            return WebSqlResultCacheInvalidationReasons.GovernanceEpoch;
        }

        foreach (var table in tables)
        {
            if (!stored.TableEpochs.TryGetValue(table, out var before) ||
                !current.TableEpochs.TryGetValue(table, out var now) ||
                before != now)
            {
                return WebSqlResultCacheInvalidationReasons.TableEpoch;
            }
        }

        IReadOnlySet<string>? invalidated;
        try
        {
            invalidated = epochs.GetInvalidatedSince(stored.Generation);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return WebSqlResultCacheInvalidationReasons.TableEpoch;
        }

        if (invalidated is null || invalidated.Contains(PolicyInvalidationLog.AllTables))
        {
            return WebSqlResultCacheInvalidationReasons.TableEpoch;
        }

        foreach (var table in tables)
        {
            if (invalidated.Contains(table.ToString().ToLowerInvariant()))
            {
                return WebSqlResultCacheInvalidationReasons.TableEpoch;
            }
        }

        // Every cluster announcement must have reached the local log; otherwise one was missed.
        return current.Generation - stored.Generation < current.ClusterSequence - stored.ClusterSequence
            ? WebSqlResultCacheInvalidationReasons.TableEpoch
            : null;
    }
}
