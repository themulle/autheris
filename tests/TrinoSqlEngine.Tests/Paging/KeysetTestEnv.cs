using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Tests.Paging;

/// <summary>A small catalog + policy environment for the keyset compile tests.</summary>
internal sealed class KeysetTestEnv
{
    private readonly List<TableCatalogEntry> _tables = [];
    private readonly Policies _policies = new();

    public KeysetTestEnv(TargetSqlDialect dialect, string schema = "crm")
    {
        Dialect = dialect;
        Schema = schema;
    }

    public TargetSqlDialect Dialect { get; }

    public string Schema { get; }

    public FastSqlEngine Engine { get; } = new();

    public KeysetTestEnv Table(string name, string[] required, params string[] columns)
    {
        var cols = ImmutableArray.CreateBuilder<CatalogColumn>();
        foreach (var c in columns)
        {
            var parts = c.Split(':');
            cols.Add(new CatalogColumn(parts[0], parts.Length > 1 ? parts[1] : "text"));
        }

        _tables.Add(new TableCatalogEntry(new TableIdentity(Schema, name), cols.ToImmutable(), null, [.. required], 1));
        return this;
    }

    public KeysetTestEnv Policy(string table, PolicyPredicate predicate)
    {
        _policies.Predicates[new TableIdentity(Schema, table)] = predicate;
        return this;
    }

    public KeysetTestEnv Mask(string table, string column)
    {
        _policies.Masks[(new TableIdentity(Schema, table), column.ToLowerInvariant())] =
            new MaskSpec(MaskKind.Nullify, new MaskArguments(), System.Collections.Frozen.FrozenDictionary<string, PolicyValue>.Empty);
        return this;
    }

    public CompileRequest Request(KeysetCandidate? keyset, KeysetSeek? seek = null, long enforcedMaxRows = 501) => new()
    {
        TargetDialect = Dialect,
        TokenGuards = SqlTokenSecurityOptions.Strict,
        AllowExperimentalDialect = true,
        EnforcedMaxRows = enforcedMaxRows,
        Keyset = keyset,
        KeysetSeek = seek,
        Policy = new GovernancePolicy
        {
            RowFilters = _policies,
            Masks = _policies,
            Catalog = new InMemoryTableCatalog(_tables, Schema),
            Tenant = new TenantBinding("__autheris_tenant", "t1", SqlParameterType.String)
        }
    };

    public CompiledSql Compile(string sql, KeysetCandidate? keyset, KeysetSeek? seek = null) =>
        Engine.Compile(sql.AsMemory(), Request(keyset, seek), CancellationToken.None);

    public static KeysetCandidate Key(params string[] columns) => new([.. columns]);

    public static KeysetSeek Seek(string hash, params object[] values) => new(hash, [.. values]);

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
