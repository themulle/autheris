namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public sealed partial class VirtualFilterAdministrationService
{
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

        // Binding options are validated for every profile of a plan or apply (the same rules as a single profile write).
        await VirtualFilterBindingValidator.ValidateAsync(request.Profiles, _federation, _catalog, request.ExplicitKeyBudgets, ct).ConfigureAwait(false);

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
        }, snapshot.Generation, ct).ConfigureAwait(false);

        await AuditAsync(request.TenantId, actor, "VIRTUAL_FILTER_SYNC_APPLIED", "virtual_filter:*",
            new { path = request.ManagedBy.Path, commit = request.ManagedBy.Commit, force, plan }, ct).ConfigureAwait(false);

        // SR15-15: Record relaxations in the sliding window history
        if (plan.TotalRelaxations > 0)
        {
            var historyList = _removalHistory.GetOrAdd(request.TenantId, _ => new List<(DateTimeOffset, int)>());
            lock (historyList)
            {
                historyList.Add((DateTimeOffset.UtcNow, plan.TotalRelaxations));
            }
        }

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

        // SR15-15: Removing bindings, weakening uncovered policies (Deny -> Skip), narrowing scopes,
        // or deleting profiles/filters widens what grantees see: count them for the safety check across a sliding time window.
        static IEnumerable<string> Keys(VirtualFilterAccessProfile p) => p.Bindings.Select(b => p.Name + "|" + b.FilterName + "|" + b.TargetPattern);
        var existingKeys = managedProfiles.Values.SelectMany(Keys).ToHashSet(StringComparer.Ordinal);
        var desiredKeys = desiredProfiles.SelectMany(Keys).ToHashSet(StringComparer.Ordinal);
        int removedBindings = existingKeys.Count(k => !desiredKeys.Contains(k));

        int uncoveredWeakened = 0;
        int scopeChanged = 0;
        foreach (var desiredProfile in desiredProfiles)
        {
            if (managedProfiles.TryGetValue(desiredProfile.Name, out var existingProf))
            {
                if (existingProf.Uncovered == UncoveredPolicy.Deny && desiredProfile.Uncovered == UncoveredPolicy.Skip)
                {
                    uncoveredWeakened++;
                }

                if (!string.Equals(existingProf.Scope, desiredProfile.Scope, StringComparison.OrdinalIgnoreCase))
                {
                    scopeChanged++;
                }
            }
        }

        // VF-10: a changed definition of a managed filter (weaker conditions, removed join) can widen what grantees see as much as a
        // removed binding, so every updated filter counts as a relaxation as well.
        int planRelaxations = removedBindings + uncoveredWeakened + scopeChanged + deleteProfiles.Count + deleteFilters.Count + updateFilters.Count;

        var windowStart = DateTimeOffset.UtcNow.AddMinutes(-Math.Max(1, _options.MaxRemovalsWindowMinutes));
        int pastRelaxations = 0;
        if (_removalHistory.TryGetValue(tenant, out var history))
        {
            lock (history)
            {
                history.RemoveAll(entry => entry.Timestamp < windowStart);
                pastRelaxations = history.Sum(entry => entry.Count);
            }
        }

        int totalRelaxationsInWindow = pastRelaxations + planRelaxations;
        // The "no bindings left at all" rule keeps its pre-VF-10 meaning: a pure filter-definition update (counted above for the
        // window limit) must not demand force just because the desired state happens to carry no bindings.
        bool removesAnything = planRelaxations - updateFilters.Count > 0;
        bool requiresForce = planRelaxations > 0 &&
            ((_options.MaxRemovals > 0 && totalRelaxationsInWindow > _options.MaxRemovals) || (desiredKeys.Count == 0 && removesAnything));

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
            removedBindings, requiresForce, planRelaxations);
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
}
