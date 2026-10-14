namespace Autheris.Tests.Integration;

using System.Collections.Immutable;
using System.Data.Common;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>
/// The compile path of the integration tests that execute generated SQL directly on a container: a typed request with one tenant
/// binding over a one-table catalog (the shape the governed pipeline produces), compiled by <see cref="FastSqlEngine.Compile"/> and
/// bound by the dialect binder. The legacy string path that these tests used before the cutover no longer exists.
/// </summary>
internal static class SingleTableGovernedCompile
{
    private sealed class NoRowPolicy : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class NoMasks : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException("No masks.");
    }

    public static CompiledSql Compile(
        TargetSqlDialect dialect, string schema, string table, (string Name, string Type)[] columns, string tenantColumn, string tenantValue, string sql)
    {
        var catalog = new InMemoryTableCatalog(
            [new TableCatalogEntry(new TableIdentity(schema, table), [.. columns.Select(c => new CatalogColumn(c.Name, c.Type))], tenantColumn, 1)],
            schema);
        var request = new CompileRequest
        {
            TargetDialect = dialect,
            TokenGuards = SqlTokenSecurityOptions.ForDialect(dialect),
            Policy = new GovernancePolicy
            {
                RowFilters = new NoRowPolicy(),
                Masks = new NoMasks(),
                Catalog = catalog,
                Tenant = new TenantBinding("__autheris_tenant", tenantValue, SqlParameterType.String)
            }
        };
        return new FastSqlEngine().Compile(sql.AsMemory(), request, CancellationToken.None);
    }

    /// <summary>Creates the command of <paramref name="compiled"/> with every value bound through the dialect binder.</summary>
    public static DbCommand BoundCommand(DbConnection connection, CompiledSql compiled)
    {
        var command = connection.CreateCommand();
        CompiledSqlBinders.For(compiled.Dialect).Bind(command, compiled, new Dictionary<string, object?>());
        return command;
    }
}
