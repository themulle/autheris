namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

/// <summary>Policy state of one table that took part in a compile; recorded for the value-free plan template (SEC-ADG-01).</summary>
public sealed record TableUsage(TableIdentity Identity, bool PolicyApplied, string PredicateFingerprint, string MaskFingerprint);

/// <summary>
/// Typed governance input of one compile, shared by the security visitor (injection) and the coverage verifier (proof). It
/// collects the values to bind (tenant, policy, mask), the applied predicate ids and the tables used. One instance per compile.
/// </summary>
public sealed class TypedPolicyContext
{
    private readonly Dictionary<string, PolicyValue> _values = new(StringComparer.Ordinal);
    private readonly List<SecurityPredicateId> _applied = new();
    private readonly List<TableUsage> _tables = new();

    public TypedPolicyContext(
        ITableCatalog catalog,
        IPolicyPredicateProvider rowFilters,
        IColumnMaskProvider masks,
        TenantBinding tenant,
        DialectCapabilities capabilities)
    {
        Catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        RowFilters = rowFilters ?? throw new ArgumentNullException(nameof(rowFilters));
        Masks = masks ?? throw new ArgumentNullException(nameof(masks));
        Tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));
        Capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
    }

    public ITableCatalog Catalog { get; }
    public IPolicyPredicateProvider RowFilters { get; }
    public IColumnMaskProvider Masks { get; }
    public TenantBinding Tenant { get; }
    public DialectCapabilities Capabilities { get; }

    /// <summary>Values to bind, by parameter name (tenant, policy and mask parameters of every applied table).</summary>
    public IReadOnlyDictionary<string, PolicyValue> PolicyValues => _values;

    /// <summary>Root predicate ids that were injected (policy subquery tenant predicates are not listed).</summary>
    public ImmutableArray<SecurityPredicateId> AppliedPredicates => _applied.ToImmutableArray();

    public IReadOnlyList<TableUsage> Tables => _tables;

    /// <summary>The tenant predicate node for <paramref name="entry"/>, or null for a table without a tenant column.</summary>
    public Expression? BuildTenantPredicate(TableCatalogEntry entry)
    {
        if (entry.TenantColumn is null) return null;
        AddValue(Tenant.ParameterName, new PolicyValue(Tenant.Value, Tenant.Type));
        return TenantPredicateFactory.Build(Capabilities, entry.TenantColumn, Tenant.ParameterName, Tenant.Type);
    }

    public void AddValues(IReadOnlyDictionary<string, PolicyValue> values)
    {
        foreach (var (name, value) in values) AddValue(name, value);
    }

    public void AddValue(string name, PolicyValue value)
    {
        if (_values.TryGetValue(name, out var existing))
        {
            bool same = existing.Type == value.Type &&
                        (existing.Value is byte[] a && value.Value is byte[] b ? a.AsSpan().SequenceEqual(b) : Equals(existing.Value, value.Value));
            if (!same)
            {
                throw new PolicyConflictException($"Policy parameter '{name}' is bound to conflicting values.");
            }

            return;
        }

        _values[name] = value;
    }

    public void RecordApplied(SecurityPredicateId id)
    {
        if (!_applied.Contains(id)) _applied.Add(id);
    }

    public void RecordTable(TableUsage usage)
    {
        if (!_tables.Contains(usage)) _tables.Add(usage);
    }

    /// <summary>
    /// A verifier whose expectations come from the providers, independent of the injector (plan 3.2): every table needs its
    /// tenant predicate (when it has a tenant column) and its policy predicate (when the provider says one applies).
    /// </summary>
    public SecurityCoverageVerifier CreateVerifier(TargetSqlDialect dialect = TargetSqlDialect.SqlServer) =>
        new(name => RequirementOf(name), dialect);

    private TableCoverageRequirement? RequirementOf(SqlQualifiedName name)
    {
        var entry = Catalog.Resolve(name);
        if (entry is null) return null;
        var id = entry.Identity;
        var tenant = entry.TenantColumn is null
            ? ImmutableArray<SecurityPredicateId>.Empty
            : ImmutableArray.Create(new SecurityPredicateId(id.ToString(), 0));
        var root = RowFilters.ShouldApplyPolicy(id) ? tenant.Add(new SecurityPredicateId(id.ToString(), 1)) : tenant;
        var masked = entry.Columns.Where(c => Masks.HasMask(id, c.Name)).Select(c => c.Name).ToImmutableHashSet(StringComparer.Ordinal);
        return new TableCoverageRequirement(id.ToString(), id.Schema, id.Table, root, tenant, masked);
    }
}
