namespace TrinoSqlEngine.Governance;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;

/// <summary>Policy state of one table that took part in a compile; recorded for the value-free plan template (SEC-ADG-01).</summary>
public sealed record TableUsage(TableIdentity Identity, bool PolicyApplied, string PredicateFingerprint, string MaskFingerprint);

/// <summary>A catalog table the compile resolved, with the fingerprint of everything that decided its injection.</summary>
public sealed record TableDependency(SqlQualifiedName Name, string Fingerprint);

/// <summary>
/// Typed governance input of one compile, shared by the security visitor (injection) and the coverage verifier (proof). It
/// collects the values to bind (tenant, policy, mask), the applied predicate ids and the tables used. One instance per compile.
/// </summary>
public sealed class TypedPolicyContext
{
    private readonly Dictionary<string, PolicyValue> _values = new(StringComparer.Ordinal);
    private readonly List<SecurityPredicateId> _applied = new();
    private readonly List<TableUsage> _tables = new();
    private readonly List<TableDependency> _dependencies = new();

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

    public ImmutableArray<TableDependency> Dependencies => _dependencies.ToImmutableArray();

    /// <summary>The tenant predicate node for <paramref name="entry"/>, or null for a table without a tenant column.</summary>
    public Expression? BuildTenantPredicate(TableCatalogEntry entry)
    {
        if (entry.TenantColumn is null) return null;
        AddValue(Tenant.ParameterName, new PolicyValue(Tenant.Value, Tenant.Type));
        var tenantColumn = entry.Columns.FirstOrDefault(c => string.Equals(c.Name, entry.TenantColumn, StringComparison.OrdinalIgnoreCase));
        return TenantPredicateFactory.Build(Capabilities, entry.TenantColumn, Tenant.ParameterName, Tenant.Type, tenantColumn?.DataType, tenantColumn?.Collation);
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

    /// <summary>Records that <paramref name="name"/> resolved to <paramref name="entry"/> (plan template dependency).</summary>
    public void RecordDependency(SqlQualifiedName name, TableCatalogEntry entry)
    {
        var dependency = new TableDependency(name, Fingerprint(entry));
        if (!_dependencies.Contains(dependency)) _dependencies.Add(dependency);
    }

    /// <summary>
    /// The mask for a column. Decision B-2: where the dialect cannot compute an HMAC in the database (<c>InDbHmac = false</c>)
    /// an HMAC mask degrades to Redact (fail closed). Gateway-side HMAC is rejected for WebSQL because user SQL could aggregate
    /// or sort the raw value. The key parameters of the degraded mask are dropped, so the key never reaches the statement.
    /// </summary>
    public MaskSpec GetMaskSpec(TableIdentity table, string column)
    {
        var spec = Masks.GetMask(table, column);
        if (spec.Kind != MaskKind.Hmac || Capabilities.InDbHmac)
        {
            return spec;
        }

        string name = "__mask_redact_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(column)))[..8].ToLowerInvariant();
        var constant = new PolicyParameterExpression(name, SqlParameterType.String, ParameterOrigin.Mask);
        return new MaskSpec(
            MaskKind.Redact,
            new MaskArguments(Constant: constant),
            new Dictionary<string, PolicyValue> { [name] = new PolicyValue("[REDACTED]", SqlParameterType.String) }
                .ToFrozenDictionary(StringComparer.Ordinal));
    }

    /// <summary>Fingerprint of everything that decides how <paramref name="entry"/> is injected: catalog entry, policy, masks.</summary>
    public string Fingerprint(TableCatalogEntry entry)
    {
        var id = entry.Identity;
        bool applies = RowFilters.ShouldApplyPolicy(id);
        string predicate = applies ? RowFilters.GetPredicate(id).Fingerprint : "-";
        var masks = entry.Columns
            .Where(c => Masks.HasMask(id, c.Name))
            .Select(c =>
            {
                var spec = GetMaskSpec(id, c.Name);
                return $"{c.Name}:{spec.Kind}:{AstReflection.FingerprintMemoized(spec.Arguments)}";
            })
            .ToList();
        return AstReflection.Fingerprint(new object[]
        {
            AstReflection.FingerprintMemoized(entry), applies, predicate, masks.Count == 0 ? "-" : AstReflection.Fingerprint(masks)
        });
    }

    /// <summary>
    /// Adds the values (tenant, policy, mask) that the current providers hold for <paramref name="entry"/>. A cached template is
    /// value-free; this rebuilds the values of the current request on a hit.
    /// </summary>
    public void AddTableValues(TableCatalogEntry entry)
    {
        var id = entry.Identity;
        if (entry.TenantColumn != null)
        {
            AddValue(Tenant.ParameterName, new PolicyValue(Tenant.Value, Tenant.Type));
        }

        if (RowFilters.ShouldApplyPolicy(id))
        {
            AddValues(RowFilters.GetPredicate(id).Parameters);
        }

        foreach (var column in entry.Columns)
        {
            if (Masks.HasMask(id, column.Name))
            {
                AddValues(GetMaskSpec(id, column.Name).Parameters);
            }
        }
    }

    /// <summary>
    /// A verifier whose expectations come from the providers, independent of the injector (plan 3.2): every table needs its
    /// tenant predicate (when it has a tenant column) and its policy predicate (when the provider says one applies).
    /// </summary>
    public SecurityCoverageVerifier CreateVerifier(TargetSqlDialect dialect = TargetSqlDialect.SqlServer, int maxSecuredTableReferences = 256) =>
        new(name => RequirementOf(name), dialect, maxSecuredTableReferences);

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
        var columns = entry.Columns.Select(c => c.Name).ToImmutableHashSet(StringComparer.Ordinal);
        return new TableCoverageRequirement(id.ToString(), id.Schema, id.Table, root, tenant, masked, id.Catalog, columns);
    }
}
