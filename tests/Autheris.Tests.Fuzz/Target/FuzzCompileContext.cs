using System.Collections.Frozen;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Fuzz.Target;

/// <summary>
/// In-memory compile context of the fuzz target (SEC-SIM-20: no file, network or process I/O). Tables mirror the names of the
/// compiler gate corpus (<c>orders</c>, <c>customers</c>, ...), so seeds derived from <c>dql.sql</c> reach the governance code:
/// <c>orders</c> has a row filter (<c>region = 'EU'</c>) and a masked <c>email</c> column; every table has a tenant column.
/// </summary>
public static class FuzzCompileContext
{
    public const string Schema = "main";
    public const string TenantColumn = "tenant_id";

    private static readonly (string Table, string[] Columns)[] Tables =
    [
        ("orders", ["id", TenantColumn, "customer_id", "amount", "status", "region", "dept", "created_at", "email", "note"]),
        ("customers", ["id", TenantColumn, "name", "email", "region"]),
        ("entitlements", ["id", TenantColumn, "order_id", "note"]),
        ("employees", ["id", TenantColumn, "name", "dept", "salary"]),
        ("users", ["id", TenantColumn, "name", "status", "email"]),
    ];

    private static readonly FuzzPolicies Policies = new();

    public static CompileRequest Request { get; } = new()
    {
        TargetDialect = TargetSqlDialect.PostgreSql,
        TokenGuards = SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.PostgreSql),
        Statements = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge,
        AllowedFunctions = SqlFunctionAllowlists.Build(TargetSqlDialect.PostgreSql, null),
        AllowRecursiveCte = true,
        Policy = new GovernancePolicy
        {
            RowFilters = Policies,
            Masks = Policies,
            Catalog = new InMemoryTableCatalog(
                Tables.Select(t => new TableCatalogEntry(
                    new TableIdentity(Schema, t.Table),
                    [.. t.Columns.Select(c => new CatalogColumn(c, ColumnType(c)))],
                    TenantColumn,
                    1)),
                Schema),
            Tenant = new TenantBinding("__autheris_tenant", "A", SqlParameterType.String),
            TablesWithConsentRowFilter = new HashSet<string>(["orders"], StringComparer.OrdinalIgnoreCase).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
            TablesWithMaskedColumns = new HashSet<string>(["orders", "customers", "users"], StringComparer.OrdinalIgnoreCase).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
        },
    };

    private static string ColumnType(string column) => column switch
    {
        "id" or "customer_id" or "order_id" => "bigint",
        "amount" or "salary" => "numeric(12,2)",
        "created_at" => "timestamp",
        _ => "text",
    };

    private sealed class FuzzPolicies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => string.Equals(table.Table, "orders", StringComparison.Ordinal);

        public PolicyPredicate GetPredicate(TableIdentity table) => ShouldApplyPolicy(table)
            ? PolicyPredicate.Create(
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName([new SqlIdentifier("region", true)])),
                    BinaryOperator.Equal,
                    new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
                new Dictionary<string, PolicyValue> { ["__pol_region"] = new("EU", SqlParameterType.String) })
            : PolicyPredicate.DenyAll;

        public bool HasMask(TableIdentity table, string column) =>
            string.Equals(column, "email", StringComparison.OrdinalIgnoreCase)
            && (table.Table is "orders" or "customers" or "users");

        public MaskSpec GetMask(TableIdentity table, string column) => new(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("__mask_const", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["__mask_const"] = new("***", SqlParameterType.String) }.ToFrozenDictionary());
    }
}
