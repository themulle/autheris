namespace TrinoSqlEngine.Tests.TimeZones;

using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

/// <summary>Compile helpers for the time zone tests (REQ-SQL-130): a tenant-scoped <c>dbo.Events</c> table with typed timestamp columns per dialect.</summary>
internal static class TimeZoneTestSupport
{
    public static readonly TableIdentity Events = new("dbo", "Events");
    public static readonly TableIdentity Other = new("dbo", "Other");

    public static readonly TargetSqlDialect[] Dialects = [TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb];

    public static IEnumerable<object[]> DialectData() => Dialects.Select(d => new object[] { d });

    private static readonly FastSqlEngine Engine = new();

    /// <summary>Id, TenantId, OccurredAt (plain), OccurredAtTz (offset), OccurredOn (date), Label (text), Version (SQL Server rowversion spelling).</summary>
    public static InMemoryTableCatalog Catalog(TargetSqlDialect dialect)
    {
        var (i, t, plain, offset, date, text) = dialect == TargetSqlDialect.SqlServer
            ? ("int", "nvarchar(64)", "datetime2(3)", "datetimeoffset(7)", "date", "nvarchar(100)")
            : ("integer", "varchar", "timestamp", "timestamptz", "date", "varchar");
        var columns = ImmutableArray.Create(
            new CatalogColumn("Id", i), new CatalogColumn("TenantId", t), new CatalogColumn("OccurredAt", plain), new CatalogColumn("OccurredAtTz", offset),
            new CatalogColumn("OccurredOn", date), new CatalogColumn("Label", text), new CatalogColumn("Version", dialect == TargetSqlDialect.SqlServer ? "timestamp" : plain), new CatalogColumn("UpdatedAt", plain));
        return new InMemoryTableCatalog(
        [
            new TableCatalogEntry(Events, columns, "TenantId", 1),
            new TableCatalogEntry(Other, columns, "TenantId", 1)
        ], "dbo");
    }

    public static CompileRequest Request(
        TargetSqlDialect dialect,
        bool available = false,
        IColumnMaskProvider? masks = null,
        ClientListShapes? lists = null,
        StatementPermissions statements = StatementPermissions.ReadOnly,
        IReadOnlySet<string>? tablesWithMaskedColumns = null)
    {
        var filters = new DictPolicyProvider();
        filters.NoPolicy.Add(Events);
        filters.NoPolicy.Add(Other);
        return new CompileRequest
        {
            TargetDialect = dialect,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = statements,
            ClientLists = lists ?? ClientListShapes.Empty,
            TimeZoneConversionAvailable = available,
            Policy = new GovernancePolicy
            {
                RowFilters = filters,
                Masks = masks ?? new DictMaskProvider(),
                TablesWithMaskedColumns = tablesWithMaskedColumns ?? System.Collections.Frozen.FrozenSet<string>.Empty,
                Catalog = Catalog(dialect),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    public static CompiledSql Compile(TargetSqlDialect dialect, string sql, bool available = true, IColumnMaskProvider? masks = null, ClientListShapes? lists = null,
        StatementPermissions statements = StatementPermissions.ReadOnly, IReadOnlySet<string>? tablesWithMaskedColumns = null) =>
        Engine.Compile(sql.AsMemory(), Request(dialect, available, masks, lists, statements, tablesWithMaskedColumns), CancellationToken.None);

    public const StatementPermissions AllDml = StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge;

    /// <summary>The error code of the refusal, or fails the test (naming the SQL) when the statement compiles or throws something else.</summary>
    public static string Rejected(TargetSqlDialect dialect, string sql, bool available = true, IColumnMaskProvider? masks = null,
        StatementPermissions statements = StatementPermissions.ReadOnly, IReadOnlySet<string>? tablesWithMaskedColumns = null, ClientListShapes? lists = null)
    {
        var failure = Record.Exception(() => Compile(dialect, sql, available, masks, lists, statements, tablesWithMaskedColumns));
        if (failure is not SqlTimeZoneException ex)
        {
            throw new Xunit.Sdk.XunitException($"Expected SqlTimeZoneException for [{dialect}] {sql}; got {failure?.GetType().Name ?? "no exception"}: {failure?.Message}");
        }

        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(ex.Code), ex.Message);
        return ex.Code;
    }
}

/// <summary>Rewrites the string literal <c>from</c> to <c>to</c> in a hand-built tree (values a SQL text cannot carry).</summary>
internal sealed class ZoneLiteralReplacer(string from, string to) : TrinoSqlEngine.Ast.Visitors.SqlAstRewriter
{
    public override TrinoSqlEngine.Ast.Nodes.SqlNode VisitLiteralExpression(TrinoSqlEngine.Ast.Nodes.LiteralExpression node) =>
        node.Value is string s && s == from ? node with { Value = to } : node;
}

/// <summary>Replaces the name of every call of convert_timezone (hand-built tree).</summary>
internal sealed class FunctionNameReplacer(TrinoSqlEngine.Ast.Nodes.SqlQualifiedName name) : TrinoSqlEngine.Ast.Visitors.SqlAstRewriter
{
    public override TrinoSqlEngine.Ast.Nodes.SqlNode VisitFunctionCallExpression(TrinoSqlEngine.Ast.Nodes.FunctionCallExpression node)
    {
        var visited = (TrinoSqlEngine.Ast.Nodes.FunctionCallExpression)base.VisitFunctionCallExpression(node);
        return string.Equals(visited.Name.SimpleName, "convert_timezone", StringComparison.OrdinalIgnoreCase) ? visited with { Name = name } : visited;
    }
}

/// <summary>Direct access to the internal validator with the production-shaped options of a compile request.</summary>
internal static class TimeZoneConversionTestApi
{
    public static void ValidateGoverned(TargetSqlDialect dialect, TrinoSqlEngine.Ast.Nodes.SqlStatement statement, bool available = true, TrinoSqlEngine.Governance.IColumnMaskProvider? masks = null)
    {
        var request = TimeZoneTestSupport.Request(dialect, available);
        var caps = TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(dialect);
        new TrinoSqlEngine.Ast.Visitors.TimeZoneCallValidator(request.Policy.Catalog, new TrinoSqlEngine.Ast.Visitors.TimeZoneValidationOptions(
            dialect, caps.SupportsTimeZoneConversion, available, masks ?? request.Policy.Masks, request.Policy.TablesWithMaskedColumns, request.ClientLists, Federated: false)).Validate(statement);
    }
}

/// <summary>Replaces the first zone argument of every call of convert_timezone by a parameter reference (hand-built tree).</summary>
internal sealed class ZoneParameterReplacer(TrinoSqlEngine.Ast.Nodes.ParameterReference parameter) : TrinoSqlEngine.Ast.Visitors.SqlAstRewriter
{
    public override TrinoSqlEngine.Ast.Nodes.SqlNode VisitFunctionCallExpression(TrinoSqlEngine.Ast.Nodes.FunctionCallExpression node)
    {
        if (!string.Equals(node.Name.SimpleName, "convert_timezone", StringComparison.OrdinalIgnoreCase)) return base.VisitFunctionCallExpression(node);
        var args = node.Arguments.ToList();
        args[0] = parameter;
        return node with { Arguments = args };
    }
}
