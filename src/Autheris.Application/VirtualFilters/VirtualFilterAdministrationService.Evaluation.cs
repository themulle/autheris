namespace Autheris.Application.VirtualFilters;

using System;
using Autheris.Application.Governance.SingleApproval;
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
        if (filter.KeySetMaxKeys is { } maxKeys && maxKeys > _federation.MaxAuthorizedKeys)
        {
            throw new ArgumentException(
                $"key_set_max_keys ({maxKeys}) cannot exceed MaxAuthorizedKeys ({_federation.MaxAuthorizedKeys}).",
                nameof(filter));
        }
        RowPolicyParameterBinder.ValidateFilterDeclarations(filter);
        if (filter.Sql == null && filter.Structured == null)
        {
            return filter;
        }

        var sourceScope = string.IsNullOrWhiteSpace(filter.Source) ? null : filter.Source;
        if (filter.Sql != null)
        {
            var catalog = _catalog ?? throw new ArgumentException("Virtual filters with a sql definition need the catalog to be validated.", nameof(filter));
            var tables = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
                catalog,
                new Autheris.Application.Catalog.CatalogTableQuery(
                    DataSource: sourceScope,
                    Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
                ct: ct).ConfigureAwait(false);
            var attributes = _callerAttributes is null ? null : await _callerAttributes.GetAsync(ct).ConfigureAwait(false);
            return SqlFilterCompiler.Validate(filter, tables, _options.AllowedReferenceTables, _dataSources?.RequireTenantColumn == true, _dataSources?.TenantColumnExemptTables, attributes);
        }

        if (filter.Structured != null && _catalog != null)
        {
            var tables = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
                _catalog,
                new Autheris.Application.Catalog.CatalogTableQuery(
                    DataSource: sourceScope,
                    Limit: Autheris.Application.Catalog.CatalogTableQuery.MaxLimit),
                ct: ct).ConfigureAwait(false);
            ValidateStructuredTablesAgainstCatalog(filter, tables);
        }

        return filter;
    }

    private static void ValidateProfileCrossSourceBindings(VirtualFilterAccessProfile profile, IReadOnlyList<VirtualFilter> allFilters)
    {
        var filters = allFilters.Where(f => f.TenantId == profile.TenantId).ToDictionary(f => f.Name, StringComparer.Ordinal);
        foreach (var binding in profile.Bindings)
        {
            if (!filters.TryGetValue(binding.FilterName, out var filter))
            {
                continue;
            }

            var pattern = binding.TargetPattern ?? profile.Scope;
            if (!string.IsNullOrWhiteSpace(pattern) &&
                !TargetPatternMatcher.IsRegex(pattern) &&
                !pattern.Contains('*', StringComparison.Ordinal) &&
                !pattern.Contains('?', StringComparison.Ordinal) &&
                TableIdentifier.TryParse(pattern, out var target))
            {
                RowPolicyParameterBinder.EnsureCrossSourceAllowed(filter, target.Domain);
            }
        }
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
        ValidateProfileCrossSourceBindings(profile, snapshot.Filters);
        var existing = FindProfile(snapshot, profile.TenantId, profile.Name);
        EnsureWritable(existing?.ManagedBy, profile.ManagedBy, actor, $"access profile '{profile.Name}'");

        if (!actor.IsSync && profile.Uncovered != UncoveredPolicy.Deny && profile.GranteeSid is { } grantee)
        {
            // An access profile only narrows rows: leaving uncovered objects to the consents is no narrowing for the writer's own identity.
            SelfWideningGuard.EnsureDoesNotWidenOwnAccess(
                new SingleApprovalWriter(actor.Sid.Value, actor.Identifiers ?? [], []),
                [grantee.Value]);
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

        var evt = existing == null ? "ACCESS_PROFILE_CREATED" : "ACCESS_PROFILE_UPDATED";
        // Fail closed: the change is audited (hashes only) before it takes effect; a failing audit denies it.
        await AuditAsync(profile.TenantId, actor, evt,
            $"access_profile:{profile.Name}", new { name = profile.Name, status = direct.Status.ToString(), before = existing?.ComputeDefinitionHash(), after = profile.ComputeDefinitionHash() }, ct).ConfigureAwait(false);
        await ApplyAsync(new VirtualFilterChangeSet { SaveProfiles = [direct] }, snapshot.Generation, ct).ConfigureAwait(false);
        return direct;
    }

    public async Task DeleteProfileAsync(TenantId tenantId, string name, VirtualFilterActor actor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var snapshot = await _repository.LoadSnapshotAsync(ct).ConfigureAwait(false);
        var existing = FindProfile(snapshot, tenantId, name) ?? throw new KeyNotFoundException($"The access profile '{name}' does not exist.");
        EnsureWritable(existing.ManagedBy, null, actor, $"access profile '{name}'");

        await ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(tenantId, name)] }, snapshot.Generation, ct).ConfigureAwait(false);
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
