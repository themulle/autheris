using System.Collections.Frozen;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;
using static TrinoSqlEngine.Ast.Nodes.BinaryOperator;

namespace TrinoSqlEngine.Tests.Compiler;

[Trait("Category", "SqlGate")]
[Trait("Category", "Security")]
public sealed class PerCellColumnSecurityTests
{
    private static readonly TableIdentity OrdersId = new("dbo", "orders");
    private static readonly TableIdentity AclId = new("conf", "acl");

    private static ITableCatalog Catalog() => new InMemoryTableCatalog(
    [
        new TableCatalogEntry(OrdersId, ImmutableArray.Create(
            new CatalogColumn("id", "int"),
            new CatalogColumn("tenant_id", "varchar(64)"),
            new CatalogColumn("region", "varchar(32)"),
            new CatalogColumn("email", "varchar(128)")),
            "tenant_id",
            1L),
        new TableCatalogEntry(AclId, ImmutableArray.Create(
            new CatalogColumn("id", "int"),
            new CatalogColumn("tenant_id", "varchar(64)"),
            new CatalogColumn("tag", "varchar(32)")),
            "tenant_id",
            1L)
    ], "dbo");

    private static PolicyPredicate EqFilter(string col, string paramName, string val) =>
        PolicyPredicate.Create(
            new BinaryExpression(
                new ColumnReference(new SqlQualifiedName([new SqlIdentifier(col, true)])),
                Equal,
                new PolicyParameterExpression(paramName, SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { [paramName] = new(val, SqlParameterType.String) });

    private static PerCellTablePolicy TwoSigPolicy() => new(
        [new PerCellSignatureSpec(EqFilter("region", "p_eu", "EU")), new PerCellSignatureSpec(EqFilter("region", "p_us", "US"))],
        new Dictionary<string, PerCellColumnRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new(ImmutableArray.Create(0), ImmutableArray.Create(1))
        });

    [Fact]
    public void Per_cell_visitor_and_verifier_accept_intact_tree_and_reject_tampered_ast()
    {
        var catalog = Catalog();
        var perCell = new StaticPerCellProvider(OrdersId, TwoSigPolicy());
        var policy = new GovernancePolicy
        {
            Tenant = new TenantBinding("__autheris_tenant", "tenant-a", SqlParameterType.String),
            Catalog = catalog,
            RowFilters = new StaticRowFilterProvider(OrdersId, EqFilter("region", "p_union", "EU").Or(EqFilter("region", "p_union2", "US"))),
            Masks = new StaticMaskProvider(OrdersId, "email"),
            PerCellColumns = perCell
        };

        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = policy
        };

        var engine = new FastSqlEngine();
        var compiled = engine.Compile("SELECT id, email FROM dbo.orders".AsMemory(), request, CancellationToken.None);
        Assert.Contains("CASE WHEN", compiled.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ELSE NULL END", compiled.Sql, StringComparison.OrdinalIgnoreCase);

        // Verify SecurityCoverageVerifier rejects a tampered CASE WHEN condition on a secured AST.
        var caps = new DialectCapabilityTable().Get(TargetSqlDialect.PostgreSql);
        var typed = new TypedPolicyContext(policy.Catalog, policy.RowFilters, policy.Masks, policy.Tenant, caps)
        {
            PerCellColumns = policy.PerCellColumns
        };
        var verifier = typed.CreateVerifier(TargetSqlDialect.PostgreSql);

        var okCase = new CaseExpression(
            null,
            [
                new WhenClause(new BinaryExpression(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("region", true)])), Equal, new PolicyParameterExpression("p_eu", SqlParameterType.String)),
                    new ColumnReference(new SqlQualifiedName([new SqlIdentifier("email", true)]))),
                new WhenClause(new BinaryExpression(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("region", true)])), Equal, new PolicyParameterExpression("p_us", SqlParameterType.String)),
                    new MaskExpression(MaskKind.Redact, new ColumnReference(new SqlQualifiedName([new SqlIdentifier("email", true)])), new MaskArguments(Constant: new PolicyParameterExpression("__mask_c", SqlParameterType.String)), "varchar(128)"))
            ],
            new LiteralExpression(null, LiteralType.Null));

        var tamperedCase = okCase with
        {
            WhenClauses =
            [
                new WhenClause(new BinaryExpression(new LiteralExpression(1L, LiteralType.Integer), Equal, new LiteralExpression(1L, LiteralType.Integer)),
                    new ColumnReference(new SqlQualifiedName([new SqlIdentifier("email", true)]))),
                okCase.WhenClauses[1]
            ]
        };

        var securedAst = WrapInSecuredDerivedTable(tamperedCase);
        Assert.Throws<SecurityCoverageException>(() => verifier.Verify(securedAst, CancellationToken.None));
    }

    private static SelectStatement WrapInSecuredDerivedTable(Expression emailExpr)
    {
        var tenantPred = new SecurityPredicateExpression(
            new BinaryExpression(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("tenant_id", true)])), Equal, new PolicyParameterExpression("__autheris_tenant", SqlParameterType.String)),
            new SecurityPredicateId("dbo.orders", 0),
            SecurityScope.Root);

        var inner = new QuerySpecification(
            false,
            [
                new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("id", true)])), new SqlIdentifier("id", true)),
                new ColumnSelectItem(emailExpr, new SqlIdentifier("email", true))
            ],
            new NamedTableSource(OrdersId.ToQualifiedName(), null),
            tenantPred,
            null,
            null);

        var derived = new SubqueryTableSource(new SelectStatement(null, inner, null, null), new SqlIdentifier("orders", true));
        return new SelectStatement(
            null,
            new QuerySpecification(
                false,
                [
                    new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("id", true)])), new SqlIdentifier("id", true)),
                    new ColumnSelectItem(new ColumnReference(new SqlQualifiedName([new SqlIdentifier("email", true)])), new SqlIdentifier("email", true))
                ],
                derived,
                null,
                null,
                null),
            null,
            null);
    }

    private sealed class StaticPerCellProvider(TableIdentity target, PerCellTablePolicy policy) : IPerCellColumnProvider
    {
        public PerCellTablePolicy? GetPolicy(TableIdentity table) => table == target ? policy : null;
    }

    private sealed class StaticRowFilterProvider(TableIdentity target, PolicyPredicate predicate) : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => table == target;
        public PolicyPredicate GetPredicate(TableIdentity table) => table == target ? predicate : PolicyPredicate.DenyAll;
    }

    private sealed class StaticMaskProvider(TableIdentity target, string col) : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => table == target && string.Equals(column, col, StringComparison.OrdinalIgnoreCase);
        public MaskSpec GetMask(TableIdentity table, string column) => new(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("__mask_c", SqlParameterType.String)),
            new Dictionary<string, PolicyValue> { ["__mask_c"] = new("***", SqlParameterType.String) }.ToFrozenDictionary());
    }
}
