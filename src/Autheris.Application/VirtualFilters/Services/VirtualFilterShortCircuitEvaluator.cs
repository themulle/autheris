namespace Autheris.Application.VirtualFilters.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;

public sealed record ShortCircuitEvaluationResult
{
    public bool IsHandled { get; init; }
    public bool IsAllowed { get; init; }
    public VirtualFilter? MatchedFilter { get; init; }
    public FilterBinding? MatchedBinding { get; init; }
    public string? Reason { get; init; }

    public static ShortCircuitEvaluationResult NotHandled => new() { IsHandled = false, IsAllowed = true };

    public static ShortCircuitEvaluationResult Allowed(VirtualFilter filter, FilterBinding binding) =>
        new() { IsHandled = true, IsAllowed = true, MatchedFilter = filter, MatchedBinding = binding };

    public static ShortCircuitEvaluationResult Miss(VirtualFilter filter, FilterBinding binding, string reason) =>
        new() { IsHandled = true, IsAllowed = false, MatchedFilter = filter, MatchedBinding = binding, Reason = reason };
}

public interface IVirtualFilterShortCircuitEvaluator
{
    ValueTask<ShortCircuitEvaluationResult> EvaluateAsync(
        TableIdentifier table,
        TableMetadata metadata,
        IReadOnlyDictionary<string, string> queriedKeyValues,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default);
}

public sealed class VirtualFilterShortCircuitEvaluator : IVirtualFilterShortCircuitEvaluator
{
    private readonly IVirtualFilterSnapshotProvider _snapshotProvider;
    private readonly IVirtualFilterKeyProvider _keyProvider;
    private readonly ILogger<VirtualFilterShortCircuitEvaluator>? _logger;

    public VirtualFilterShortCircuitEvaluator(
        IVirtualFilterSnapshotProvider snapshotProvider,
        IVirtualFilterKeyProvider keyProvider,
        ILogger<VirtualFilterShortCircuitEvaluator>? logger = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
        _logger = logger;
    }

    public async ValueTask<ShortCircuitEvaluationResult> EvaluateAsync(
        TableIdentifier table,
        TableMetadata metadata,
        IReadOnlyDictionary<string, string> queriedKeyValues,
        ClaimsPrincipal user,
        TenantId tenantId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(queriedKeyValues);
        ArgumentNullException.ThrowIfNull(user);

        var snapshot = await _snapshotProvider.GetAsync(ct).ConfigureAwait(false);
        if (snapshot.Profiles.Count == 0 || snapshot.Filters.Count == 0)
        {
            return ShortCircuitEvaluationResult.NotHandled;
        }

        var userSid = user.GetUserSid() ?? new Sid("anonymous");
        var groupSids = user.GetGroupSids();
        var roles = user.GetUserRoles();
        var allUserSids = user.GetAllUserSids();

        var activeProfiles = snapshot.Profiles
            .Where(p => p.TenantId == tenantId &&
                        p.Status == FilterApprovalStatus.Active &&
                        GranteeMatcher.Matches(p.GranteeType, p.GranteeSid, p.RoleName, null, userSid, groupSids, roles, allUserSids))
            .ToList();

        var filtersByName = snapshot.Filters
            .Where(f => f.TenantId == tenantId && f.Status == FilterApprovalStatus.Active)
            .ToDictionary(f => f.Name, StringComparer.Ordinal);

        foreach (var profile in activeProfiles)
        {
            foreach (var binding in profile.Bindings)
            {
                if (!binding.MatchesTarget(table))
                {
                    continue;
                }

                if (!filtersByName.TryGetValue(binding.FilterName, out var filter))
                {
                    continue;
                }

                // Map required target columns for this virtual filter
                var targetKeyColumns = VirtualFilterColumns.RequiredTargetColumns(filter, binding);
                if (targetKeyColumns.Count == 0)
                {
                    continue;
                }

                // Check if all required target key columns are present in the point predicate
                bool allKeysPresent = targetKeyColumns.All(col => queriedKeyValues.ContainsKey(col) ||
                    queriedKeyValues.Any(kv => string.Equals(kv.Key, col, StringComparison.OrdinalIgnoreCase)));

                if (allKeysPresent)
                {
                    // Point Lookup Evaluation
                    if (targetKeyColumns.Count == 1)
                    {
                        var targetCol = targetKeyColumns[0];
                        var queriedVal = queriedKeyValues.First(kv => string.Equals(kv.Key, targetCol, StringComparison.OrdinalIgnoreCase)).Value;

                        var filterKeyCol = filter.KeyColumns.FirstOrDefault() ?? targetCol;
                        var allowedKeys = await _keyProvider.GetAllowedKeysAsync(filter, user, tenantId, filterKeyCol, ct).ConfigureAwait(false);

                        if (!allowedKeys.Contains(queriedVal))
                        {
                            _logger?.LogInformation("Short-circuit miss on virtual filter {Filter} for key {Key}={Val}", filter.Name, targetCol, queriedVal);
                            return ShortCircuitEvaluationResult.Miss(filter, binding, $"Queried key '{queriedVal}' is not authorized by virtual filter '{filter.Name}'.");
                        }

                        return ShortCircuitEvaluationResult.Allowed(filter, binding);
                    }
                    else
                    {
                        // Composite Key Point Lookup
                        var allowedTuples = await _keyProvider.GetAllowedKeyTuplesAsync(filter, user, tenantId, filter.KeyColumns, ct).ConfigureAwait(false);

                        bool tupleMatches = false;
                        foreach (var tuple in allowedTuples)
                        {
                            bool allMatch = true;
                            foreach (var targetCol in targetKeyColumns)
                            {
                                var filterCol = binding.ColumnMap != null && binding.ColumnMap.Any(kv => string.Equals(kv.Value, targetCol, StringComparison.OrdinalIgnoreCase))
                                    ? binding.ColumnMap.First(kv => string.Equals(kv.Value, targetCol, StringComparison.OrdinalIgnoreCase)).Key
                                    : targetCol;

                                var queriedVal = queriedKeyValues.First(kv => string.Equals(kv.Key, targetCol, StringComparison.OrdinalIgnoreCase)).Value;
                                tuple.TryGetValue(filterCol, out var tupleVal);

                                if (!string.Equals(tupleVal?.ToString(), queriedVal, StringComparison.OrdinalIgnoreCase))
                                {
                                    allMatch = false;
                                    break;
                                }
                            }

                            if (allMatch)
                            {
                                tupleMatches = true;
                                break;
                            }
                        }

                        if (!tupleMatches)
                        {
                            _logger?.LogInformation("Short-circuit composite miss on virtual filter {Filter}", filter.Name);
                            return ShortCircuitEvaluationResult.Miss(filter, binding, $"Queried composite key is not authorized by virtual filter '{filter.Name}'.");
                        }

                        return ShortCircuitEvaluationResult.Allowed(filter, binding);
                    }
                }
                else if (binding.Strategy == VirtualFilterExecutionStrategy.ShortCircuitOnly)
                {
                    return ShortCircuitEvaluationResult.Miss(filter, binding, $"Full table scan or non-point query is prohibited under ShortCircuitOnly strategy for filter '{filter.Name}'.");
                }
            }
        }

        return ShortCircuitEvaluationResult.NotHandled;
    }
}
