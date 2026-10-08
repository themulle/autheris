namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Who changes virtual filters; <see cref="IsSync"/> is the file repository sync (the only writer of managed rows).</summary>
public sealed record VirtualFilterActor(Sid Sid, bool IsSync);

/// <summary>A row managed by the file repository was changed outside the sync (HTTP 409).</summary>
public sealed class ManagedResourceLockedException(string message) : InvalidOperationException(message);

/// <summary>The change conflicts with existing rows (HTTP 409).</summary>
public sealed class VirtualFilterConflictException(string message) : InvalidOperationException(message);

/// <summary>Desired state of one tenant from the file repository.</summary>
public sealed record VirtualFilterSyncRequest(
    TenantId TenantId,
    ManagedBy ManagedBy,
    IReadOnlyList<VirtualFilter> Filters,
    IReadOnlyList<FilterBinding> Bindings);

/// <summary>Differences between the file repository and Autheris; drift = a stored row changed outside Autheris.</summary>
public sealed record VirtualFilterSyncPlan(
    IReadOnlyList<string> CreateFilters,
    IReadOnlyList<string> UpdateFilters,
    IReadOnlyList<string> DeleteFilters,
    IReadOnlyList<string> DriftedFilters,
    IReadOnlyList<string> CreateBindings,
    IReadOnlyList<string> UpdateBindings,
    IReadOnlyList<string> DeleteBindings,
    IReadOnlyList<string> DriftedBindings)
{
    public bool HasChanges =>
        CreateFilters.Count + UpdateFilters.Count + DeleteFilters.Count +
        CreateBindings.Count + UpdateBindings.Count + DeleteBindings.Count > 0;
}

/// <summary>
/// Virtual filters: the rules for changing filters and bindings. Every change is validated, audited and
/// increments the generation (through the repository). Rows managed by the file repository (Talos) only change
/// through <see cref="ApplySyncAsync"/>.
/// </summary>
public sealed class VirtualFilterAdministrationService
{
    private readonly IVirtualFilterRepository _repository;
    private readonly IAuditLogRepository _audit;

    public VirtualFilterAdministrationService(IVirtualFilterRepository repository, IAuditLogRepository audit)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<VirtualFilterSnapshot> GetSnapshotAsync(CancellationToken ct = default) => _repository.LoadSnapshotAsync(ct);

    public async Task<VirtualFilter> SaveFilterAsync(VirtualFilter filter, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(actor);
        filter.Validate();

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, filter.TenantId, filter.Name);
        EnsureWritable(existing?.ManagedBy, filter.ManagedBy, actor, $"virtual filter '{filter.Name}'");

        var tenantFilters = snapshot.Filters.Where(f => f.TenantId == filter.TenantId && f.Name != filter.Name).Append(filter).ToList();
        ValidateSupersedes(tenantFilters);

