namespace TrinoSqlEngine.Tests.TimeZones;

using System.Collections.Frozen;
using System.Reflection;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;
using static TrinoSqlEngine.Tests.TimeZones.TimeZoneTestSupport;

/// <summary>SEC-130-01 (masked timestamp), SEC-130-05 (list zones), SEC-130-08 (caps), SEC-130-12 (every position): ST-130-9, ST-130-11 (compile part), ST-130-5, ST-130-17.</summary>
[Trait("Category", "Security")]
public sealed class TimeZoneValidatorCoverageTests
{
    private static string Bad(string alias) => $"convert_timezone('Mars/Base', 'UTC', {alias}.OccurredAt)";
    private const string Ts = "TIMESTAMP '2026-01-01 00:00:00'";

    public static IEnumerable<object[]> Positions()
    {
        string e = Bad("e");
        string o = Bad("o");
        var positions = new (string Name, string Sql, StatementPermissions Statements)[]
        {
            ("projection", $"SELECT {e} FROM dbo.Events e", StatementPermissions.ReadOnly),
            ("where", $"SELECT e.Id FROM dbo.Events e WHERE {e} > {Ts}", StatementPermissions.ReadOnly),
            ("having", $"SELECT e.Id FROM dbo.Events e GROUP BY e.Id HAVING max({e}) > {Ts}", StatementPermissions.ReadOnly),
            ("order by", $"SELECT e.Id FROM dbo.Events e ORDER BY {e}", StatementPermissions.ReadOnly),
            ("group by", $"SELECT count(*) FROM dbo.Events e GROUP BY {e}", StatementPermissions.ReadOnly),
            ("window partition by", $"SELECT row_number() OVER (PARTITION BY {e}) FROM dbo.Events e", StatementPermissions.ReadOnly),
            ("window order by", $"SELECT row_number() OVER (ORDER BY {e}) FROM dbo.Events e", StatementPermissions.ReadOnly),
            ("case", $"SELECT CASE WHEN {e} > {Ts} THEN 1 ELSE 0 END FROM dbo.Events e", StatementPermissions.ReadOnly),
            ("aggregate argument", $"SELECT max({e}) FROM dbo.Events e", StatementPermissions.ReadOnly),
            ("aggregate filter", $"SELECT count(*) FILTER (WHERE {e} > {Ts}) FROM dbo.Events e", StatementPermissions.ReadOnly),
            ("cte", $"WITH c AS (SELECT {e} AS x FROM dbo.Events e) SELECT c.x FROM c", StatementPermissions.ReadOnly),
            ("derived table", $"SELECT d.x FROM (SELECT {e} AS x FROM dbo.Events e) d", StatementPermissions.ReadOnly),
            ("scalar subquery", $"SELECT o.Id, (SELECT max({e}) FROM dbo.Events e) FROM dbo.Other o", StatementPermissions.ReadOnly),
            ("in subquery", $"SELECT o.Id FROM dbo.Other o WHERE o.Id IN (SELECT e.Id FROM dbo.Events e WHERE {e} > {Ts})", StatementPermissions.ReadOnly),
            ("exists", $"SELECT o.Id FROM dbo.Other o WHERE EXISTS (SELECT 1 FROM dbo.Events e WHERE {e} > {Ts})", StatementPermissions.ReadOnly),
            ("set operation left arm", $"SELECT {e} FROM dbo.Events e UNION ALL SELECT o.OccurredAt FROM dbo.Other o", StatementPermissions.ReadOnly),
            ("set operation right arm", $"SELECT o.OccurredAt FROM dbo.Other o UNION ALL SELECT {e} FROM dbo.Events e", StatementPermissions.ReadOnly),
            ("join condition", $"SELECT e.Id FROM dbo.Events e JOIN dbo.Other o ON {e} = o.OccurredAt", StatementPermissions.ReadOnly),
            ("insert select", $"INSERT INTO dbo.Events (Id, TenantId, OccurredAt) SELECT o.Id, o.TenantId, {o} FROM dbo.Other o", StatementPermissions.Insert),
            ("update set", "UPDATE dbo.Events SET OccurredAt = convert_timezone('Mars/Base', 'UTC', OccurredAt) WHERE Id = 1", StatementPermissions.Update),
            ("update where", $"UPDATE dbo.Events SET Label = 'x' WHERE convert_timezone('Mars/Base', 'UTC', OccurredAt) > {Ts}", StatementPermissions.Update),
            ("delete where", $"DELETE FROM dbo.Events WHERE convert_timezone('Mars/Base', 'UTC', OccurredAt) > {Ts}", StatementPermissions.Delete),
            ("merge update", $"MERGE INTO dbo.Events t USING dbo.Other s ON t.Id = s.Id WHEN MATCHED THEN UPDATE SET OccurredAt = {Bad("s")}", StatementPermissions.Merge),
            ("merge condition", $"MERGE INTO dbo.Events t USING dbo.Other s ON {Bad("s")} = t.OccurredAt WHEN MATCHED THEN UPDATE SET Label = 'x'", StatementPermissions.Merge),
        };

        foreach (var dialect in Dialects)
        {
            foreach (var (name, sql, statements) in positions)
            {
                yield return [dialect, name, sql, statements];
            }
        }
    }

