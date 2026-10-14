namespace Autheris.Tests.Unit.Sql;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Threading;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// The compile environment of the unit tests after the X1 cutover: one engine, an in-memory catalog and plain typed providers. It
/// replaces the former <c>RlsOptions</c> plus string provider setup; <see cref="FastSqlEngine.Compile"/> is the only governed path.
/// </summary>
internal sealed class TestCompileEnv
{
    private readonly List<TableCatalogEntry> _tables = [];
    private readonly Policies _policies = new();

    public TestCompileEnv(TargetSqlDialect dialect, string defaultSchema = "main")
    {
        Dialect = dialect;
        DefaultSchema = defaultSchema;
    }

    public TargetSqlDialect Dialect { get; }

    public string DefaultSchema { get; }

    public FastSqlEngine Engine { get; } = new();

    /// <summary>Adds a table; every column has the type <c>TEXT</c> unless it is written as <c>name:type</c>.</summary>
    public TestCompileEnv Table(string name, string? tenantColumn, params string[] columns)
    {
        var cols = ImmutableArray.CreateBuilder<CatalogColumn>();
        foreach (var c in columns)
        {
            var parts = c.Split(':');
            cols.Add(new CatalogColumn(parts[0], parts.Length > 1 ? parts[1] : "TEXT"));
        }

        _tables.Add(new TableCatalogEntry(new TableIdentity(DefaultSchema, name), cols.ToImmutable(), tenantColumn, 1));
        return this;
    }

    /// <summary>The table has a row policy (an admin predicate); without a call every table has none.</summary>
    public TestCompileEnv Policy(string table, PolicyPredicate predicate)
    {
        _policies.Predicates[new TableIdentity(DefaultSchema, table)] = predicate;
        return this;
    }

    public TestCompileEnv Mask(string table, string column, MaskSpec spec)
    {
        _policies.Masks[(new TableIdentity(DefaultSchema, table), column.ToLowerInvariant())] = spec;
        return this;
    }

    public CompileRequest Request(
        string tenant = "t1",
        StatementPermissions statements = StatementPermissions.ReadOnly,
        SqlTokenSecurityOptions? guards = null,
        IReadOnlySet<string>? consentTables = null) => new()
    {
        TargetDialect = Dialect,
        TokenGuards = guards ?? SqlTokenSecurityOptions.Strict,
        Statements = statements,
        AllowExperimentalDialect = true,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = new InMemoryTableCatalog(_tables, DefaultSchema),
            Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String),
            TablesWithConsentRowFilter = consentTables ?? new HashSet<string>()
        }
    };

    public CompiledSql Compile(
        string sql,
        string tenant = "t1",
        StatementPermissions statements = StatementPermissions.ReadOnly,
        SqlTokenSecurityOptions? guards = null) =>
        Engine.Compile(sql.AsMemory(), Request(tenant, statements, guards), CancellationToken.None);

    private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
    }
}
