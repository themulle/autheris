namespace Autheris.Application.VirtualFilters;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

/// <summary>Who changes virtual filters; <see cref="IsSync"/> is the file repository sync (the only writer of managed rows).</summary>
public sealed record VirtualFilterActor(
    Sid Sid,
    bool IsSync,
    IReadOnlyCollection<string>? Identifiers = null,
    IReadOnlySet<string>? Roles = null)
{
    public string FormatAllIdentifiers()
    {
        var list = new List<string>();
        if (!string.IsNullOrWhiteSpace(Sid.Value))
        {
            list.Add(Sid.Value);
        }
        if (Identifiers != null)
        {
            foreach (var id in Identifiers)
            {
                if (!string.IsNullOrWhiteSpace(id) && !list.Contains(id, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(id);
                }
            }
        }
        return string.Join(";", list);
    }

    public bool MatchesAny(IEnumerable<string> contributorIdentifiers)
    {
        if (contributorIdentifiers == null) return false;
        var myIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(Sid.Value))
        {
            myIds.Add(Sid.Value);
        }
        if (Identifiers != null)
        {
            foreach (var id in Identifiers)
            {
                if (!string.IsNullOrWhiteSpace(id)) myIds.Add(id);
            }
        }

        foreach (var c in contributorIdentifiers)
        {
            if (myIds.Contains(c)) return true;
        }

        return false;
    }
}

/// <summary>A row managed by the file repository was changed outside the sync (HTTP 409).</summary>
public sealed class ManagedResourceLockedException(string message) : InvalidOperationException(message);

/// <summary>The change conflicts with existing rows or needs <c>force</c> (HTTP 409).</summary>
public sealed class VirtualFilterConflictException(string message) : InvalidOperationException(message);

/// <summary>Desired state of one tenant from the file repository.</summary>
public sealed record VirtualFilterSyncRequest(
    TenantId TenantId,
    ManagedBy ManagedBy,
    IReadOnlyList<VirtualFilter> Filters,
    IReadOnlyList<VirtualFilterAccessProfile> Profiles)
{
    /// <summary>Bindings (by reference) whose <c>max_pushdown_keys</c> the caller stated explicitly; null when the caller cannot tell.</summary>
    public IReadOnlySet<FilterBinding>? ExplicitKeyBudgets { get; init; }
}

/// <summary>
/// Differences between the file repository and Autheris. Drift = a stored row changed outside Autheris.
/// <see cref="RemovedBindings"/> counts bindings that disappear (they widen what grantees see).
/// </summary>
public sealed record VirtualFilterSyncPlan(
    IReadOnlyList<string> CreateFilters,
    IReadOnlyList<string> UpdateFilters,
    IReadOnlyList<string> DeleteFilters,
    IReadOnlyList<string> DriftedFilters,
    IReadOnlyList<string> CreateProfiles,
    IReadOnlyList<string> UpdateProfiles,
    IReadOnlyList<string> DeleteProfiles,
    IReadOnlyList<string> DriftedProfiles,
    int RemovedBindings,
    bool RequiresForce,
    int TotalRelaxations = 0)
{
    public bool HasChanges =>
        CreateFilters.Count + UpdateFilters.Count + DeleteFilters.Count +
        CreateProfiles.Count + UpdateProfiles.Count + DeleteProfiles.Count > 0;
}

/// <summary>
/// Virtual filters: the rules for changing filters and access profiles. Every change is validated, audited and applied
/// in one transaction (which increments the generation). Rows managed by the file repository (Talos) only change
/// through <see cref="ApplySyncAsync"/>.
/// </summary>
public sealed partial class VirtualFilterAdministrationService
{
    private readonly IVirtualFilterRepository _repository;
    private readonly IAuditLogRepository _audit;
    private readonly VirtualFilterOptions _options;
    private readonly VirtualFilterFederationOptions _federation;
    private readonly IVirtualFilterSnapshotProvider? _snapshots;
    private readonly ITableMetadataRepository? _catalog;
    private readonly Autheris.Application.Identity.Attributes.ICallerAttributeRegistry? _callerAttributes;
    private readonly SqlDataSourceOptions? _dataSources;
    private readonly IContainerOwnership? _ownership;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TenantId, List<(DateTimeOffset Timestamp, int Count)>> _removalHistory = new();

