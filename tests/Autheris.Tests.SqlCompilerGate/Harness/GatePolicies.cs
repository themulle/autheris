namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Collections.Frozen;
using System.Collections.Immutable;
using Autheris.Tests.SqlCompilerGate.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Builds the legacy options and the AST compile request of one scenario from the same fixture model. The legacy options follow
/// the production assembly in <c>GovernedSqlRewriter</c> (string filters, catalog projection, table aliases, DML guards); the AST
/// request carries the typed equivalents (tenant binding, consent predicate, constant masks).
/// </summary>
public sealed class GatePolicies(GateDialect dialect, string defaultSchema, PolicyScenario scenario)
{
    private static readonly string[] MaskedColumns = ["email"];

    private GateDialect Dialect => dialect;

    private bool Consent => scenario == PolicyScenario.TenantConsentMask;

    private bool Masks => scenario == PolicyScenario.TenantConsentMask;

    private TargetSqlDialect Target => GateModel.ToTarget(dialect);

    private static string SimpleName(string name)
    {
        string simple = SqlIdentifierHelper.GetSimpleName(name).Trim('"', '`', '[', ']');
        return simple.ToLowerInvariant();
    }

    public RlsOptions LegacyOptions(bool isDml)
    {
        var allowed = SqlFunctionAllowlists.Build(Target, null);
        var masked = new HashSet<string>(GateModel.Tables.Where(t => t.Columns.Any(c => c.Masked)).Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var provider = new DefaultRlsPolicyProvider(
            defaultFilter: "1 = 0",
            predicate: _ => true,
            filterFunc: table => SimpleName(table) switch
            {
                "orders" when Consent => $"{GateModel.TenantColumn} = '{GateModel.TenantA}' AND {GateModel.ConsentColumn} = '{GateModel.ConsentValue}'",
                var t when GateModel.Tables.Any(x => x.Name == t) => $"{GateModel.TenantColumn} = '{GateModel.TenantA}'",
                _ => "1 = 0"
            })
        {
            FallbackToSimpleName = false
        };

        var maskProvider = new DefaultColumnMaskingPolicyProvider(
            hasMaskPredicate: (table, column) => Masks && masked.Contains(SimpleName(table)) && MaskedColumns.Contains(column, StringComparer.OrdinalIgnoreCase),
            maskExpressionProvider: (_, _) => "'***'")
        {
            FallbackToSimpleName = false
        };

        return new RlsOptions
        {
            AppendTableAlias = true,
            EnforceCatalogProjection = true,
            EnforceReadOnlyQueries = !isDml,
            TargetDialect = Target,
            RejectComments = true,
            RejectBackslashInStrings = true,
            RejectEscapedStringLiterals = true,
            RejectDollarQuoting = Target != TargetSqlDialect.PostgreSql,
            RejectBracketLexerDifferentials = Target is TargetSqlDialect.SqlServer or TargetSqlDialect.Sqlite,
            RejectNonAsciiIdentifiers = true,
            RejectDotsInQuotedIdentifiers = true,
            RejectTimeTravelQueries = true,
            RejectConsentFilteredInsert = true,
            RejectWholeRowReferencesInDml = true,
            TablesWithConsentRowFilter = Consent ? new HashSet<string>(["orders"], StringComparer.OrdinalIgnoreCase) : new(StringComparer.OrdinalIgnoreCase),
            TablesWithMaskedColumns = Masks ? masked : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ExpectedTenantValue = GateModel.TenantA,
            TenantColumnName = GateModel.TenantColumn,
            RequireTenantColumnInInsert = true,
            EnforceFunctionPolicy = true,
            AllowedFunctions = allowed,
            AllowInlineFunctionDefinitions = false,
            RejectMaskedColumnsInDml = true,
            RejectUnfilteredDml = true,
            PolicyProvider = provider,
            TableColumnsProvider = table => GateModel.Tables.FirstOrDefault(t => t.Name == SimpleName(table))?.Columns.Select(c => c.Name).ToList(),
            ColumnMaskingProvider = maskProvider,
            PolicyFiltersAreTargetDialectSql = true,
            RewriterEngine = "LegacyTokenStream"
        };
    }

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

    public CompileRequest AstRequest(RlsOptions legacyOptions, StatementPermissions permissions)
    {
        var typed = new TypedPolicies(this);
        return new CompileRequest
        {
            TargetDialect = Target,
            TokenGuards = SqlTokenSecurityOptions.FromRlsOptions(legacyOptions),
            Statements = permissions,
            AllowedFunctions = legacyOptions.AllowedFunctions,
            Policy = new GovernancePolicy
            {
                RowFilters = typed,
                Masks = typed,
                Catalog = Catalog(),
                Tenant = new TenantBinding("__autheris_tenant", GateModel.TenantA, SqlParameterType.String),
                TablesWithConsentRowFilter = legacyOptions.TablesWithConsentRowFilter.ToFrozenSet(StringComparer.OrdinalIgnoreCase),
                TablesWithMaskedColumns = legacyOptions.TablesWithMaskedColumns.ToFrozenSet(StringComparer.OrdinalIgnoreCase)
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
