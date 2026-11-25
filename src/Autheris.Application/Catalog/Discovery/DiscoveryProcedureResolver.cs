namespace Autheris.Application.Catalog.Discovery;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

internal sealed record EligibleDiscoveryProcedure(
    RegisteredProcedure Procedure,
    IReadOnlyList<TableMetadata> GovernedTables);

/// <summary>
/// Filters and maps active Read-mode governed procedures into <see cref="DiscoveryObject"/> entries,
/// evaluating execute eligibility solely via <see cref="IAccessDecisionPoint.DecideManyAsync"/>.
/// </summary>
internal static class DiscoveryProcedureResolver
{
    public static List<EligibleDiscoveryProcedure> FilterEligible(
        IReadOnlyList<RegisteredProcedure> visibleProcedures,
        IReadOnlyList<TableMetadata> visibleTables,
        TenantId? tenant = null)
    {
        if (visibleProcedures.Count == 0 || visibleTables.Count == 0)
        {
            return [];
        }

        var nonHiddenById = new Dictionary<TableIdentifier, TableMetadata>();
        foreach (var table in visibleTables)
        {
            if (!table.Table.DiscoveryHidden)
            {
                nonHiddenById.TryAdd(table.Identifier, table);
            }
        }

        if (nonHiddenById.Count == 0)
        {
            return [];
        }

        var eligible = new List<EligibleDiscoveryProcedure>(visibleProcedures.Count);

        foreach (var proc in visibleProcedures)
        {
            if (proc.State != ProcedureState.Active || proc.Definition.Mode != ProcedureMode.Read)
            {
                continue;
            }

            // a product procedure is decided on its product object, never on the tables its SQL touches
            var governedIds = string.IsNullOrWhiteSpace(proc.Definition.ProductSchema)
                ? proc.GovernedTables()
                : ProductObjects(proc.Definition, tenant);
            if (governedIds is null || governedIds.Count == 0)
            {
                continue;
            }

            var metas = new List<TableMetadata>(governedIds.Count);
            var allDiscoverable = true;
            foreach (var id in governedIds)
            {
                if (!nonHiddenById.TryGetValue(id, out var meta))
                {
                    allDiscoverable = false;
                    break;
                }

                metas.Add(meta);
            }

            if (allDiscoverable)
            {
                eligible.Add(new EligibleDiscoveryProcedure(proc, metas));
            }
        }

        return eligible;
    }

    private static IReadOnlyList<TableIdentifier>? ProductObjects(ProcedureDefinition definition, TenantId? tenant) =>
        tenant is { Value: { Length: > 0 } } t
            ? [Autheris.Application.Products.ProductSource.ObjectOf(t, definition.ProductSchema!.Trim().ToLowerInvariant(), definition.Name.ToLowerInvariant())]
            : null;