    private readonly Autheris.Application.State.IDistributedClusterStateProvider _clusterState;

    public VirtualFilterAdministrationService(
        IVirtualFilterRepository repository,
        IAuditLogRepository audit,
        IOptions<GatewayOptions>? options = null,
        IVirtualFilterSnapshotProvider? snapshots = null,
        ITableMetadataRepository? catalog = null,
        Autheris.Application.Identity.Attributes.ICallerAttributeRegistry? callerAttributes = null,
        Autheris.Application.State.IDistributedClusterStateProvider? clusterState = null,
        IContainerOwnership? ownership = null)
    {
        _callerAttributes = options?.Value?.CallerAttributes?.Enabled == true ? callerAttributes : null;
        _catalog = catalog;
        _dataSources = options?.Value?.DataSources;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options?.Value?.VirtualFilters ?? new VirtualFilterOptions();
        _federation = _options.Federation ?? new VirtualFilterFederationOptions();
        _snapshots = snapshots;
        _clusterState = clusterState ?? new Autheris.Application.State.InMemoryClusterStateProvider();
        _ownership = ownership;
    }

    public VirtualFilterOptions Options => _options;

    internal const string WebhookDedupeKeyPrefix = "vf:webhook:";
    private static readonly TimeSpan WebhookDedupeTtl = TimeSpan.FromHours(24);
    private DateTimeOffset? _lastWebhookTriggerAt;
    private string? _lastWebhookDeliveryId;

    public DateTimeOffset? LastWebhookTriggerAt => _lastWebhookTriggerAt;
    public string? LastWebhookDeliveryId => _lastWebhookDeliveryId;

    public async Task<bool> RecordWebhookTriggerAsync(string bodyHashHex, string deliveryId, DateTimeOffset timestamp, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyHashHex);
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        var key = WebhookDedupeKeyPrefix + bodyHashHex;
        var existing = await _clusterState.GetAsync<string>(key, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return false;
        }

