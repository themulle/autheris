namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

/// <summary>Who changes virtual filters; <see cref="IsSync"/> is the file repository sync (the only writer of managed rows).</summary>
public sealed record VirtualFilterActor(Sid Sid, bool IsSync, IReadOnlyCollection<string>? Identifiers = null)
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
    private readonly SqlDataSourceOptions? _dataSources;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<TenantId, List<(DateTimeOffset Timestamp, int Count)>> _removalHistory = new();

    public VirtualFilterAdministrationService(
        IVirtualFilterRepository repository,
        IAuditLogRepository audit,
        IOptions<GatewayOptions>? options = null,
        IVirtualFilterSnapshotProvider? snapshots = null,
        ITableMetadataRepository? catalog = null)
    {
        _catalog = catalog;
        _dataSources = options?.Value?.DataSources;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options?.Value?.VirtualFilters ?? new VirtualFilterOptions();
        _federation = _options.Federation ?? new VirtualFilterFederationOptions();
        _snapshots = snapshots;
    }

    public VirtualFilterOptions Options => _options;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _processedWebhookDeliveries = new(StringComparer.Ordinal);
    private DateTimeOffset? _lastWebhookTriggerAt;
    private string? _lastWebhookDeliveryId;

    public DateTimeOffset? LastWebhookTriggerAt => _lastWebhookTriggerAt;
    public string? LastWebhookDeliveryId => _lastWebhookDeliveryId;

    public bool RecordWebhookTrigger(string deliveryId, DateTimeOffset timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deliveryId);

        // Prune entries older than 1 hour to prevent unbounded memory growth
        if (_processedWebhookDeliveries.Count > 100)
        {
            var cutoff = DateTimeOffset.UtcNow.AddHours(-1);
            foreach (var kvp in _processedWebhookDeliveries)
            {
                if (kvp.Value < cutoff)
                {
                    _processedWebhookDeliveries.TryRemove(kvp.Key, out _);
                }
            }
        }

        if (!_processedWebhookDeliveries.TryAdd(deliveryId, timestamp))
        {
            return false;
        }

        _lastWebhookDeliveryId = deliveryId;
        _lastWebhookTriggerAt = timestamp;
        return true;
    }

    /// <summary>Writes and makes the change visible to enforcement on this instance at once.</summary>
    private async Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct)
    {
        await _repository.ApplyAsync(changes, ct).ConfigureAwait(false);
        _snapshots?.Invalidate();
    }

    public Task<VirtualFilterSnapshot> GetSnapshotAsync(CancellationToken ct = default) => _repository.LoadSnapshotAsync(ct);

    public async Task<VirtualFilter> SaveFilterAsync(VirtualFilter filter, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(actor);
        filter = await ValidateDefinitionAsync(filter, ct).ConfigureAwait(false);

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, filter.TenantId, filter.Name);
        EnsureWritable(existing?.ManagedBy, filter.ManagedBy, actor, $"virtual filter '{filter.Name}'");
        ValidateSupersedes(snapshot.Filters.Where(f => f.TenantId == filter.TenantId && f.Name != filter.Name).Append(filter).ToList());

        if (_options.RequireApproval && !actor.IsSync)
        {
            if (existing != null && existing.Status == FilterApprovalStatus.Active)
            {
                // Last-Known-Good: Active filter stays active, changes go to Draft
                var draft = filter with
                {
                    Id = Guid.NewGuid(),
                    Status = FilterApprovalStatus.PendingApproval,
                    CreatedBy = actor.Sid,
                    ApprovedBy = null,
                    ApprovedAt = null,
                    UpdatedBy = CombineUpdatedBy(existing.Draft?.UpdatedBy, actor),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Draft = null,
                    PendingDeletion = false,
                    DeletionRequestedBy = null
                };

                var toStore = existing with
                {
                    Draft = draft,
                    UpdatedBy = CombineUpdatedBy(existing.UpdatedBy, actor),
                    UpdatedAt = DateTimeOffset.UtcNow
                };

                await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [toStore] }, ct).ConfigureAwait(false);
                await AuditAsync(filter.TenantId, actor, "VIRTUAL_FILTER_PENDING_APPROVAL",
                    $"virtual_filter:{filter.Name}", new { name = filter.Name, status = "DraftPendingApproval", before = existing.ComputeDefinitionHash(), draft = draft.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
                return toStore;
            }

            // Brand new filter (or existing was PendingApproval)
            var pending = filter with
            {
                Id = existing?.Id ?? filter.Id,
                Status = FilterApprovalStatus.PendingApproval,
                CreatedBy = existing?.CreatedBy ?? actor.Sid,
                ApprovedBy = null,
                ApprovedAt = null,
                UpdatedBy = CombineUpdatedBy(existing?.UpdatedBy, actor),
                UpdatedAt = DateTimeOffset.UtcNow,
                Draft = null,
                PendingDeletion = false,
                DeletionRequestedBy = null
            };

            await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [pending] }, ct).ConfigureAwait(false);
            var action = existing == null ? "VIRTUAL_FILTER_CREATED" : "VIRTUAL_FILTER_UPDATED";
            await AuditAsync(filter.TenantId, actor, "VIRTUAL_FILTER_PENDING_APPROVAL",
                $"virtual_filter:{filter.Name}", new { name = filter.Name, status = pending.Status.ToString(), before = existing?.ComputeDefinitionHash(), after = filter.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
            return pending;
        }

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
            DeletionRequestedBy = null
        };

        await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [direct] }, ct).ConfigureAwait(false);
        var evt = existing == null ? "VIRTUAL_FILTER_CREATED" : "VIRTUAL_FILTER_UPDATED";
        await AuditAsync(filter.TenantId, actor, evt,
            $"virtual_filter:{filter.Name}", new { name = filter.Name, status = direct.Status.ToString(), before = existing?.ComputeDefinitionHash(), after = filter.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
        return direct;
    }

    public Task<VirtualFilter> ApproveFilterAsync(TenantId tenantId, string name, VirtualFilterActor actor, CancellationToken ct) =>
        ApproveFilterAsync(tenantId, name, actor, expectedHash: null, ct);

    public async Task<VirtualFilter> ApproveFilterAsync(TenantId tenantId, string name, VirtualFilterActor actor, string? expectedHash = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The virtual filter '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"virtual filter '{name}'");

        bool hasPending = existing.Status == FilterApprovalStatus.PendingApproval ||
                          existing.Draft != null ||
                          existing.PendingDeletion;
        if (!hasPending)
        {
            throw new InvalidOperationException($"The virtual filter '{name}' has no pending changes to approve.");
        }

        var contributors = GetContributorIdentifiers(existing);
        if (actor.MatchesAny(contributors))
        {
            throw new InvalidOperationException("Four-eyes principle violation: Creator or editor cannot approve their own rule.");
        }

        string pendingHash = existing.PendingDeletion
            ? existing.ComputeDefinitionHash()
            : (existing.Draft?.ComputeDefinitionHash() ?? existing.ComputeDefinitionHash());

        if (expectedHash != null && !string.Equals(expectedHash, pendingHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new VirtualFilterConflictException($"Hash mismatch for virtual filter '{name}': expected '{expectedHash}' but pending definition hash is '{pendingHash}'.");
        }

        if (existing.PendingDeletion)
        {
            await ApplyAsync(new VirtualFilterChangeSet { DeleteFilters = [(tenantId, name)] }, ct).ConfigureAwait(false);
            await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_DELETED", $"virtual_filter:{name}", new { name, before = existing.ComputeDefinitionHash(), approved_by = actor.Sid.Value }, ct).ConfigureAwait(false);
            return existing with { Status = FilterApprovalStatus.Rejected, PendingDeletion = false };
        }

        if (existing.Draft != null)
        {
            var promoted = existing.Draft with
            {
                Id = existing.Id,
                Status = FilterApprovalStatus.Active,
                ApprovedBy = actor.Sid,
                ApprovedAt = DateTimeOffset.UtcNow,
                Draft = null,
                PendingDeletion = false,
                DeletionRequestedBy = null
            };
            await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [promoted] }, ct).ConfigureAwait(false);
            await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_APPROVED", $"virtual_filter:{name}", new { name, approved_by = actor.Sid.Value }, ct).ConfigureAwait(false);
            return promoted;
        }

        var approved = existing with
        {
            Status = FilterApprovalStatus.Active,
            ApprovedBy = actor.Sid,
            ApprovedAt = DateTimeOffset.UtcNow
        };

        await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [approved] }, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_APPROVED",
            $"virtual_filter:{name}", new { name, approved_by = actor.Sid.Value }, ct).ConfigureAwait(false);
        return approved;
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

        if (_options.RequireApproval && existing.Status == FilterApprovalStatus.Active)
        {
            var pendingDeletion = existing with
            {
                PendingDeletion = true,
                DeletionRequestedBy = actor.Sid,
                UpdatedBy = actor.Sid.Value,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [pendingDeletion] }, ct).ConfigureAwait(false);
            await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_PENDING_DELETION", $"virtual_filter:{name}", new { name }, ct).ConfigureAwait(false);
            return;
        }

        await ApplyAsync(new VirtualFilterChangeSet { DeleteFilters = [(tenantId, name)] }, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_DELETED", $"virtual_filter:{name}", new { name, before = existing.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }
}
