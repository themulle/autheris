namespace Autheris.Application.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public sealed partial class VirtualFilterAdministrationService
{
    /// <summary>Phase 7: sql and structured definitions are checked against the catalog and allowed reference tables.</summary>
    private async Task<VirtualFilter> ValidateDefinitionAsync(VirtualFilter filter, CancellationToken ct)
    {
        filter.Validate();
        if (filter.Sql == null && filter.Structured == null)
        {
            return filter;
        }

        if (filter.Sql != null)
        {
            var catalog = _catalog ?? throw new ArgumentException("Virtual filters with a sql definition need the catalog to be validated.", nameof(filter));
            var tables = await catalog.GetAllTablesAsync(ct).ConfigureAwait(false);
            var attributes = _callerAttributes is null ? null : await _callerAttributes.GetAsync(ct).ConfigureAwait(false);
            return SqlFilterCompiler.Validate(filter, tables, _options.AllowedReferenceTables, _dataSources?.RequireTenantColumn == true, _dataSources?.TenantColumnExemptTables, attributes);
        }

        if (filter.Structured != null && _catalog != null)
        {
            var tables = await _catalog.GetAllTablesAsync(ct).ConfigureAwait(false);
            ValidateStructuredTablesAgainstCatalog(filter, tables);
        }

        return filter;
    }

    private void ValidateStructuredTablesAgainstCatalog(VirtualFilter filter, IReadOnlyList<TableMetadata> catalog)
    {
        var def = filter.Structured;
        if (def == null) return;

        var allTables = new List<TableIdentifier> { def.From };
        allTables.AddRange(def.Joins.Select(j => j.Table));

        foreach (var table in allTables)
        {
            var catalogued = catalog.FirstOrDefault(t =>
                string.Equals(t.Identifier.Domain, filter.Source, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Identifier.Schema, table.Schema, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(t.Identifier.TableName, table.TableName, StringComparison.OrdinalIgnoreCase));

            if (catalogued == null)
            {
                throw new ArgumentException($"The virtual filter '{filter.Name}' references table '{table}' which is unknown in the catalog of '{filter.Source}'.", nameof(filter));
            }

            if (_options.AllowedReferenceTables is { Count: > 0 } allowed)
            {
                bool isAllowed = allowed.Any(a =>
                    string.Equals(a, table.ToString(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, $"{table.Schema}.{table.TableName}", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, table.TableName, StringComparison.OrdinalIgnoreCase));

                if (!isAllowed)
                {
                    throw new ArgumentException($"The virtual filter '{filter.Name}' references table '{table}' which is not in the allowed reference tables list.", nameof(filter));
                }
            }
        }
    }

    public async Task<VirtualFilterAccessProfile> SaveProfileAsync(VirtualFilterAccessProfile profile, VirtualFilterActor actor, IReadOnlySet<FilterBinding>? explicitKeyBudgets = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(actor);
        profile.Validate();
        await VirtualFilterBindingValidator.ValidateAsync([profile], _federation, _catalog, explicitKeyBudgets, ct).ConfigureAwait(false);

        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        EnsureFiltersExist(profile, snapshot.Filters.Where(f => f.TenantId == profile.TenantId).Select(f => f.Name));
        var existing = FindProfile(snapshot, profile.TenantId, profile.Name);
        EnsureWritable(existing?.ManagedBy, profile.ManagedBy, actor, $"access profile '{profile.Name}'");

        if (_options.RequireApproval && !actor.IsSync)
        {
            if (existing != null && existing.Status == FilterApprovalStatus.Active)
            {
                // Last-Known-Good: Active profile stays active, changes go to Draft
                var draft = profile with
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

                await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [toStore] }, ct).ConfigureAwait(false);
                await AuditAsync(profile.TenantId, actor, "ACCESS_PROFILE_PENDING_APPROVAL",
                    $"access_profile:{profile.Name}", new { name = profile.Name, status = "DraftPendingApproval", before = existing.ComputeDefinitionHash(), draft = draft.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
                return toStore;
            }

            // Brand new profile (or existing was PendingApproval)
            var pending = profile with
            {
                Id = existing?.Id ?? profile.Id,
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

            await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [pending] }, ct).ConfigureAwait(false);
            var action = existing == null ? "ACCESS_PROFILE_CREATED" : "ACCESS_PROFILE_UPDATED";
            await AuditAsync(profile.TenantId, actor, "ACCESS_PROFILE_PENDING_APPROVAL",
                $"access_profile:{profile.Name}", new { name = profile.Name, status = pending.Status.ToString(), before = existing?.ComputeDefinitionHash(), after = profile.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
            return pending;
        }

        var direct = profile with
        {
            Id = existing?.Id ?? profile.Id,
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

        await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [direct] }, ct).ConfigureAwait(false);
        var evt = existing == null ? "ACCESS_PROFILE_CREATED" : "ACCESS_PROFILE_UPDATED";
        await AuditAsync(profile.TenantId, actor, evt,
            $"access_profile:{profile.Name}", new { name = profile.Name, status = direct.Status.ToString(), before = existing?.ComputeDefinitionHash(), after = profile.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
        return direct;
    }

    /// <summary>VF-05: the hash an approval compares (see <see cref="PendingHash(VirtualFilter)"/>).</summary>
    public static string PendingHash(VirtualFilterAccessProfile profile) =>
        profile.PendingDeletion
            ? profile.ComputeDefinitionHash()
            : (profile.Draft?.ComputeDefinitionHash() ?? profile.ComputeDefinitionHash());

    /// <summary>VF-05: approval is bound to the reviewed definition; there is no hash-less approval.</summary>
    public async Task<VirtualFilterAccessProfile> ApproveProfileAsync(TenantId tenantId, string name, VirtualFilterActor actor, string expectedHash, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHash);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindProfile(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The access profile '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"access profile '{name}'");

        bool hasPending = existing.Status == FilterApprovalStatus.PendingApproval ||
                          existing.Draft != null ||
                          existing.PendingDeletion;
        if (!hasPending)
        {
            throw new InvalidOperationException($"The access profile '{name}' has no pending changes to approve.");
        }

        var contributors = GetContributorIdentifiers(existing);
        if (actor.MatchesAny(contributors))
        {
            throw new InvalidOperationException("Four-eyes principle violation: Creator or editor cannot approve their own profile.");
        }

        string pendingHash = PendingHash(existing);

        if (!string.Equals(expectedHash, pendingHash, StringComparison.Ordinal))
        {
            throw new VirtualFilterConflictException($"Hash mismatch for access profile '{name}': expected '{expectedHash}' but pending definition hash is '{pendingHash}'.");
        }

        if (existing.PendingDeletion)
        {
            await ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(tenantId, name)] }, ct).ConfigureAwait(false);
            await AuditAsync(tenantId, actor, "ACCESS_PROFILE_DELETED", $"access_profile:{name}", new { name, before = existing.ComputeDefinitionHash(), approved_by = actor.Sid.Value }, ct).ConfigureAwait(false);
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
            await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [promoted] }, ct).ConfigureAwait(false);
            await AuditAsync(tenantId, actor, "ACCESS_PROFILE_APPROVED", $"access_profile:{name}", new { name, approved_by = actor.Sid.Value }, ct).ConfigureAwait(false);
            return promoted;
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

        if (_options.RequireApproval && existing.Status == FilterApprovalStatus.Active)
        {
            var pendingDeletion = existing with
            {
                PendingDeletion = true,
                DeletionRequestedBy = actor.Sid,
                UpdatedBy = actor.Sid.Value,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [pendingDeletion] }, ct).ConfigureAwait(false);
            await AuditAsync(tenantId, actor, "ACCESS_PROFILE_PENDING_DELETION", $"access_profile:{name}", new { name }, ct).ConfigureAwait(false);
            return;
        }

        await ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(tenantId, name)] }, ct).ConfigureAwait(false);
        await AuditAsync(tenantId, actor, "ACCESS_PROFILE_DELETED", $"access_profile:{name}", new { name, before = existing.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
    }

    private static VirtualFilter? FindFilter(VirtualFilterSnapshot snapshot, TenantId tenantId, string name) =>
        snapshot.Filters.FirstOrDefault(f => f.TenantId == tenantId && string.Equals(f.Name, name, StringComparison.Ordinal));

    private static VirtualFilterAccessProfile? FindProfile(VirtualFilterSnapshot snapshot, TenantId tenantId, string name) =>
        snapshot.Profiles.FirstOrDefault(p => p.TenantId == tenantId && string.Equals(p.Name, name, StringComparison.Ordinal));

    private static void EnsureFiltersExist(VirtualFilterAccessProfile profile, IEnumerable<string> filterNames)
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
        if (actor.IsSync) return;

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

    private static string CombineUpdatedBy(string? existingUpdatedBy, VirtualFilterActor actor)
    {
        var actorIds = actor.FormatAllIdentifiers();
        if (string.IsNullOrWhiteSpace(existingUpdatedBy)) return actorIds;

        var parts = existingUpdatedBy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Concat(actorIds.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return string.Join(";", parts);
    }

    private static HashSet<string> GetContributorIdentifiers(VirtualFilter filter)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddToContributors(set, filter.CreatedBy, filter.UpdatedBy);
        AddToContributors(set, filter.DeletionRequestedBy, null);

        if (filter.Draft != null)
        {
            AddToContributors(set, filter.Draft.CreatedBy, filter.Draft.UpdatedBy);
            AddToContributors(set, filter.Draft.DeletionRequestedBy, null);
        }

        return set;
    }

    private static HashSet<string> GetContributorIdentifiers(VirtualFilterAccessProfile profile)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddToContributors(set, profile.CreatedBy, profile.UpdatedBy);
        AddToContributors(set, profile.DeletionRequestedBy, null);

        if (profile.Draft != null)
        {
            AddToContributors(set, profile.Draft.CreatedBy, profile.Draft.UpdatedBy);
            AddToContributors(set, profile.Draft.DeletionRequestedBy, null);
        }

        return set;
    }

    private static void AddToContributors(HashSet<string> set, Sid? sid, string? updatedBy)
    {
        if (sid != null && !string.IsNullOrWhiteSpace(sid.Value.Value))
        {
            set.Add(sid.Value.Value);
        }
        if (!string.IsNullOrWhiteSpace(updatedBy))
        {
            foreach (var part in updatedBy.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                set.Add(part);
            }
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
