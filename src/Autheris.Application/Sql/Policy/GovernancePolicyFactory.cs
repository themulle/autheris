namespace Autheris.Application.Sql.Policy;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
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

    /// <summary>
    /// Issue #107 (D-WPC-1): statement context of the key-set stage. <c>null</c> (any other caller) means the stage is not invoked and the
    /// assembly is byte-identical to the one without the feature (AC-8).
    /// </summary>
    public RowFilterKeySetStatement? KeySetStatement { get; init; }
}

/// <summary>
/// D-WPC-1 / SEC-RF107-17: <paramref name="IsRead"/> is <c>metadata.StatementType == Select</c> (the same classification as <c>isDml</c>),
/// <paramref name="UserSqlLength"/> the length of the user statement, <paramref name="MaxExpansionFactor"/> the normalized factor of the compile
/// request, <paramref name="Disabled"/> true on the one retry of SEC-RF107-15 (every site stays <c>EXISTS</c>).
/// </summary>
public sealed record RowFilterKeySetStatement(bool IsRead, int UserSqlLength, int MaxExpansionFactor, bool Disabled);

/// <summary>
/// WP-X0l: assembles the typed <see cref="GovernancePolicy"/> of one statement from its access decisions and the verified catalog
/// (SEC-ADG-02, SEC-X1-04). Fail closed per table: a table is part of the policy's catalog only when its decision is allowed, its row
/// filter and masks exist as typed policies, and the string and typed forms agree. Every other table is unknown to the compiler, which
/// rejects a reference to it with the same error as for a table that does not exist. The tenant predicate is not assembled here: the
/// compiler builds it from the catalog's tenant column and its database type, with the tenant bound as
/// <see cref="RowPolicyFactory.TenantParameterName"/>.
/// </summary>
public sealed class GovernancePolicyFactory(
    IGovernedCatalogProvider catalogs,
    ITableMetadataRepository tables,
    IOptions<GatewayOptions> options,
    IRowFilterKeySetStage? keySetStage = null)
{
    private readonly IRowFilterKeySetStage? _keySetStage = keySetStage;
    private readonly IGovernedCatalogProvider _catalogs = catalogs ?? throw new ArgumentNullException(nameof(catalogs));
    private readonly ITableMetadataRepository _tables = tables ?? throw new ArgumentNullException(nameof(tables));
    private readonly IOptions<GatewayOptions> _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <exception cref="SecurityException">The decisions carry row policies of different target dialects (a gateway defect).</exception>
    public async ValueTask<GovernancePolicy> CreateAsync(GovernedPolicyInput input, CancellationToken ct) =>
        (await CreateAssemblyAsync(input, ct).ConfigureAwait(false)).Policy;

    /// <summary>
    /// The policy plus the key-set diagnostics of the statement (issue #107, A-2). After the admission loop the key-set stage may replace correlated
    /// <c>EXISTS</c> sites of the admitted predicates by cached key sets; only the compiler input (<c>rowPredicates</c>) changes, never a decision,
    /// recipe or cache (SEC-RF107-04 a, -10 c). The policy catalog's dependents are computed from the final predicates.
    /// </summary>
    /// <exception cref="SecurityException">The decisions carry row policies of different target dialects (a gateway defect).</exception>
    public async ValueTask<GovernedPolicyAssembly> CreateAssemblyAsync(GovernedPolicyInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Decisions);
        ArgumentNullException.ThrowIfNull(input.TablesWithoutRls);
        ArgumentException.ThrowIfNullOrWhiteSpace(input.DataSourceName);

        var tables = input.Decisions.Values.Select(static d => d.Table).Distinct().ToList();

        // A typed row policy can read other tables in a subquery (correlated consent filters, virtual filters with a key source). The
        // compiler resolves those names against the same catalog, so the provider is asked for them too: it verifies them like any
        // other table (an unverifiable table is simply absent, and the policy that needs it denies its own table below).
        var policyTables = input.Decisions.Values
            .Select(static d => d.RowPolicy)
            .OfType<PolicyPredicateRowPolicy>()
            .SelectMany(static p => PolicySubqueryTables.Names(p.Predicate))
            .Distinct()
            .ToList();
        var requested = new List<TableIdentifier>(tables);
        var records = new Dictionary<TableIdentifier, GovernanceRecord>();
        foreach (var name in policyTables)
        {
            foreach (var candidate in PolicySubqueryTables.Candidates(name, input.DataSourceName))
            {
                // M-2: only a candidate that the catalog provider can admit (a governance record of the statement's data source) is
                // requested. The catalog cache is keyed without the domain, so a homonymous candidate that can never be admitted would
                // evict the admitted one's entry and force a reload per statement. A record that cannot be read stays requested: it is
                // unusable below (fail closed), exactly as before.
                var record = await LoadRecordAsync(records, candidate, ct).ConfigureAwait(false);
                if ((record.Failed || record.Metadata is { } metadata && string.Equals(metadata.Table.SourceName, input.DataSourceName, StringComparison.OrdinalIgnoreCase))
                    && !requested.Contains(candidate))
                {
                    requested.Add(candidate);
                }
            }
        }

        var catalog = await _catalogs.GetCatalogAsync(input.DataSourceName, requested, ct).ConfigureAwait(false);

        // M-A1: a table that only a policy subquery reads is governed like a primary table: it must be active and satisfy
        // DataSources:RequireTenantColumn. The policy that needs a table that violates this denies its own table (below).
        var unusableDependents = await FindUnusableDependentsAsync(input, catalog, requested, records, ct).ConfigureAwait(false);

        var rowPredicates = new Dictionary<TableIdentity, PolicyPredicate?>();
        var masks = new Dictionary<TableIdentity, ColumnMaskPolicy>();
        var consentFiltered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        TargetSqlDialect? dialect = null;

        foreach (var (identity, decision) in input.Decisions)
        {
            var metadata = input.Metadata is { } all && all.TryGetValue(identity, out var m) ? m : null;
            if (!TryAdmit(catalog, unusableDependents, identity, decision, metadata, input.TablesWithoutRls.Contains(identity), out var rowPolicy, out var tableMasks))
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
        // The tables that the admitted policies read: resolvable for the compiler's policy subqueries, but never "admitted" (no decision,
        // so a user reference to one is still governed by stage 3 and by the deny-all default of the row policy provider).
        var dependents = new HashSet<TableIdentity>();
        foreach (var decision in input.Decisions)
        {
            if (admitted.Contains(decision.Key) && decision.Value.RowPolicy is PolicyPredicateRowPolicy policy)
            {
                foreach (var name in PolicySubqueryTables.Names(policy.Predicate))
                {
                    if (catalog.Resolve(name) is { } entry)
                    {
                        dependents.Add(entry.Identity);
                    }
                }
            }
        }

        var keySets = new RowFilterKeySetReport();
        IReadOnlyDictionary<TableIdentity, PolicyPredicate?> finalPredicates = rowPredicates;
        if (_keySetStage is not null && input.KeySetStatement is { } statement && dialect is { } keySetDialect && HasRegisteredSite(rowPredicates))
        {
            var context = await BuildKeySetContextAsync(input, statement, keySetDialect, catalog, admitted, dependents, rowPredicates, records, ct).ConfigureAwait(false);
            if (statement.IsRead)
            {
                finalPredicates = await _keySetStage.ApplyAsync(context, rowPredicates, keySets, ct).ConfigureAwait(false);
            }
            else
            {
                // D-WPC-2 / SEC-RF107-17: no epoch read, cache lookup or key query for a write or unknown statement type.
                _keySetStage.ReportSkipped(context, rowPredicates, keySets, KeySetFallbackReason.WriteStatement);
            }

            if (!ReferenceEquals(finalPredicates, rowPredicates))
            {
                // A fully substituted policy no longer reads its dependent tables: they stop being resolvable for the compiler (A-2).
                dependents = ReadTables(catalog, finalPredicates, admitted);
            }
        }

        var policyResult = new GovernancePolicy
        {
            RowFilters = new DecisionRowPolicyProvider(finalPredicates, dialect),
            Masks = new DecisionColumnMaskProvider(masks),
            Catalog = new AdmittedTableCatalog(catalog, admitted, dependents),
            Tenant = new TenantBinding(RowPolicyFactory.TenantParameterName, input.Tenant.Value, SqlParameterType.String),
            TablesWithConsentRowFilter = consentFiltered.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            TablesWithMaskedColumns = masked.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            SubqueryStrategy = input.SubqueryStrategy
        };

        return new GovernedPolicyAssembly(policyResult, keySets);
    }

    /// <summary>True when any predicate holds a registered correlated site; without one the key-set stage has nothing to do and no governance record is read for it.</summary>
    private static bool HasRegisteredSite(IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates)
    {
        foreach (var predicate in predicates.Values)
        {
            if (predicate is not null && CorrelatedKeySetSubstitution.Scan(predicate) is { Eligible.Count: > 0 } or { Excluded.Count: > 0 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The tables read by the given predicates of admitted tables (resolvable subquery tables of the final policy).</summary>
    private static HashSet<TableIdentity> ReadTables(ITableCatalog catalog, IReadOnlyDictionary<TableIdentity, PolicyPredicate?> predicates, IReadOnlySet<TableIdentity> admitted)
    {
        var result = new HashSet<TableIdentity>();
        foreach (var (identity, predicate) in predicates)
        {
            if (!admitted.Contains(identity) || predicate is null)
            {
                continue;
            }

            foreach (var name in PolicySubqueryTables.Names(predicate))
            {
                if (catalog.Resolve(name) is { } entry)
                {
                    result.Add(entry.Identity);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The stage input (A-2, C-8): the catalog still resolves the dependents (the key query reads them), the governance identifiers per table are the
    /// target and every admittable dependent record (the epoch keys, A-6, A-13).
    /// </summary>
    private async ValueTask<RowFilterKeySetContext> BuildKeySetContextAsync(
        GovernedPolicyInput input,
        RowFilterKeySetStatement statement,
        TargetSqlDialect dialect,
        ITableCatalog catalog,
        IReadOnlySet<TableIdentity> admitted,
        IReadOnlySet<TableIdentity> dependents,
        Dictionary<TableIdentity, PolicyPredicate?> rowPredicates,
        Dictionary<TableIdentifier, GovernanceRecord> records,
        CancellationToken ct)
    {
        // The dependent scan only feeds the epoch keys of a read statement against an opted-in data source; the stage stops before them otherwise.
        bool needDependents = statement.IsRead
            && _options.Value.DataSources?.Connections is { } connections
            && connections.TryGetValue(input.DataSourceName, out var connection)
            && connection.KeySetCacheAllowed;
        var identifiers = new Dictionary<TableIdentity, IReadOnlyList<TableIdentifier>>();
        var metadata = new Dictionary<TableIdentity, TableMetadata?>();
        foreach (var (identity, predicate) in rowPredicates)
        {
            if (input.Metadata is { } all && all.TryGetValue(identity, out var m))
            {
                metadata[identity] = m;
            }

            var ids = new List<TableIdentifier> { input.Decisions[identity].Table };
            if (predicate is not null && needDependents)
            {
                foreach (var name in PolicySubqueryTables.Names(predicate))
                {
                    foreach (var candidate in PolicySubqueryTables.Candidates(name, input.DataSourceName))
                    {
                        var record = await LoadRecordAsync(records, candidate, ct).ConfigureAwait(false);
                        if (record.Metadata is { } dependent
                            && string.Equals(dependent.Table.SourceName, input.DataSourceName, StringComparison.OrdinalIgnoreCase)
                            && !ids.Contains(candidate))
                        {
                            ids.Add(candidate);
                        }
                    }
                }
            }

            identifiers[identity] = ids;
        }

        return new RowFilterKeySetContext(
            input.DataSourceName,
            input.Tenant,
            dialect,
            new AdmittedTableCatalog(catalog, admitted, dependents),
            new TenantBinding(RowPolicyFactory.TenantParameterName, input.Tenant.Value, SqlParameterType.String),
            metadata,
            identifiers,
            statement.Disabled,
            statement.IsRead,
            statement.UserSqlLength,
            statement.MaxExpansionFactor);
    }

    /// <summary>
    /// M-A1: the verified tables that the policies read in a subquery and that are inactive, have no governance record, or violate
    /// <c>DataSources:RequireTenantColumn</c> (not exempt). Any failure to load the governance record counts as unusable (fail closed).
    /// </summary>
    private async ValueTask<HashSet<TableIdentity>> FindUnusableDependentsAsync(
        GovernedPolicyInput input, ITableCatalog catalog, List<TableIdentifier> requested, Dictionary<TableIdentifier, GovernanceRecord> records, CancellationToken ct)
    {
        var unusable = new HashSet<TableIdentity>();
        var checkedTables = new HashSet<TableIdentity>();
        var dataSources = _options.Value.DataSources;
        foreach (var decision in input.Decisions.Values)
        {
            if (decision.RowPolicy is not PolicyPredicateRowPolicy policy)
            {
                continue;
            }

            foreach (var name in PolicySubqueryTables.Names(policy.Predicate))
            {
                if (catalog.Resolve(name) is not { } entry || !checkedTables.Add(entry.Identity))
                {
                    continue;
                }

                // L-1: the governance record that decides is the one the catalog provider can admit (its data source is the one of the
                // statement). Homonymous tables of other domains are candidates too, so every record that could be admitted is checked and
                // one unusable admittable record denies the table; no admittable record at all is unusable as well (fail closed).
                var admittable = 0;
                var usable = true;
                foreach (var candidate in requested.Where(r => string.Equals(r.Schema, entry.Identity.Schema, StringComparison.OrdinalIgnoreCase)
                                                               && string.Equals(r.TableName, entry.Identity.Table, StringComparison.OrdinalIgnoreCase)))
                {
                    var record = await LoadRecordAsync(records, candidate, ct).ConfigureAwait(false);
                    if (record.Failed)
                    {
                        usable = false;
                        break;
                    }

                    var metadata = record.Metadata;
                    if (metadata is null || !string.Equals(metadata.Table.SourceName, input.DataSourceName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    admittable++;
                    if (!metadata.Table.IsActive)
                    {
                        usable = false;
                        break;
                    }

                    try
                    {
                        TableMetadata.RequireTenantColumnOrThrow(metadata, dataSources?.RequireTenantColumn == true, dataSources?.TenantColumnExemptTables);
                    }
                    catch (SecurityException)
                    {
                        usable = false;
                        break;
                    }
                }

                if (!usable || admittable == 0)
                {
                    unusable.Add(entry.Identity);
                }
            }
        }

        return unusable;
    }

    /// <summary>The governance record of a candidate (read once per assembly); a read failure is remembered, never swallowed into "no record".</summary>
    private readonly record struct GovernanceRecord(TableMetadata? Metadata, bool Failed);

    private async ValueTask<GovernanceRecord> LoadRecordAsync(Dictionary<TableIdentifier, GovernanceRecord> records, TableIdentifier candidate, CancellationToken ct)
    {
        if (records.TryGetValue(candidate, out var known))
        {
            return known;
        }

        GovernanceRecord record;
        try
        {
            record = new GovernanceRecord(await _tables.GetTableMetadataAsync(candidate, ct).ConfigureAwait(false), false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            record = new GovernanceRecord(null, true);
        }

        records[candidate] = record;
        return record;
    }

    private static bool TryAdmit(
        ITableCatalog catalog,
        IReadOnlySet<TableIdentity> unusableDependents,
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

            // SEC-X1-05: a policy that reads a table the catalog cannot verify cannot be enforced, so its own table is denied (fail closed,
            // reported like every other "table not usable" cause) instead of failing inside the compiler with another code.
            if (PolicySubqueryTables.Names(predicateRowPolicy.Predicate).Any(name =>
                    catalog.Resolve(name) is not { } dependent || unusableDependents.Contains(dependent.Identity)))
            {
                rowPolicy = null;
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

    /// <summary>
    /// The catalog of the admitted tables and of the tables their policies read in subqueries; everything else resolves to null, which the
    /// compiler rejects (fail closed).
    /// </summary>
    private sealed class AdmittedTableCatalog(ITableCatalog inner, IReadOnlySet<TableIdentity> admitted, IReadOnlySet<TableIdentity> dependents) : ITableCatalog
    {
        public TableCatalogEntry? Resolve(TrinoSqlEngine.Ast.Nodes.SqlQualifiedName name)
        {
            var entry = inner.Resolve(name);
            return entry is not null && (admitted.Contains(entry.Identity) || dependents.Contains(entry.Identity)) ? entry : null;
        }
    }
}

/// <summary>The tables that a typed row policy reads in its subqueries (correlated consent filters, virtual filters with a key source).</summary>
internal static class PolicySubqueryTables
{
    private sealed class Collector : TrinoSqlEngine.Ast.Visitors.SqlAstRewriter
    {
        public List<TrinoSqlEngine.Ast.Nodes.SqlQualifiedName> Names { get; } = [];

        public override TrinoSqlEngine.Ast.Nodes.SqlNode VisitNamedTableSource(TrinoSqlEngine.Ast.Nodes.NamedTableSource node)
        {
            Names.Add(node.Name);
            return node;
        }
    }

    // The assembly asks for the names of one predicate several times (request list, dependents check, admission); the predicate is
    // immutable, so the walk happens once per instance.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PolicyPredicate, IReadOnlyList<TrinoSqlEngine.Ast.Nodes.SqlQualifiedName>> NameCache = new();

    /// <summary>Every table name of the predicate (a predicate without a subquery has none).</summary>
    public static IReadOnlyList<TrinoSqlEngine.Ast.Nodes.SqlQualifiedName> Names(PolicyPredicate predicate) =>
        NameCache.GetValue(predicate, static p =>
        {
            var collector = new Collector();
            collector.Visit(p.Expression);
            return collector.Names;
        });

    /// <summary>
    /// The governance identifiers a policy table name may stand for. The policy renders <c>schema.table</c> (SQLite: the table alone), without
    /// the data source, so the data source of the statement and the legacy <c>default</c> domain are tried. The provider verifies each
    /// candidate against the governance record and the database and omits the ones that do not exist.
    /// </summary>
    public static IEnumerable<TableIdentifier> Candidates(TrinoSqlEngine.Ast.Nodes.SqlQualifiedName name, string dataSource)
    {
        var parts = name.Parts;
        switch (parts.Count)
        {
            case 1:
                yield return new TableIdentifier(dataSource, "main", parts[0].Value);
                yield return new TableIdentifier("default", "main", parts[0].Value);
                break;
            case 2:
                yield return new TableIdentifier(dataSource, parts[0].Value, parts[1].Value);
                yield return new TableIdentifier("default", parts[0].Value, parts[1].Value);
                break;
            case 3:
                yield return new TableIdentifier(parts[0].Value, parts[1].Value, parts[2].Value);
                yield return new TableIdentifier(dataSource, parts[1].Value, parts[2].Value);
                break;
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