        var toStore = filter with { Id = existing?.Id ?? filter.Id, UpdatedBy = actor.Sid.Value, UpdatedAt = DateTimeOffset.UtcNow };
        await _repository.SaveFilterAsync(toStore, ct).ConfigureAwait(false);
        await AuditAsync(filter.TenantId, actor, existing == null ? "VIRTUAL_FILTER_CREATED" : "VIRTUAL_FILTER_UPDATED",
            $"virtual_filter:{filter.Name}", new { name = filter.Name, before = existing?.ComputeDefinitionHash(), after = filter.ComputeDefinitionHash(), managed_by = filter.ManagedBy }, ct).ConfigureAwait(false);
        return toStore;
    }

    public async Task DeleteFilterAsync(TenantId tenantId, string name, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindFilter(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The virtual filter '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"virtual filter '{name}'");

        if (snapshot.Bindings.Any(b => b.TenantId == tenantId && b.FilterName == name))
        {
            throw new VirtualFilterConflictException($"The virtual filter '{name}' still has bindings.");
        }

        if (snapshot.Filters.Any(f => f.TenantId == tenantId && f.Supersedes.Contains(name)))
        {
            throw new VirtualFilterConflictException($"The virtual filter '{name}' is superseded by another filter.");
        }

        await _repository.DeleteFilterAsync(tenantId, name, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "VIRTUAL_FILTER_DELETED", $"virtual_filter:{name}",
            new { name, before = existing.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }

    public async Task<FilterBinding> SaveBindingAsync(FilterBinding binding, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(actor);
        binding.Validate();

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        if (FindFilter(snapshot, binding.TenantId, binding.FilterName) == null)
        {
            throw new ArgumentException($"The binding refers to the unknown virtual filter '{binding.FilterName}'.", nameof(binding));
        }

        var existing = snapshot.Bindings.FirstOrDefault(b => b.Id == binding.Id && b.TenantId == binding.TenantId);
        EnsureWritable(existing?.ManagedBy, binding.ManagedBy, actor, $"filter binding '{binding.Id}'");

        var toStore = binding with { UpdatedBy = actor.Sid.Value, UpdatedAt = DateTimeOffset.UtcNow };
        await _repository.SaveBindingAsync(toStore, ct).ConfigureAwait(false);
        await AuditAsync(binding.TenantId, actor, existing == null ? "FILTER_BINDING_CREATED" : "FILTER_BINDING_UPDATED",
            $"filter_binding:{binding.Id}", new { id = binding.Id, filter = binding.FilterName, target = binding.TargetPattern, before = existing?.ComputeDefinitionHash(), after = binding.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
        return toStore;
    }

    public async Task DeleteBindingAsync(TenantId tenantId, Guid id, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = snapshot.Bindings.FirstOrDefault(b => b.Id == id && b.TenantId == tenantId)
            ?? throw new KeyNotFoundException($"The filter binding '{id}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"filter binding '{id}'");

        await _repository.DeleteBindingAsync(tenantId, id, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "FILTER_BINDING_DELETED", $"filter_binding:{id}",
            new { id, filter = existing.FilterName, before = existing.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ sync from the file repository

    public async Task<VirtualFilterSyncPlan> PlanSyncAsync(VirtualFilterSyncRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        return BuildPlan(request, snapshot, out _);
    }

    public async Task<VirtualFilterSyncPlan> ApplySyncAsync(VirtualFilterSyncRequest request, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(actor);
        if (!actor.IsSync)
        {
            throw new ManagedResourceLockedException("Only the virtual filter sync may apply the file repository state.");
        }

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var plan = BuildPlan(request, snapshot, out var work);

        foreach (var filter in work.FiltersToWrite)
        {
            await _repository.SaveFilterAsync(filter with { UpdatedBy = actor.Sid.Value, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        }

        foreach (var binding in work.BindingsToWrite)
        {
            await _repository.SaveBindingAsync(binding with { UpdatedBy = actor.Sid.Value, UpdatedAt = DateTimeOffset.UtcNow }, ct).ConfigureAwait(false);
        }

        foreach (var binding in work.BindingsToDelete)
        {
            await _repository.DeleteBindingAsync(request.TenantId, binding.Id, ct).ConfigureAwait(false);
        }

        foreach (var name in plan.DeleteFilters)
        {
            await _repository.DeleteFilterAsync(request.TenantId, name, ct).ConfigureAwait(false);
        }

        await AuditAsync(request.TenantId, actor, "VIRTUAL_FILTER_SYNC_APPLIED", "virtual_filter:*", new
        {
            path = request.ManagedBy.Path,
            commit = request.ManagedBy.Commit,
            plan
        }, ct).ConfigureAwait(false);
        return plan;
    }

    private sealed record SyncWork(List<VirtualFilter> FiltersToWrite, List<FilterBinding> BindingsToWrite, List<FilterBinding> BindingsToDelete);

    private static VirtualFilterSyncPlan BuildPlan(VirtualFilterSyncRequest request, VirtualFilterSnapshot snapshot, out SyncWork work)
    {
        var tenant = request.TenantId;
        var desiredFilters = request.Filters.Select(f => f with { TenantId = tenant, ManagedBy = request.ManagedBy }).ToList();
        var desiredBindings = request.Bindings.Select(b => b with { TenantId = tenant, ManagedBy = request.ManagedBy }).ToList();
        foreach (var filter in desiredFilters) filter.Validate();
        foreach (var binding in desiredBindings) binding.Validate();

        var duplicateName = desiredFilters.GroupBy(f => f.Name).FirstOrDefault(g => g.Count() > 1);
        if (duplicateName != null)
        {
            throw new ArgumentException($"The virtual filter '{duplicateName.Key}' is defined more than once.", nameof(request));
        }

        var existingFilters = snapshot.Filters.Where(f => f.TenantId == tenant).ToList();
        var existingBindings = snapshot.Bindings.Where(b => b.TenantId == tenant).ToList();
        var unmanagedFilters = existingFilters.Where(f => f.ManagedBy == null).ToList();

        foreach (var filter in desiredFilters.Where(d => unmanagedFilters.Any(u => u.Name == d.Name)))
        {
            throw new VirtualFilterConflictException($"The virtual filter '{filter.Name}' exists and is not managed by the file repository.");
        }

        ValidateSupersedes(unmanagedFilters.Concat(desiredFilters).ToList());
        var knownNames = unmanagedFilters.Concat(desiredFilters).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var binding in desiredBindings.Where(b => !knownNames.Contains(b.FilterName)))
        {
            throw new ArgumentException($"The binding refers to the unknown virtual filter '{binding.FilterName}'.", nameof(request));
        }

        var managedFilters = existingFilters.Where(f => f.ManagedBy != null).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var create = new List<string>();
        var update = new List<string>();
        var filtersToWrite = new List<VirtualFilter>();
        foreach (var filter in desiredFilters)
        {
            if (!managedFilters.TryGetValue(filter.Name, out var current))
            {
                create.Add(filter.Name);
                filtersToWrite.Add(filter);
            }
            else if (current.StoredDefinitionHash != filter.ComputeDefinitionHash())
            {
                update.Add(filter.Name);
                filtersToWrite.Add(filter with { Id = current.Id });
            }
        }

        var desiredNames = desiredFilters.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var deleteFilters = managedFilters.Keys.Where(n => !desiredNames.Contains(n)).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var driftedFilters = managedFilters.Values.Where(f => f.StoredDefinitionHash != f.ComputeDefinitionHash()).Select(f => f.Name).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var managedBindings = existingBindings.Where(b => b.ManagedBy != null).ToList();
        var managedByKey = managedBindings.GroupBy(b => b.NaturalKey).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var createBindings = new List<string>();
        var updateBindings = new List<string>();
        var bindingsToWrite = new List<FilterBinding>();
        foreach (var binding in desiredBindings)
        {
            if (!managedByKey.TryGetValue(binding.NaturalKey, out var current))
            {
                createBindings.Add(binding.NaturalKey);
                bindingsToWrite.Add(binding);
            }
            else if (current.StoredDefinitionHash != binding.ComputeDefinitionHash())
            {
                updateBindings.Add(binding.NaturalKey);
                bindingsToWrite.Add(binding with { Id = current.Id });
            }
        }

        var desiredKeys = desiredBindings.Select(b => b.NaturalKey).ToHashSet(StringComparer.Ordinal);
        var bindingsToDelete = managedBindings.Where(b => !desiredKeys.Contains(b.NaturalKey)).ToList();
        var deleteSet = deleteFilters.ToHashSet(StringComparer.Ordinal);
        var blocking = existingBindings.FirstOrDefault(b => b.ManagedBy == null && deleteSet.Contains(b.FilterName));
        if (blocking != null)
        {
            throw new VirtualFilterConflictException($"The virtual filter '{blocking.FilterName}' would be removed but still has an unmanaged binding.");
        }

        var driftedBindings = managedBindings.Where(b => b.StoredDefinitionHash != b.ComputeDefinitionHash()).Select(b => b.NaturalKey).ToList();

        work = new SyncWork(filtersToWrite, bindingsToWrite, bindingsToDelete);
        return new VirtualFilterSyncPlan(
            create, update, deleteFilters, driftedFilters,
            createBindings, updateBindings, bindingsToDelete.Select(b => b.NaturalKey).ToList(), driftedBindings);
    }

    // ------------------------------------------------------------------ helpers

    private static VirtualFilter? FindFilter(VirtualFilterSnapshot snapshot, TenantId tenantId, string name) =>
        snapshot.Filters.FirstOrDefault(f => f.TenantId == tenantId && string.Equals(f.Name, name, StringComparison.Ordinal));

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