    public static bool MatchesQuery(
        RegisteredProcedure proc,
        string? domain,
        string? schema,
        string? tag,
        string? search)
    {
        if (!string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        var def = proc.Definition;
        if (!string.IsNullOrWhiteSpace(domain) &&
            !string.Equals(def.CatalogDomain, domain.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(schema) &&
            !string.Equals("procedure", schema.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            return MatchesText(def, search.Trim());
        }

        return true;
    }

    public static bool MatchesText(ProcedureDefinition def, string query)
    {
        if (def.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            def.CatalogDomain.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            "procedure".Contains(query, StringComparison.OrdinalIgnoreCase) ||
            def.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (def.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return true;
        }

        foreach (var param in def.Parameters)
        {
            if (param.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (param.Description?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                return true;
            }
        }

        return false;
    }

    public static async Task<IReadOnlyList<DiscoveryObject>> ResolveBatchAsync(
        IAccessDecisionPoint decisionPoint,
        IContainerOwnership? ownershipRepository,
        SecurityPrincipalContext subject,
        IReadOnlyList<EligibleDiscoveryProcedure> items,
        IReadOnlySet<TableIdentifier> pendingTables,
        IReadOnlySet<TableIdentifier> expiredTables,
        CancellationToken ct,
        IReadOnlyDictionary<TableIdentifier, Autheris.Domain.Security.ProtectionClass>? classesByTable = null)
    {
        if (items.Count == 0)
        {
            return [];
        }

        var distinctTables = new List<TableMetadata>();
        var distinctIds = new HashSet<TableIdentifier>();
        foreach (var item in items)
        {
            foreach (var table in item.GovernedTables)
            {
                if (distinctIds.Add(table.Identifier))
                {
                    distinctTables.Add(table);
                }
            }
        }

        var decisionsTask = decisionPoint.DecideManyAsync(
            subject, distinctTables, AccessOperation.Execute, AccessTransport.Discovery, ct).AsTask();
        var ownersTask = ownershipRepository is null
            ? Task.FromResult<IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>>(
                new Dictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>>())
            : ownershipRepository.GetDataOwnersForTablesAsync(distinctIds.ToList(), ct);

        await Task.WhenAll(decisionsTask, ownersTask).ConfigureAwait(false);

        var decisions = await decisionsTask.ConfigureAwait(false);
        var ownersByTable = await ownersTask.ConfigureAwait(false);

        if (decisions.Count != distinctTables.Count)
        {
            throw new InvalidOperationException("AccessDecisionPoint returned mismatched execute decision count.");
        }

        var decisionById = new Dictionary<TableIdentifier, TableAccessDecision>(distinctTables.Count);
        for (var i = 0; i < distinctTables.Count; i++)
        {
            decisionById[distinctTables[i].Identifier] = decisions[i];
        }

        var mapped = new List<DiscoveryObject>(items.Count);
        foreach (var item in items)
        {
            mapped.Add(MapProcedure(item.Procedure, item.GovernedTables, decisionById, ownersByTable, pendingTables, expiredTables, classesByTable));
        }

        return mapped;
    }

    private static DiscoveryObject MapProcedure(
        RegisteredProcedure proc,
        IReadOnlyList<TableMetadata> governedTables,
        IReadOnlyDictionary<TableIdentifier, TableAccessDecision> decisionById,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>> ownersByTable,
        IReadOnlySet<TableIdentifier> pendingTables,
        IReadOnlySet<TableIdentifier> expiredTables,
        IReadOnlyDictionary<TableIdentifier, Autheris.Domain.Security.ProtectionClass>? classesByTable = null)
    {
        var allAllowed = true;
        var hasRowFilters = false;
        var anyPending = false;
        var anyExpired = false;

        foreach (var table in governedTables)
        {
            var decision = decisionById[table.Identifier];
            if (!decision.IsAllowed)
            {
                allAllowed = false;
                if (pendingTables.Contains(table.Identifier))
                {
                    anyPending = true;
                }
                else if (expiredTables.Contains(table.Identifier))
                {
                    anyExpired = true;
                }
            }
            else if (DiscoveryAccessMapper.HasRowPredicate(decision))
            {
                hasRowFilters = true;
            }
        }

        var access = allAllowed
            ? DiscoveryAccessState.Granted
            : anyPending
                ? DiscoveryAccessState.Requested
                : anyExpired
                    ? DiscoveryAccessState.Expired
                    : DiscoveryAccessState.None;

        DiscoveryRowScope? rowScope = allAllowed
            ? (hasRowFilters ? DiscoveryRowScope.RowRestricted : DiscoveryRowScope.Full)
            : null;

        var def = proc.Definition;
        var columns = def.Parameters.Select(p => new DiscoveryColumn(
            Name: p.Name,
            DisplayName: null,
            DataType: p.SqlType,
            Description: DiscoveryTextSanitizer.SanitizeText(p.Description, DiscoveryTextSanitizer.MaxDescriptionRunes),
            Pii: false,
            ClearAccessRequiresSeparateApproval: false,
            State: DiscoveryColumnState.Clear,
            Statistics: null)).ToList();

        var owners = CollectOwners(governedTables, ownersByTable);
        var sensitivity = ResolveSensitivity(governedTables, classesByTable);

        return new DiscoveryObject(
            Kind: DiscoveryObjectKind.Procedure,
            Domain: def.CatalogDomain,
            Schema: "procedure",
            Name: def.Name,
            DisplayName: DiscoveryTextSanitizer.SanitizeText(def.Summary, DiscoveryTextSanitizer.MaxDisplayNameRunes),
            Description: DiscoveryTextSanitizer.SanitizeText(def.Description ?? def.Summary, DiscoveryTextSanitizer.MaxDescriptionRunes),
            Sensitivity: sensitivity,
            Tags: [],
            Owners: owners,
            Access: access,
            RowScope: rowScope,
            Columns: columns,
            Statistics: null,
            SamplesAvailable: false,
            IsHidden: false,
            Write: null);
    }

    private static IReadOnlyList<DiscoveryOwner> CollectOwners(
        IReadOnlyList<TableMetadata> governedTables,
        IReadOnlyDictionary<TableIdentifier, IReadOnlyList<ContainerOwnerEntry>> ownersByTable)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var owners = new List<DiscoveryOwner>();
        foreach (var table in governedTables)
        {
            if (!ownersByTable.TryGetValue(table.Identifier, out var list))
            {
                continue;
            }

            foreach (var o in list)
            {
                var name = DiscoveryTextSanitizer.SanitizeRequiredText(o.DisplayName, string.Empty);
                var email = string.IsNullOrWhiteSpace(o.Email)
                    ? null
                    : DiscoveryTextSanitizer.SanitizeText(o.Email, DiscoveryTextSanitizer.MaxDisplayNameRunes);
                var dedupKey = $"{name}|{email}";
                if (seen.Add(dedupKey))
                {
                    owners.Add(new DiscoveryOwner(name, email));
                }
            }
        }

        return owners;
    }

    private static string ResolveSensitivity(IReadOnlyList<TableMetadata> governedTables, IReadOnlyDictionary<TableIdentifier, ProtectionClass>? classesByTable = null)
    {
        if (governedTables.Count == 0 || classesByTable is null || classesByTable.Count == 0)
        {
            return "CONFIDENTIAL";
        }

        var strictest = ProtectionClass.Public;
        var matchedAny = false;
        foreach (var table in governedTables)
        {
            if (classesByTable.TryGetValue(table.Identifier, out var cls))
            {
                strictest = ProtectionClassExtensions.Max(strictest, cls);
                matchedAny = true;
            }
            else
            {
                return "CONFIDENTIAL";
            }
        }

        return matchedAny ? strictest.ToCanonicalName() : "CONFIDENTIAL";
    }
}
