namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;

/// <summary>Who changes virtual filters; <see cref="IsSync"/> is the file repository sync (the only writer of managed rows).</summary>
public sealed record VirtualFilterActor(Sid Sid, bool IsSync);

/// <summary>A row managed by the file repository was changed outside the sync (HTTP 409).</summary>
public sealed class ManagedResourceLockedException(string message) : InvalidOperationException(message);

/// <summary>The change conflicts with existing rows or needs <c>force</c> (HTTP 409).</summary>
public sealed class VirtualFilterConflictException(string message) : InvalidOperationException(message);

/// <summary>Desired state of one tenant from the file repository.</summary>
public sealed record VirtualFilterSyncRequest(
    TenantId TenantId,
    ManagedBy ManagedBy,
    IReadOnlyList<VirtualFilter> Filters,
    IReadOnlyList<AccessProfile> Profiles);

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
    bool RequiresForce)
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
public sealed class VirtualFilterAdministrationService
{
    private readonly IVirtualFilterRepository _repository;
    private readonly IAuditLogRepository _audit;
    private readonly VirtualFilterOptions _options;
    private readonly IVirtualFilterSnapshotProvider? _snapshots;
    private readonly ITableMetadataRepository? _catalog;

    public VirtualFilterAdministrationService(
        IVirtualFilterRepository repository,
        IAuditLogRepository audit,
        IOptions<GatewayOptions>? options = null,
        IVirtualFilterSnapshotProvider? snapshots = null,
        ITableMetadataRepository? catalog = null)
    {
        _catalog = catalog;
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options?.Value?.VirtualFilters ?? new VirtualFilterOptions();
        _snapshots = snapshots;
    }

    /// <summary>Writes and makes the change visible to enforcement on this instance at once.</summary>
    private async Task ApplyAsync(VirtualFilterChangeSet changes, CancellationToken ct)
    {
        await _repository.ApplyAsync(changes, ct).ConfigureAwait(false);
        _snapshots?.Invalidate();
    }

    public Task<VirtualFilterSnapshot> GetSnapshotAsync(CancellationToken ct = default) => _repository.LoadSnapshotAsync(ct);

    /// <summary>Phase 7: a sql definition is checked against the catalog and gets its derived target columns.</summary>
    private async Task<VirtualFilter> ValidateDefinitionAsync(VirtualFilter filter, CancellationToken ct)
    {
        filter.Validate();
        if (filter.Sql == null)
        {
            return filter;
        }

        var catalog = _catalog ?? throw new ArgumentException("Virtual filters with a sql definition need the catalog to be validated.", nameof(filter));
        return SqlFilterCompiler.Validate(filter, await catalog.GetAllTablesAsync(ct).ConfigureAwait(false));
    }

    public async Task<VirtualFilter> SaveFilterAsync(VirtualFilter filter, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(actor);
        filter = await ValidateDefinitionAsync(filter, ct).ConfigureAwait(false);

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, filter.TenantId, filter.Name);
        EnsureWritable(existing?.ManagedBy, filter.ManagedBy, actor, $"virtual filter '{filter.Name}'");
        ValidateSupersedes(snapshot.Filters.Where(f => f.TenantId == filter.TenantId && f.Name != filter.Name).Append(filter).ToList());

        var status = (_options.RequireApproval && !actor.IsSync)
            ? FilterApprovalStatus.PendingApproval
            : FilterApprovalStatus.Active;

