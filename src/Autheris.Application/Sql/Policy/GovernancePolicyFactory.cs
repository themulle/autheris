namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>
/// Everything stage 3 of the governed pipeline decided for one statement (WP-X0l). <see cref="Decisions"/> is keyed by the canonical
/// physical identity of the catalog entry. <see cref="TablesWithoutRls"/> lists the tables that have no row policy at all (the tenant
/// predicate is not a row policy: the compiler injects it from the catalog's tenant column).
/// </summary>
public sealed record GovernedPolicyInput(
    string DataSourceName,
    TenantId Tenant,
    IReadOnlyDictionary<TableIdentity, TableAccessDecision> Decisions,
    IReadOnlySet<TableIdentity> TablesWithoutRls,
    GovernedSubqueryStrategy SubqueryStrategy)
{
    /// <summary>Catalog metadata of the tables, so that catalog-sensitive columns without an explicit level are checked for a typed mask.</summary>
    public IReadOnlyDictionary<TableIdentity, TableMetadata>? Metadata { get; init; }
}

/// <summary>
/// WP-X0l: assembles the typed <see cref="GovernancePolicy"/> of one statement from its access decisions and the verified catalog
/// (SEC-ADG-02, SEC-X1-04). Fail closed per table: a table is part of the policy's catalog only when its decision is allowed, its row
/// filter and masks exist as typed policies, and the string and typed forms agree. Every other table is unknown to the compiler, which
/// rejects a reference to it with the same error as for a table that does not exist. The tenant predicate is not assembled here: the
/// compiler builds it from the catalog's tenant column and its database type, with the tenant bound as
/// <see cref="RowPolicyFactory.TenantParameterName"/>.
/// </summary>
public sealed class GovernancePolicyFactory(IGovernedCatalogProvider catalogs)
{
    private readonly IGovernedCatalogProvider _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));

    /// <exception cref="SecurityException">The decisions carry row policies of different target dialects (a gateway defect).</exception>
    public async ValueTask<GovernancePolicy> CreateAsync(GovernedPolicyInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Decisions);
        ArgumentNullException.ThrowIfNull(input.TablesWithoutRls);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.DataSourceName);

        var tables = input.Decisions.Values.Select(static d => d.Table).Distinct().ToList();
        var catalog = await _catalogs.GetCatalogAsync(input.DataSourceName, tables, ct).ConfigureAwait(false);

        var rowPredicates = new Dictionary<TableIdentity, PolicyPredicate?>();
        var masks = new Dictionary<TableIdentity, ColumnMaskPolicy>();
        var consentFiltered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TargetSqlDialect? dialect = null;

        foreach (var (identity, decision) in input.Decisions)
        {
            var metadata = input.Metadata is { } all && all.TryGetValue(identity, out var m) ? m : null;
            if (!TryAdmit(catalog, identity, decision, metadata, input.TablesWithoutRls.Contains(identity), out var rowPolicy, out var tableMasks))
            {
                continue;
            }

            if (rowPolicy is not null)
            {
                if (dialect is { } known && known != rowPolicy.Dialect)
                {
                    throw new SecurityException("The row policies of one statement are not of one target dialect.");
                }

                dialect = rowPolicy.Dialect;
                consentFiltered.Add(identity.ToString());
            }

            // No row policy and not declared without one: deny-all for this table (never "no policy").
            rowPredicates[identity] = rowPolicy?.Predicate ?? (input.TablesWithoutRls.Contains(identity) ? null : PolicyPredicate.DenyAll);
            if (tableMasks is not null)
            {
                masks[identity] = tableMasks;
                masked.Add(identity.ToString());
            }
        }

        var admitted = rowPredicates.Keys.ToHashSet();
        return new GovernancePolicy
        {
            RowFilters = new DecisionRowPolicyProvider(rowPredicates, dialect),
            Masks = new DecisionColumnMaskProvider(masks),
            Catalog = new AdmittedTableCatalog(catalog, admitted),
            Tenant = new TenantBinding(RowPolicyFactory.TenantParameterName, input.Tenant.Value, SqlParameterType.String),
            TablesWithConsentRowFilter = consentFiltered.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            TablesWithMaskedColumns = masked.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            SubqueryStrategy = input.SubqueryStrategy
        };
    }

    private static bool TryAdmit(
        ITableCatalog catalog,
        TableIdentity identity,
        TableAccessDecision decision,
        TableMetadata? metadata,
        bool declaredWithoutRls,
        out PolicyPredicateRowPolicy? rowPolicy,
        out ColumnMaskPolicy? masks)
    {
        rowPolicy = null;
        masks = null;
        if (!decision.IsAllowed)
        {
            return false;
        }

        // SEC-ADG-02 item 3: a text-only (or drifted) row filter and a text-only mask never reach the compiler.
        var typed = decision.RequireTypedRowPolicy().RequireTypedColumnMasks(metadata);
        if (!typed.IsAllowed)
        {
            return false;
        }

        // Only a catalog entry that the provider verified can be admitted, under exactly the identity of the decision.
        var entry = catalog.Resolve(identity.ToQualifiedName());
        if (entry is null || entry.Identity != identity)
        {
            return false;
        }

        if (typed.RowPolicy is not null)
        {
            // A policy must never be dropped silently: a table with a row policy cannot also be declared without one.
            if (typed.RowPolicy is not PolicyPredicateRowPolicy predicateRowPolicy || declaredWithoutRls || !MarkersAgree(typed))
            {
                return false;
            }

            rowPolicy = predicateRowPolicy;
        }

        return TryBuildMasks(entry, typed, metadata, out masks);
    }

    /// <summary>
    /// The string and typed forms of a Virtual Filter decision must come from the same inputs: the text of the mandatory predicate is
    /// exactly the projection of its typed policy, and every marker value it uses is bound identically in the decision's parameters.
    /// </summary>
    private static bool MarkersAgree(TableAccessDecision decision)
    {
        if (decision.MandatoryRowPredicateSql is null)
        {
            return true;
        }

        if (decision.MandatoryRowPolicy is null)
        {
            return false;
        }

        var text = decision.MandatoryRowPolicy.Project();
        if (!string.Equals(text.Sql, decision.MandatoryRowPredicateSql, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var (marker, value) in text.Parameters)
        {
            if (decision.RowFilterParameters is null ||
                !decision.RowFilterParameters.TryGetValue(marker, out var bound) ||
                !(bound is byte[] a && value is byte[] b ? a.AsSpan().SequenceEqual(b) : Equals(bound, value)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The typed masks of a table: the decision's typed mask for every column whose effective level is Mask, and a typed Nullify for
    /// every denied column (the legacy projection answered a denied column with NULL). Null when no column is masked.
    /// </summary>
    private static bool TryBuildMasks(TableCatalogEntry entry, TableAccessDecision decision, TableMetadata? metadata, out ColumnMaskPolicy? masks)
    {
        masks = null;
        var specs = new Dictionary<string, MaskSpec>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in entry.Columns)
        {
            switch (decision.GetEffectiveColumnAccess(column.Name, metadata))
            {
                case ColumnAccessLevel.Deny:
                    specs[column.Name] = new MaskSpec(MaskKind.Nullify, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
                    break;
                case ColumnAccessLevel.Mask:
                    if (decision.ColumnMasks is not ColumnMaskPolicy typedMasks || !typedMasks.TryGetSpec(column.Name, out var spec))
                    {
                        return false;
                    }

                    specs[column.Name] = spec;
                    break;
            }
        }

        masks = specs.Count == 0 ? null : new ColumnMaskPolicy(specs);
        return true;
    }

    /// <summary>The catalog of the admitted tables only; everything else resolves to null, which the compiler rejects (fail closed).</summary>
    private sealed class AdmittedTableCatalog(ITableCatalog inner, IReadOnlySet<TableIdentity> admitted) : ITableCatalog
    {
        public TableCatalogEntry? Resolve(TrinoSqlEngine.Ast.Nodes.SqlQualifiedName name)
        {
            var entry = inner.Resolve(name);
            return entry is not null && admitted.Contains(entry.Identity) ? entry : null;
        }
    }
}

/// <summary>
/// Row policies of the admitted tables; a null entry means the table was declared without a row policy. A table without an entry gets
/// deny-all (never null, never "no policy").
/// </summary>
internal sealed class DecisionRowPolicyProvider(IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates, TargetSqlDialect? dialect) : IPolicyPredicateProvider
{
    /// <summary>The target dialect of the typed policies (null when no table has one).</summary>
    public TargetSqlDialect? Dialect { get; } = dialect;

    public bool ShouldApplyPolicy(TableIdentity table) => !predicates.TryGetValue(table, out var predicate) || predicate is not null;

    public PolicyPredicate GetPredicate(TableIdentity table) =>
        predicates.TryGetValue(table, out var predicate) && predicate is not null ? predicate : PolicyPredicate.DenyAll;
}