        await _clusterState.SetAsync(key, deliveryId, WebhookDedupeTtl, ct).ConfigureAwait(false);
        _lastWebhookDeliveryId = deliveryId;
        _lastWebhookTriggerAt = timestamp;
        return true;
    }

    /// <summary>Writes and makes the change visible to enforcement on this instance at once.</summary>
    private async Task ApplyAsync(VirtualFilterChangeSet changes, long expectedGeneration, CancellationToken ct)
    {
        // PERS-13: the change is bound to the generation of the snapshot it was computed from.
        await _repository.ApplyAsync(changes with { ExpectedGeneration = expectedGeneration }, ct).ConfigureAwait(false);
        _snapshots?.Invalidate();
    }

    public Task<VirtualFilterSnapshot> GetSnapshotAsync(CancellationToken ct = default) => _repository.LoadSnapshotAsync(ct);

    public async Task<VirtualFilter> SaveFilterAsync(VirtualFilter filter, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(actor);
        if (filter.Sql != null && !actor.IsSync)
        {
            await EnsureMaySaveSqlFilterAsync(filter, actor, ct).ConfigureAwait(false);
        }

        filter = await ValidateDefinitionAsync(filter, ct).ConfigureAwait(false);

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, filter.TenantId, filter.Name);
        EnsureWritable(existing?.ManagedBy, filter.ManagedBy, actor, $"virtual filter '{filter.Name}'");
        ValidateSupersedes(snapshot.Filters.Where(f => f.TenantId == filter.TenantId && f.Name != filter.Name).Append(filter).ToList());

        // Single approval: the save of a writer who holds the capacity activates the filter at once.
        var direct = filter with
        {
            Id = existing?.Id ?? filter.Id,
            Status = FilterApprovalStatus.Active,
            CreatedBy = existing?.CreatedBy ?? actor.Sid,
            ApprovedBy = actor.IsSync ? actor.Sid : (existing?.ApprovedBy ?? actor.Sid),
            ApprovedAt = existing?.ApprovedAt ?? DateTimeOffset.UtcNow,
            UpdatedBy = actor.Sid.Value,
            UpdatedAt = DateTimeOffset.UtcNow,
            Draft = null,
            PendingDeletion = false,
            DeletionRequestedBy = null,
            ReferencedContainers = await RecordReferencedContainersAsync(filter, ct).ConfigureAwait(false)
        };

        var evt = existing == null ? "VIRTUAL_FILTER_CREATED" : "VIRTUAL_FILTER_UPDATED";
        // Fail closed: the change is audited (hashes only) before it takes effect; a failing audit denies it.
        await AuditAsync(filter.TenantId, actor, evt,
            $"virtual_filter:{filter.Name}", new { name = filter.Name, status = direct.Status.ToString(), before = existing?.ComputeDefinitionHash(), after = filter.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
        await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [direct] }, snapshot.Generation, ct).ConfigureAwait(false);
        return direct;
    }

    /// <summary>The referenced tables of a SQL filter with their current container, recorded so that a later move deactivates the filter.</summary>
    private async Task<IReadOnlyList<ReferencedTableContainer>> RecordReferencedContainersAsync(VirtualFilter filter, CancellationToken ct)
    {
        if (filter.Sql == null || _ownership is null)
        {
            return [];
        }

        var recorded = new List<ReferencedTableContainer>();
        foreach (var table in SqlFilterCompiler.ReferencedTables(filter).DistinctBy(t => t.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            var key = await _ownership.GetContainerKeyForTableAsync(table, ct).ConfigureAwait(false) ?? ReferencedContainerKeys.Unassigned;
            recorded.Add(new ReferencedTableContainer(table.ToString(), key));
        }

        return recorded;
    }

    /// <summary>
    /// A SQL filter activates on save only when the author is GovernanceAdmin, or an owner-group member of the container of every table it
    /// references (it references at least one). Anyone else, IT included, is refused and nothing is stored, so the filter never becomes active.
    /// </summary>
    private async Task EnsureMaySaveSqlFilterAsync(VirtualFilter filter, VirtualFilterActor actor, CancellationToken ct)
    {
        if (actor.Roles?.Contains(nameof(Autheris.Domain.Security.GatewayRole.GovernanceAdmin), StringComparer.OrdinalIgnoreCase) == true)
        {
            return;
        }

        var referenced = SqlFilterCompiler.ReferencedTables(filter).Distinct().ToList();
        if (referenced.Count > 0 && _ownership is not null)
        {
            var allOwned = true;
            foreach (var table in referenced)
            {
                if (!await _ownership.IsAuthorizedApproverForTableAsync(table, actor.Sid, ct).ConfigureAwait(false))
                {
                    allOwned = false;
                    break;
                }
            }

            if (allOwned)
            {
                return;
            }
        }

        throw new UnauthorizedAccessException(
            "A SQL virtual filter needs GovernanceAdmin or ownership of every table it references.");
    }

    public async Task DeleteFilterAsync(TenantId tenantId, string name, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The virtual filter '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"virtual filter '{name}'");

        var user = snapshot.Profiles.FirstOrDefault(p => p.TenantId == tenantId && p.Bindings.Any(b => b.FilterName == name));
        if (user != null)
        {
            throw new VirtualFilterConflictException($"The virtual filter '{name}' is still bound in the profile '{user.Name}'.");
        }

        if (snapshot.Filters.Any(f => f.TenantId == tenantId && f.Supersedes.Contains(name)))
        {
            throw new VirtualFilterConflictException($"The virtual filter '{name}' is superseded by another filter.");
        }

        await ApplyAsync(new VirtualFilterChangeSet { DeleteFilters = [(tenantId, name)] }, snapshot.Generation, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_DELETED", $"virtual_filter:{name}", new { name, before = existing.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }
}