        var toStore = filter with
        {
            Id = existing?.Id ?? filter.Id,
            Status = status,
            CreatedBy = existing?.CreatedBy ?? actor.Sid,
            ApprovedBy = status == FilterApprovalStatus.Active ? (Sid?)(actor.IsSync ? actor.Sid : (existing?.ApprovedBy ?? actor.Sid)) : null,
            ApprovedAt = status == FilterApprovalStatus.Active ? (existing?.ApprovedAt ?? DateTimeOffset.UtcNow) : null,
            UpdatedBy = actor.Sid.Value,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [toStore] }, ct).ConfigureAwait(false);
        var action = status == FilterApprovalStatus.PendingApproval
            ? "VIRTUAL_FILTER_PENDING_APPROVAL"
            : (existing == null ? "VIRTUAL_FILTER_CREATED" : "VIRTUAL_FILTER_UPDATED");
        await AuditAsync(filter.TenantId, actor, action,
            $"virtual_filter:{filter.Name}", new { name = filter.Name, status = status.ToString(), before = existing?.ComputeDefinitionHash(), after = filter.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
        return toStore;
    }

    public async Task<VirtualFilter> ApproveFilterAsync(TenantId tenantId, string name, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The virtual filter '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"virtual filter '{name}'");

        if (existing.CreatedBy == actor.Sid || string.Equals(existing.UpdatedBy, actor.Sid.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Four-eyes principle violation: Creator cannot approve their own rule.");
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

        await ApplyAsync(new VirtualFilterChangeSet { DeleteFilters = [(tenantId, name)] }, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_DELETED", $"virtual_filter:{name}", new { name, before = existing.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }

    public async Task<AccessProfile> SaveProfileAsync(AccessProfile profile, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(actor);
        profile.Validate();

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        EnsureFiltersExist(profile, snapshot.Filters.Where(f => f.TenantId == profile.TenantId).Select(f => f.Name));
        var existing = FindProfile(snapshot, profile.TenantId, profile.Name);
        EnsureWritable(existing?.ManagedBy, profile.ManagedBy, actor, $"access profile '{profile.Name}'");

        var status = (_options.RequireApproval && !actor.IsSync)
            ? FilterApprovalStatus.PendingApproval
            : FilterApprovalStatus.Active;

        var toStore = profile with
        {
            Id = existing?.Id ?? profile.Id,
            Status = status,
            CreatedBy = existing?.CreatedBy ?? actor.Sid,
            ApprovedBy = status == FilterApprovalStatus.Active ? (Sid?)(actor.IsSync ? actor.Sid : (existing?.ApprovedBy ?? actor.Sid)) : null,
            ApprovedAt = status == FilterApprovalStatus.Active ? (existing?.ApprovedAt ?? DateTimeOffset.UtcNow) : null,
            UpdatedBy = actor.Sid.Value,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [toStore] }, ct).ConfigureAwait(false);
        var action = status == FilterApprovalStatus.PendingApproval
            ? "ACCESS_PROFILE_PENDING_APPROVAL"
            : (existing == null ? "ACCESS_PROFILE_CREATED" : "ACCESS_PROFILE_UPDATED");
        await AuditAsync(profile.TenantId, actor, action,
            $"access_profile:{profile.Name}", new { name = profile.Name, status = status.ToString(), before = existing?.ComputeDefinitionHash(), after = profile.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
        return toStore;
    }

    public async Task<AccessProfile> ApproveProfileAsync(TenantId tenantId, string name, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindProfile(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The access profile '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"access profile '{name}'");

        if (existing.CreatedBy == actor.Sid || string.Equals(existing.UpdatedBy, actor.Sid.Value, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Four-eyes principle violation: Creator cannot approve their own rule.");
        }

        var approved = existing with
        {
            Status = FilterApprovalStatus.Active,
            ApprovedBy = actor.Sid,
            ApprovedAt = DateTimeOffset.UtcNow
        };

        await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [approved] }, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "ACCESS_PROFILE_APPROVED",
            $"access_profile:{name}", new { name, approved_by = actor.Sid.Value }, ct).ConfigureAwait(false);
        return approved;
    }

    public async Task DeleteProfileAsync(TenantId tenantId, string name, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindProfile(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The access profile '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"access profile '{name}'");

        await ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(tenantId, name)] }, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "ACCESS_PROFILE_DELETED", $"access_profile:{name}", new { name, before = existing.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ sync from the file repository

    public async Task<VirtualFilterSyncPlan> PlanSyncAsync(VirtualFilterSyncRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        return BuildPlan(await ValidateSqlDefinitionsAsync(request, ct).ConfigureAwait(false), snapshot, out _);
    }

    private async Task<VirtualFilterSyncRequest> ValidateSqlDefinitionsAsync(VirtualFilterSyncRequest request, CancellationToken ct)
    {
        var validated = new List<VirtualFilter>(request.Filters.Count);
        foreach (var filter in request.Filters)
        {
            validated.Add(await ValidateDefinitionAsync(filter with { TenantId = request.TenantId }, ct).ConfigureAwait(false));
        }

        return request with { Filters = validated };
    }

    /// <summary>
    /// Validates everything first, then applies the differences in one transaction (all or nothing). A plan that
    /// removes more than <see cref="VirtualFilterOptions.MaxRemovals"/> bindings, or all of them, needs <paramref name="force"/>.
    /// </summary>
    public async Task<VirtualFilterSyncPlan> ApplySyncAsync(VirtualFilterSyncRequest request, VirtualFilterActor actor, bool force = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsSync)
        {
            throw new ManagedResourceLockedException("Only the virtual filter sync may apply the file repository state.");
        }

        request = await ValidateSqlDefinitionsAsync(request, ct).ConfigureAwait(false);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var plan = BuildPlan(request, snapshot, out var changes);
        if (plan.RequiresForce && !force)
        {
            throw new VirtualFilterConflictException(
                $"The sync would remove {plan.RemovedBindings} binding(s) (limit {_options.MaxRemovals}, or all bindings); apply it with force.");
        }

        var stamp = DateTimeOffset.UtcNow;
        await ApplyAsync(changes with
        {
            SaveFilters = changes.SaveFilters.Select(f => f with { UpdatedBy = actor.Sid.Value, UpdatedAt = stamp }).ToList(),
            SaveProfiles = changes.SaveProfiles.Select(p => p with { UpdatedBy = actor.Sid.Value, UpdatedAt = stamp }).ToList()
        }, ct).ConfigureAwait(false);

        await AuditAsync(request.TenantId, actor, "VIRTUAL_FILTER_SYNC_APPLIED", "virtual_filter:*",
            new { path = request.ManagedBy.Path, commit = request.ManagedBy.Commit, force, plan }, ct).ConfigureAwait(false);
        return plan;
    }

    private VirtualFilterSyncPlan BuildPlan(VirtualFilterSyncRequest request, VirtualFilterSnapshot snapshot, out VirtualFilterChangeSet changes)
    {
        var tenant = request.TenantId;
        var desiredFilters = request.Filters.Select(f => f with { TenantId = tenant, ManagedBy = request.ManagedBy }).ToList();
        var desiredProfiles = request.Profiles.Select(p => p with { TenantId = tenant, ManagedBy = request.ManagedBy }).ToList();
        foreach (var filter in desiredFilters) filter.Validate();
        foreach (var profile in desiredProfiles) profile.Validate();
        EnsureUnique(desiredFilters.Select(f => f.Name), "virtual filter");
        EnsureUnique(desiredProfiles.Select(p => p.Name), "access profile");

        var existingFilters = snapshot.Filters.Where(f => f.TenantId == tenant).ToList();
        var existingProfiles = snapshot.Profiles.Where(p => p.TenantId == tenant).ToList();
        var unmanagedFilters = existingFilters.Where(f => f.ManagedBy == null).ToList();
        var unmanagedProfiles = existingProfiles.Where(p => p.ManagedBy == null).ToList();

        var clash = desiredFilters.Select(f => f.Name).Intersect(unmanagedFilters.Select(f => f.Name)).FirstOrDefault()
                    ?? desiredProfiles.Select(p => p.Name).Intersect(unmanagedProfiles.Select(p => p.Name)).FirstOrDefault();
        if (clash != null)
        {
            throw new VirtualFilterConflictException($"'{clash}' exists and is not managed by the file repository.");
        }

        ValidateSupersedes(unmanagedFilters.Concat(desiredFilters).ToList());
        var availableFilters = unmanagedFilters.Concat(desiredFilters).Select(f => f.Name).ToList();
        foreach (var profile in desiredProfiles)
        {
            EnsureFiltersExist(profile, availableFilters);
        }

        var managedFilters = existingFilters.Where(f => f.ManagedBy != null).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var managedProfiles = existingProfiles.Where(p => p.ManagedBy != null).ToDictionary(p => p.Name, StringComparer.Ordinal);

        var (createFilters, updateFilters, saveFilters) = Diff(desiredFilters, managedFilters, f => f.Name, f => f.ComputeDefinitionHash(), f => f.StoredDefinitionHash, (f, id) => f with { Id = id }, f => f.Id);
        var (createProfiles, updateProfiles, saveProfiles) = Diff(desiredProfiles, managedProfiles, p => p.Name, p => p.ComputeDefinitionHash(), p => p.StoredDefinitionHash, (p, id) => p with { Id = id }, p => p.Id);

        var desiredFilterNames = desiredFilters.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var desiredProfileNames = desiredProfiles.Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var deleteFilters = managedFilters.Keys.Where(n => !desiredFilterNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var deleteProfiles = managedProfiles.Keys.Where(n => !desiredProfileNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var stillBound = unmanagedProfiles.SelectMany(p => p.Bindings.Select(b => (Profile: p.Name, b.FilterName)))
            .FirstOrDefault(x => deleteFilters.Contains(x.FilterName));
        if (stillBound != default)
        {
            throw new VirtualFilterConflictException($"The virtual filter '{stillBound.FilterName}' would be removed but is bound in the unmanaged profile '{stillBound.Profile}'.");
        }

        // Removing a binding widens what its grantee sees: count them for the safety check.
        static IEnumerable<string> Keys(AccessProfile p) => p.Bindings.Select(b => p.Name + "|" + b.FilterName + "|" + b.TargetPattern);
        var existingKeys = managedProfiles.Values.SelectMany(Keys).ToHashSet(StringComparer.Ordinal);
        var desiredKeys = desiredProfiles.SelectMany(Keys).ToHashSet(StringComparer.Ordinal);
        int removed = existingKeys.Count(k => !desiredKeys.Contains(k));
        bool requiresForce = removed > 0 && ((_options.MaxRemovals > 0 && removed > _options.MaxRemovals) || desiredKeys.Count == 0);

        changes = new VirtualFilterChangeSet
        {
            SaveFilters = saveFilters,
            SaveProfiles = saveProfiles,
            DeleteProfiles = deleteProfiles.Select(n => (tenant, n)).ToList(),
            DeleteFilters = deleteFilters.Select(n => (tenant, n)).ToList()
        };

        return new VirtualFilterSyncPlan(
            createFilters, updateFilters, deleteFilters, Drifted(managedFilters.Values, f => f.Name, f => f.ComputeDefinitionHash(), f => f.StoredDefinitionHash),
            createProfiles, updateProfiles, deleteProfiles, Drifted(managedProfiles.Values, p => p.Name, p => p.ComputeDefinitionHash(), p => p.StoredDefinitionHash),
            removed, requiresForce);
    }

    private static (List<string> Create, List<string> Update, List<T> Save) Diff<T>(
        IEnumerable<T> desired,
        IReadOnlyDictionary<string, T> managed,
        Func<T, string> name,
        Func<T, string> hash,
        Func<T, string?> storedHash,
        Func<T, Guid, T> withId,
        Func<T, Guid> id)
    {
        var create = new List<string>();
        var update = new List<string>();
        var save = new List<T>();
        foreach (var item in desired)
        {
            if (!managed.TryGetValue(name(item), out var current))
            {
                create.Add(name(item));
                save.Add(item);
            }
            else if (storedHash(current) != hash(item))
            {
                // also repairs drift: the stored hash no longer matches the stored definition
                update.Add(name(item));
                save.Add(withId(item, id(current)));
            }
        }

        return (create, update, save);
    }

    private static List<string> Drifted<T>(IEnumerable<T> managed, Func<T, string> name, Func<T, string> hash, Func<T, string?> storedHash) =>
        managed.Where(m => storedHash(m) != hash(m)).Select(name).OrderBy(n => n, StringComparer.Ordinal).ToList();

    // ------------------------------------------------------------------ helpers

    private static VirtualFilter? FindFilter(VirtualFilterSnapshot snapshot, TenantId tenantId, string name) =>
        snapshot.Filters.FirstOrDefault(f => f.TenantId == tenantId && string.Equals(f.Name, name, StringComparison.Ordinal));

    private static AccessProfile? FindProfile(VirtualFilterSnapshot snapshot, TenantId tenantId, string name) =>
        snapshot.Profiles.FirstOrDefault(p => p.TenantId == tenantId && string.Equals(p.Name, name, StringComparison.Ordinal));

    private static void EnsureFiltersExist(AccessProfile profile, IEnumerable<string> filterNames)
    {
        var known = filterNames.ToHashSet(StringComparer.Ordinal);
        var unknown = profile.Bindings.FirstOrDefault(b => !known.Contains(b.FilterName));
        if (unknown != null)
        {
            throw new ArgumentException($"The profile '{profile.Name}' binds the unknown virtual filter '{unknown.FilterName}'.", nameof(profile));
        }
    }

    private static void EnsureUnique(IEnumerable<string> names, string what)
    {
        var duplicate = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
        {
            throw new ArgumentException($"The {what} '{duplicate.Key}' is defined more than once.", nameof(names));
        }
    }

    private static void EnsureWritable(ManagedBy? stored, ManagedBy? incoming, VirtualFilterActor actor, string what)
    {
        if (actor.IsSync)
        {
            return;
        }

        if (stored != null)
        {
            throw new ManagedResourceLockedException($"The {what} is managed by '{stored.Path}' and only changes through the sync.");
        }

        if (incoming != null)
        {
            throw new ManagedResourceLockedException($"Only the sync may create the managed {what}.");
        }
    }

    /// <summary>Every superseded filter exists in the tenant and the supersedes graph has no cycle.</summary>
    private static void ValidateSupersedes(IReadOnlyList<VirtualFilter> tenantFilters)
    {
        var byName = tenantFilters.ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var filter in tenantFilters)
        {
            foreach (var superseded in filter.Supersedes.Where(s => !byName.ContainsKey(s)))
            {
                throw new ArgumentException($"The virtual filter '{filter.Name}' supersedes the unknown filter '{superseded}'.", nameof(tenantFilters));
            }
        }

        var state = new Dictionary<string, int>(StringComparer.Ordinal); // 1 = visiting, 2 = done
        foreach (var filter in tenantFilters)
        {
            Visit(filter.Name, []);
        }

        void Visit(string name, List<string> path)
        {
            if (state.TryGetValue(name, out var s))
            {
                if (s == 1)
                {
                    throw new ArgumentException($"The supersedes relation has a cycle: {string.Join(" -> ", path.Append(name))}.", nameof(tenantFilters));
                }

                return;
            }

            state[name] = 1;
            path.Add(name);
            foreach (var next in byName[name].Supersedes)
            {
                Visit(next, path);
            }

            path.RemoveAt(path.Count - 1);
            state[name] = 2;
        }
    }

    private Task AuditAsync(TenantId tenantId, VirtualFilterActor actor, string eventType, string target, object details, CancellationToken ct) =>
        _audit.RecordAuditEventAsync(new AuditLogEntry
        {
            TenantId = tenantId,
            EventType = eventType,
            ActorSid = actor.Sid,
            TargetTable = target,
            Decision = "ALLOW",
            TraceId = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? string.Empty,
            DetailsJson = JsonSerializer.Serialize(details)
        }, ct);
}