    [Theory, MemberData(nameof(Positions))]
    public void ST_130_9_AnInvalidZoneIsRejectedInEveryPosition(TargetSqlDialect dialect, string position, string sql, StatementPermissions statements)
    {
        _ = position;
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneInvalid, Rejected(dialect, sql, statements: statements));
    }

    [Fact]
    public void ST_130_9_EveryNodeTypeThatHostsChildNodesIsKnown()
    {
        // The validator is a SqlAstRewriter: it reaches the children of exactly the node types the rewriter dispatches. A new node type that hosts
        // other nodes fails this list until the position table above is extended and the validator is checked for it.
        var hosting = typeof(SqlNode).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(SqlNode).IsAssignableFrom(t) && t.Namespace == typeof(SqlNode).Namespace)
            .Where(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(p => HostsNodes(p.PropertyType)))
            .Select(t => t.Name).Order(StringComparer.Ordinal).ToList();

        string[] expected =
        [
            "ArrayConstructorExpression", "BetweenExpression", "BinaryExpression", "CaseExpression", "CastExpression", "ColumnReference", "ColumnSelectItem", "CommonTableExpression",
            "DateFunctionExpression", "DeleteStatement", "ExistsExpression", "ExtractExpression", "FunctionCallExpression", "GroupByClause", "GroupingOperationExpression",
            "InListExpression", "InSubqueryExpression", "InsertStatement", "IsDistinctFromExpression", "JoinedTableSource", "LateralTableSource", "LikeExpression",
            "MaskExpression", "MergeDeleteClause", "MergeInsertClause", "MergeStatement", "MergeUpdateClause", "NamedTableSource", "OnJoinCondition", "OrderByClause",
            "OrderByElement", "PaginationClause", "ParenthesizedExpression", "PositionExpression", "QuantifiedComparisonExpression", "QuerySpecification", "RowValueExpression",
            "ScalarSubqueryExpression", "SecurityPredicateExpression", "SelectStatement", "SetOperationQuery", "SqlQualifiedName", "SubqueryTableSource", "SubscriptExpression",
            "SubstringExpression", "TableQueryBody", "TrimExpression", "UnaryExpression", "UpdateAssignment", "UpdateStatement", "UsingJoinCondition", "ValuesQueryBody", "WhenClause", "WildcardSelectItem",
            "WindowFrame", "WindowSpecification", "WithClause"
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), hosting);
    }

    private static bool HostsNodes(Type type) =>
        typeof(SqlNode).IsAssignableFrom(type) || (type.IsGenericType && type.GetGenericArguments().Any(a => typeof(SqlNode).IsAssignableFrom(a)));

    // ---- BC-130-1 masks ----

    private static DictMaskProvider MaskOn(MaskKind kind, string column = "occurredat")
    {
        var masks = new DictMaskProvider();
        masks.Masks[(Events, column)] = new MaskSpec(kind, new MaskArguments(), FrozenDictionary<string, PolicyValue>.Empty);
        return masks;
    }

    public static IEnumerable<object[]> MaskKindsPerDialect() =>
        Dialects.SelectMany(d => Enum.GetValues<MaskKind>().Select(k => new object[] { d, k }));

    [Theory, MemberData(nameof(MaskKindsPerDialect))]
    public void ST_130_11_BC_130_1_AMaskedColumnIsNotAcceptedAsTheTimestamp(TargetSqlDialect dialect, MaskKind kind)
    {
        var masks = MaskOn(kind);
        string call = "convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt)";
        string[] statements =
        [
            $"SELECT {call} FROM dbo.Events e",
            $"SELECT e.Id FROM dbo.Events e WHERE {call} > {Ts}",
            $"SELECT e.Id FROM dbo.Events e ORDER BY {call}",
            $"SELECT count(*) FROM dbo.Events e GROUP BY {call}",
            $"SELECT e.Id FROM dbo.Events e GROUP BY e.Id HAVING max({call}) > {Ts}",
            "SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(e.OccurredAt AS timestamp)) FROM dbo.Events e",
            "SELECT convert_timezone('UTC', 'Europe/Berlin', convert_timezone('UTC', 'Europe/Paris', e.OccurredAt)) FROM dbo.Events e",
            "SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(concat(e.OccurredAt, 'x') AS timestamp)) FROM dbo.Events e"
        ];
        foreach (string sql in statements)
        {
            Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, sql, masks: masks));
        }
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_11_TheRefusalIsIdenticalForEveryMaskKindAndAnUnmaskedColumnIsAccepted(TargetSqlDialect dialect)
    {
        string sql = "SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e";
        var responses = Enum.GetValues<MaskKind>().Select(kind =>
        {
            var ex = Assert.Throws<SqlTimeZoneException>(() => Compile(dialect, sql, masks: MaskOn(kind)));
            return (ex.Code, ex.Message);
        }).Distinct().ToList();
        Assert.Single(responses);

        // the same statement without the mask, and another column of the same masked table, succeed
        Assert.NotNull(Compile(dialect, sql));
        Assert.NotNull(Compile(dialect, "SELECT convert_timezone('UTC', 'Europe/Berlin', e.UpdatedAt) FROM dbo.Events e", masks: MaskOn(MaskKind.Redact)));
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void BC_130_1_AFailingMaskLookupFailsClosed(TargetSqlDialect dialect)
    {
        var request = Request(dialect, available: true, new ThrowingMaskProvider());
        var statement = CompilerTestHelpers.Build("SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e");
        var caps = TrinoSqlEngine.Ast.Capabilities.DialectCapabilityTable.Default.Get(dialect);
        var validator = new TrinoSqlEngine.Ast.Visitors.TimeZoneCallValidator(request.Policy.Catalog, new TrinoSqlEngine.Ast.Visitors.TimeZoneValidationOptions(
            dialect, caps.SupportsTimeZoneConversion, true, request.Policy.Masks, request.Policy.TablesWithMaskedColumns, request.ClientLists, false));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Assert.Throws<SqlTimeZoneException>(() => validator.Validate(statement)).Code);
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void BC_130_1_TheTablesWithMaskedColumnsFallbackCoversACastOverAnUnresolvedColumn(TargetSqlDialect dialect)
    {
        string sql = "WITH c AS (SELECT e.OccurredAt AS OccurredAt FROM dbo.Events e) SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(c.OccurredAt AS timestamp)) FROM c";
        Assert.NotNull(Compile(dialect, sql));
        var masks = MaskOn(MaskKind.Redact);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, sql, masks: masks, tablesWithMaskedColumns: new HashSet<string> { "dbo.Events" }.ToFrozenSet()));
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void BC_130_1_ACancelledMaskLookupIsNotSwallowedIntoARefusal(TargetSqlDialect dialect)
    {
        var masks = new CancellingMaskProvider();
        // resolved column path
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Compile(dialect, "SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e", masks: masks));
        // unresolved CAST operand: the TablesWithMaskedColumns fallback
        Assert.ThrowsAny<OperationCanceledException>(() =>
            Compile(dialect, "WITH c AS (SELECT e.OccurredAt AS OccurredAt FROM dbo.Events e) SELECT convert_timezone('UTC', 'Europe/Berlin', CAST(c.OccurredAt AS timestamp)) FROM c",
                masks: masks, tablesWithMaskedColumns: new HashSet<string> { "dbo.Events" }.ToFrozenSet()));
    }

    private sealed class CancellingMaskProvider : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => throw new OperationCanceledException();

        public MaskSpec GetMask(TableIdentity table, string column) => throw new OperationCanceledException();
    }

    private sealed class ThrowingMaskProvider : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => throw new InvalidOperationException("lookup failed");

        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException("lookup failed");
    }

    // ---- BC-130-5c list parameters ----

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_5_ADeclaredListParameterIsNeverAZone(TargetSqlDialect dialect)
    {
        var lists = ClientListHarness.Shapes(("zones", 2));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument,
            Rejected(dialect, "SELECT convert_timezone(__param_zones, 'UTC', e.OccurredAt) FROM dbo.Events e", lists: lists));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument,
            Rejected(dialect, "SELECT convert_timezone('UTC', __param_zones, e.OccurredAt) FROM dbo.Events e", lists: lists));
        Assert.NotNull(Compile(dialect, "SELECT convert_timezone(__param_zone, 'UTC', e.OccurredAt) FROM dbo.Events e", lists: lists));
    }

    [Fact]
    public void ST_130_5_AnIndexedParameterReferenceIsNeverAZone()
    {
        var statement = CompilerTestHelpers.Build("SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e");
        var indexed = (SqlStatement)new ZoneParameterReplacer(new ParameterReference("zones", IsSynthetic: true, ListIndex: 0, ListCount: 2)).Visit(statement);
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument,
            Assert.Throws<SqlTimeZoneException>(() => TimeZoneConversionTestApi.ValidateGoverned(TargetSqlDialect.PostgreSql, indexed)).Code);
    }

    // ---- BC-130-8 caps ----

    private static string Calls(int count) =>
        string.Join(", ", Enumerable.Range(0, count).Select(i => $"convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) AS c{i}"));

    private static string Nested(int depth)
    {
        string inner = "e.OccurredAt";
        for (int i = 0; i < depth; i++) inner = $"convert_timezone('UTC', 'Europe/Berlin', {inner})";
        return inner;
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_17_AtMostEightCallsPerStatement(TargetSqlDialect dialect)
    {
        Assert.NotNull(Compile(dialect, $"SELECT {Calls(8)} FROM dbo.Events e"));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, $"SELECT {Calls(9)} FROM dbo.Events e"));
    }

    [Theory, MemberData(nameof(DialectData), MemberType = typeof(TimeZoneTestSupport))]
    public void ST_130_17_AtMostThreeLevelsOfNesting(TargetSqlDialect dialect)
    {
        Assert.NotNull(Compile(dialect, $"SELECT {Nested(3)} FROM dbo.Events e"));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, Rejected(dialect, $"SELECT {Nested(4)} FROM dbo.Events e"));
    }

    [Fact]
    public void ST_130_17_AGeneratedStatementOfThreeThousandCallsIsRejectedAtTheNinthWithoutWalkingTheRest()
    {
        // The text limit of the engine would stop a 3000-call text first, so the tree is built by repeating the projection.
        var single = (SelectStatement)CompilerTestHelpers.Build("SELECT convert_timezone('UTC', 'Europe/Berlin', e.OccurredAt) FROM dbo.Events e");
        var spec = (QuerySpecification)single.Body;
        var statement = single with { Body = spec with { Projections = Enumerable.Repeat(spec.Projections[0], 3000).ToList() } };
        var ex = Assert.Throws<SqlTimeZoneException>(() => TimeZoneConversionTestApi.ValidateGoverned(TargetSqlDialect.PostgreSql, statement));
        Assert.Equal(GovernedSqlErrorCodes.TimeZoneArgument, ex.Code);
    }

    [Fact]
    public void ST_130_17_TheCapsAreEngineConstants()
    {
        Assert.Equal(8, TrinoSqlEngine.Ast.Visitors.TimeZoneCallValidator.MaxCallsPerStatement);
        Assert.Equal(3, TrinoSqlEngine.Ast.Visitors.TimeZoneCallValidator.MaxNestingDepth);
    }
}
