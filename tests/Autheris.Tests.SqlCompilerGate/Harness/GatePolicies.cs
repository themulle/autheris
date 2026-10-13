namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Collections.Frozen;
using System.Collections.Immutable;
using Autheris.Tests.SqlCompilerGate.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Builds the AST compile request of one scenario from the fixture model: tenant binding, consent predicate, constant masks, the
/// catalog and the token guards of the dialect. (The legacy options that used to be built here were deleted with the legacy
/// rewriter; its outcomes are frozen in <c>golden-results</c>, see <see cref="FrozenLegacy"/>.)
/// </summary>
public sealed class GatePolicies(GateDialect dialect, string defaultSchema, PolicyScenario scenario)
{
    private GateDialect Dialect => dialect;

    private bool Consent => scenario == PolicyScenario.TenantConsentMask;

    private bool Masks => scenario == PolicyScenario.TenantConsentMask;

    private TargetSqlDialect Target => GateModel.ToTarget(dialect);

    private TableIdentity IdentityOf(TableDef table) => new(defaultSchema, GateModel.Canon(dialect, table.Name));

    public InMemoryTableCatalog Catalog() => new(
        GateModel.Tables.Select(t => new TableCatalogEntry(
            IdentityOf(t),
            [.. t.Columns.Select(c => new CatalogColumn(GateModel.Canon(dialect, c.Name), GateModel.TypeOf(dialect, c)))],
            GateModel.Canon(dialect, GateModel.TenantColumn),
            1)),
        defaultSchema);

    private sealed class TypedPolicies(GatePolicies owner) : IPolicyPredicateProvider, IColumnMaskProvider
    {
        private static ColumnReference Col(string name) => new(new SqlQualifiedName([new SqlIdentifier(name, true)]));

        public bool ShouldApplyPolicy(TableIdentity table) => owner.Consent && IsOrders(table);

        public PolicyPredicate GetPredicate(TableIdentity table) => ShouldApplyPolicy(table)
            ? PolicyPredicate.Create(
                new BinaryExpression(Col(GateModel.Canon(owner.Dialect, GateModel.ConsentColumn)), BinaryOperator.Equal,
                    new PolicyParameterExpression("__pol_region", SqlParameterType.String)),
                new Dictionary<string, PolicyValue> { ["__pol_region"] = new(GateModel.ConsentValue, SqlParameterType.String) })
            : PolicyPredicate.DenyAll;

        private bool IsOrders(TableIdentity table) =>
            string.Equals(table.Table, GateModel.Canon(owner.Dialect, "orders"), StringComparison.Ordinal);

        public bool HasMask(TableIdentity table, string column) =>
            owner.Masks && GateModel.Tables.Any(t => string.Equals(GateModel.Canon(owner.Dialect, t.Name), table.Table, StringComparison.Ordinal)
                                                     && t.Columns.Any(c => c.Masked && string.Equals(GateModel.Canon(owner.Dialect, c.Name), column, StringComparison.OrdinalIgnoreCase)));

        public MaskSpec GetMask(TableIdentity table, string column) => new(
            MaskKind.Redact,
            new MaskArguments(Constant: new PolicyParameterExpression("__mask_const", SqlParameterType.String, ParameterOrigin.Mask)),
            new Dictionary<string, PolicyValue> { ["__mask_const"] = new(GateModel.MaskValue, SqlParameterType.String) }.ToFrozenDictionary());
    }

    public CompileRequest AstRequest(StatementPermissions permissions)
    {
        var typed = new TypedPolicies(this);
        var masked = new HashSet<string>(GateModel.Tables.Where(t => t.Columns.Any(c => c.Masked)).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        return new CompileRequest
        {
            TargetDialect = Target,
            // The token guards of the legacy options of this gate: PostgreSQL keeps dollar quoting out of the rejected set.
            TokenGuards = SqlTokenSecurityOptions.ForDialect(Target) with { RejectDollarQuoting = Target != TargetSqlDialect.PostgreSql },
            Statements = permissions,
            AllowedFunctions = SqlFunctionAllowlists.Build(Target, null),
            Policy = new GovernancePolicy
            {
                RowFilters = typed,
                Masks = typed,
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", GateModel.TenantA, SqlParameterType.String),
                TablesWithConsentRowFilter = (Consent ? new HashSet<string>(["orders"], StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                TablesWithMaskedColumns = (Masks ? masked : new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToFrozenSet(StringComparer.OrdinalIgnoreCase)
            }
        };
    }

    public static StatementPermissions PermissionsOf(string statementClass) => statementClass switch
    {
        "select" => StatementPermissions.ReadOnly,
        "insert" => StatementPermissions.Insert,
        "update" => StatementPermissions.Update,
        "delete" => StatementPermissions.Delete,
        "merge" => StatementPermissions.Merge,
        _ => throw new ArgumentOutOfRangeException(nameof(statementClass))
    };
}
