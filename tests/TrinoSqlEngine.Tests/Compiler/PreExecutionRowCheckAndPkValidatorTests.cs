namespace TrinoSqlEngine.Tests.Compiler;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "SqlGate")]
public sealed class PreExecutionRowCheckAndPkValidatorTests
{
    private static readonly TableCatalogEntry ParamsEntry = new(
        new TableIdentity("main", "report_params"),
        ImmutableArray.Create(
            new CatalogColumn("id", "bigint"),
            new CatalogColumn("sub_id", "varchar(64)"),
            new CatalogColumn("dept", "varchar(64)"),
            new CatalogColumn("val", "decimal(18,2)")),
        TenantColumn: null,
        IsMetricView: false);

    private static ColumnReference Col(params string[] parts) =>
        new(new SqlQualifiedName(Array.ConvertAll(parts, static p => new SqlIdentifier(p, IsQuoted: false))));

    private static LiteralExpression Str(string v) => new(v, LiteralType.String);
    private static LiteralExpression Num(object v) => new(v, LiteralType.Integer);
    private static LiteralExpression BoolLit(bool v) => new(v, LiteralType.Boolean);
    private static LiteralExpression NullLit() => new(null, LiteralType.Null);
    private static PolicyParameterExpression Pol(string name, SqlParameterType t = SqlParameterType.String) => new(name, t);
    private static ParameterReference Param(string name) => new(name);

    private static IReadOnlyDictionary<string, PolicyValue> NoPol => ImmutableDictionary<string, PolicyValue>.Empty;
    private static IReadOnlyDictionary<string, object?> NoClient => ImmutableDictionary<string, object?>.Empty;

    private static void AssertRowMapAllows(Expression predicate, IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<string, PolicyValue>? pol = null)
    {
        var cols = new HashSet<string>(row.Keys, StringComparer.OrdinalIgnoreCase);
        PreExecutionRowCheck.ValidateRowMap(predicate, cols, pol ?? NoPol, row, TargetSqlDialect.Databricks);
    }

    private static void AssertRowMapRejects(Expression predicate, IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<string, PolicyValue>? pol = null)
    {
        var cols = new HashSet<string>(row.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Throws<DmlCheckOptionViolationException>(() =>
            PreExecutionRowCheck.ValidateRowMap(predicate, cols, pol ?? NoPol, row, TargetSqlDialect.Databricks));
    }

    // -------------------------------------------------------------------------
    // PreExecutionRowCheck: Compile & Covers guards
    // -------------------------------------------------------------------------

    [Fact]
    public void Compile_RejectsEmptyRows_MissingPolicyColumn_MismatchedLength_AndUnsupportedAst()
    {
        var eq = new BinaryExpression(Col("dept"), BinaryOperator.Equal, Str("FIN"));
        var cols = new[] { new SqlIdentifier("dept", false) };
        var polCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "dept" };

        Assert.Throws<SecurityException>(() => PreExecutionRowCheck.Compile(eq, polCols, cols, []));

        var otherCols = new[] { new SqlIdentifier("other", false) };
        Assert.Throws<SecurityException>(() => PreExecutionRowCheck.Compile(eq, polCols, otherCols, [new RowValueExpression([Str("x")])]));

        // Column referenced inside AST missing from writtenColumns even when policyColumns is empty
        Assert.Throws<SecurityException>(() => PreExecutionRowCheck.Compile(eq, new HashSet<string>(), otherCols, [new RowValueExpression([Str("x")])]));

        // Row element count mismatch
        Assert.Throws<SecurityException>(() => PreExecutionRowCheck.Compile(eq, polCols, cols, [new RowValueExpression([Str("FIN"), Str("extra")])]));

        // Unsupported policy AST node
        var unsupportedPolicy = new FunctionCallExpression(new SqlQualifiedName([new SqlIdentifier("lower", false)]), [Col("dept")]);
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            PreExecutionRowCheck.Compile(unsupportedPolicy, polCols, cols, [new RowValueExpression([Str("FIN")])]));

        // Unsupported cell AST node
        var unsupportedCell = new FunctionCallExpression(new SqlQualifiedName([new SqlIdentifier("upper", false)]), [Str("fin")]);
        Assert.Throws<SqlCompileNotSupportedException>(() =>
            PreExecutionRowCheck.Compile(eq, polCols, cols, [new RowValueExpression([unsupportedCell])]));
    }

    [Fact]
    public void Compile_AcceptsWrappedCellExpressions_AndCoversChecksAllDimensions()
    {
        var pred = new SecurityPredicateExpression(
            new ParenthesizedExpression(new BinaryExpression(Col("n"), BinaryOperator.Equal, Num(-5L))),
            new SecurityPredicateId("main.report_params", 0),
            SecurityScope.Root);

        var writtenCols = new[] { new SqlIdentifier("n", false), new SqlIdentifier("note", false) };
        var polCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "n" };
        var rows = new[]
        {
            new RowValueExpression([
                new ParenthesizedExpression(new CastExpression(new UnaryExpression(UnaryOperator.Negate, Num(5)), "bigint")),
                Str("ok")
            ])
        };

        var check = PreExecutionRowCheck.Compile(pred, polCols, writtenCols, rows);
        Assert.Equal(1, check.RowCount);
        Assert.Contains("n", check.PolicyColumns);

        check.Validate(NoPol, NoClient, TargetSqlDialect.Databricks);

        var valuesBody = new ValuesQueryBody(rows);
        Assert.True(check.Covers(polCols, writtenCols, valuesBody));
        Assert.True(check.Covers(null, writtenCols, valuesBody));
        Assert.False(check.Covers(new HashSet<string> { "missing_col" }, writtenCols, valuesBody));
        Assert.False(check.Covers(polCols, [new SqlIdentifier("n", false)], valuesBody));
        Assert.False(check.Covers(polCols, writtenCols, new ValuesQueryBody([])));
        Assert.False(check.Covers(polCols, [new SqlIdentifier("other", false), new SqlIdentifier("note", false)], valuesBody));
    }

    [Fact]
    public void Validate_ClientParameterNormalization_AndMissingValueErrors()
    {
        var pred = new BinaryExpression(Col("dept"), BinaryOperator.Equal, Pol("__p_dept"));
        var cols = new[] { new SqlIdentifier("dept", false) };
        var polCols = new HashSet<string> { "dept" };
        var rows = new[] { new RowValueExpression([Param("@p1")]) };

        var check = PreExecutionRowCheck.Compile(pred, polCols, cols, rows);
        var polVals = new Dictionary<string, PolicyValue> { ["__p_dept"] = new("FIN", SqlParameterType.String) };

        // Leading prefixes (@, :, $) and ignored empty key
        check.Validate(polVals, new Dictionary<string, object?> { [""] = "ignored", [":p1"] = "FIN" }, TargetSqlDialect.Databricks);
        check.Validate(polVals, new Dictionary<string, object?> { ["$p1"] = "FIN" }, TargetSqlDialect.Databricks);

        // DBNull client value fails closed as NULL != 'FIN'
        Assert.Throws<DmlCheckOptionViolationException>(() =>
            check.Validate(polVals, new Dictionary<string, object?> { ["p1"] = DBNull.Value }, TargetSqlDialect.Databricks));

        // Missing client param throws SecurityException
        Assert.Throws<SecurityException>(() => check.Validate(polVals, NoClient, TargetSqlDialect.Databricks));

        // Missing policy param throws SecurityException
        Assert.Throws<SecurityException>(() => check.Validate(NoPol, new Dictionary<string, object?> { ["p1"] = "FIN" }, TargetSqlDialect.Databricks));

        // ValidateRowMap missing policy column throws SecurityException
        Assert.Throws<SecurityException>(() =>
            PreExecutionRowCheck.ValidateRowMap(pred, polCols, polVals, new Dictionary<string, object?> { ["other"] = "FIN" }, TargetSqlDialect.Databricks));
    }

    // -------------------------------------------------------------------------
    // PreExecutionRowCheck: SQL 3VL truth tables (AND, OR, NOT, IS [NOT] NULL)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    [InlineData(true, null, false)]
    [InlineData(null, true, false)]
    [InlineData(false, null, false)]
    [InlineData(null, false, false)]
    [InlineData(null, null, false)]
    public void ThreeValuedAnd_TruthTable(bool? left, bool? right, bool expectedAllow)
    {
        Expression ToExpr(bool? v) => v.HasValue ? BoolLit(v.Value) : NullLit();
        var pred = new BinaryExpression(ToExpr(left), BinaryOperator.And, ToExpr(right));
        if (expectedAllow) AssertRowMapAllows(pred, new Dictionary<string, object?>());
        else AssertRowMapRejects(pred, new Dictionary<string, object?>());
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, null, true)]
    [InlineData(null, true, true)]
    [InlineData(false, null, false)]
    [InlineData(null, false, false)]
    [InlineData(null, null, false)]
    public void ThreeValuedOr_TruthTable(bool? left, bool? right, bool expectedAllow)
    {
        Expression ToExpr(bool? v) => v.HasValue ? BoolLit(v.Value) : NullLit();
        var pred = new BinaryExpression(ToExpr(left), BinaryOperator.Or, ToExpr(right));
        if (expectedAllow) AssertRowMapAllows(pred, new Dictionary<string, object?>());
        else AssertRowMapRejects(pred, new Dictionary<string, object?>());
    }

    [Fact]
    public void ThreeValuedNot_AndNullPredicates()
    {
        AssertRowMapAllows(new UnaryExpression(UnaryOperator.Not, BoolLit(false)), new Dictionary<string, object?>());
        AssertRowMapRejects(new UnaryExpression(UnaryOperator.Not, BoolLit(true)), new Dictionary<string, object?>());
        AssertRowMapRejects(new UnaryExpression(UnaryOperator.Not, NullLit()), new Dictionary<string, object?>());

        var isNull = new UnaryExpression(UnaryOperator.IsNull, Col("a"));
        var isNotNull = new UnaryExpression(UnaryOperator.IsNotNull, Col("a"));

        AssertRowMapAllows(isNull, new Dictionary<string, object?> { ["a"] = null });
        AssertRowMapAllows(isNull, new Dictionary<string, object?> { ["a"] = DBNull.Value });
        AssertRowMapRejects(isNull, new Dictionary<string, object?> { ["a"] = "x" });

        AssertRowMapAllows(isNotNull, new Dictionary<string, object?> { ["a"] = "x" });
        AssertRowMapRejects(isNotNull, new Dictionary<string, object?> { ["a"] = null });
    }

    // -------------------------------------------------------------------------
    // PreExecutionRowCheck: Comparisons, IN/NOT IN, BETWEEN/NOT BETWEEN, IS DISTINCT FROM
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(BinaryOperator.Equal, 10, 10, true)]
    [InlineData(BinaryOperator.Equal, 10, 11, false)]
    [InlineData(BinaryOperator.NotEqual, 10, 11, true)]
    [InlineData(BinaryOperator.NotEqual, 10, 10, false)]
    [InlineData(BinaryOperator.LessThan, 9, 10, true)]
    [InlineData(BinaryOperator.LessThan, 10, 10, false)]
    [InlineData(BinaryOperator.LessThanOrEqual, 10, 10, true)]
    [InlineData(BinaryOperator.LessThanOrEqual, 11, 10, false)]
    [InlineData(BinaryOperator.GreaterThan, 11, 10, true)]
    [InlineData(BinaryOperator.GreaterThan, 10, 10, false)]
    [InlineData(BinaryOperator.GreaterThanOrEqual, 10, 10, true)]
    [InlineData(BinaryOperator.GreaterThanOrEqual, 9, 10, false)]
    public void ComparisonOperators_AllBoundaries(BinaryOperator op, int lhs, int rhs, bool expectedAllow)
    {
        var pred = new BinaryExpression(Col("x"), op, Num(rhs));
        var row = new Dictionary<string, object?> { ["x"] = lhs };
        if (expectedAllow) AssertRowMapAllows(pred, row);
        else AssertRowMapRejects(pred, row);
    }

    [Fact]
    public void InList_AndNotIn_HandleMatchesMissesAndNulls()
    {
        var inList = new InListExpression(Col("x"), [Str("A"), Str("B")], IsNotIn: false);
        AssertRowMapAllows(inList, new Dictionary<string, object?> { ["x"] = "A" });
        AssertRowMapRejects(inList, new Dictionary<string, object?> { ["x"] = "C" });
        AssertRowMapRejects(inList, new Dictionary<string, object?> { ["x"] = null });

        var inListWithNull = new InListExpression(Col("x"), [NullLit(), Str("B")], IsNotIn: false);
        AssertRowMapAllows(inListWithNull, new Dictionary<string, object?> { ["x"] = "B" });
        AssertRowMapRejects(inListWithNull, new Dictionary<string, object?> { ["x"] = "C" });

        var notIn = new InListExpression(Col("x"), [Str("A"), Str("B")], IsNotIn: true);
        AssertRowMapAllows(notIn, new Dictionary<string, object?> { ["x"] = "C" });
        AssertRowMapRejects(notIn, new Dictionary<string, object?> { ["x"] = "A" });
        AssertRowMapRejects(notIn, new Dictionary<string, object?> { ["x"] = null });

        // SQL 3VL: 'C' NOT IN (NULL, 'B') evaluates to UNKNOWN -> must reject!
        var notInWithNull = new InListExpression(Col("x"), [NullLit(), Str("B")], IsNotIn: true);
        AssertRowMapRejects(notInWithNull, new Dictionary<string, object?> { ["x"] = "C" });
        AssertRowMapRejects(notInWithNull, new Dictionary<string, object?> { ["x"] = "B" });
    }

    [Fact]
    public void Between_AndNotBetween_HandleBoundariesAndNulls()
    {
        var between = new BetweenExpression(Col("x"), Num(10), Num(20), IsNotBetween: false);
        AssertRowMapAllows(between, new Dictionary<string, object?> { ["x"] = 10 });
        AssertRowMapAllows(between, new Dictionary<string, object?> { ["x"] = 15 });
        AssertRowMapAllows(between, new Dictionary<string, object?> { ["x"] = 20 });
        AssertRowMapRejects(between, new Dictionary<string, object?> { ["x"] = 9 });
        AssertRowMapRejects(between, new Dictionary<string, object?> { ["x"] = 21 });
        AssertRowMapRejects(between, new Dictionary<string, object?> { ["x"] = null });

        var notBetween = new BetweenExpression(Col("x"), Num(10), Num(20), IsNotBetween: true);
        AssertRowMapAllows(notBetween, new Dictionary<string, object?> { ["x"] = 9 });
        AssertRowMapAllows(notBetween, new Dictionary<string, object?> { ["x"] = 21 });
        AssertRowMapRejects(notBetween, new Dictionary<string, object?> { ["x"] = 10 });
        AssertRowMapRejects(notBetween, new Dictionary<string, object?> { ["x"] = 15 });
        AssertRowMapRejects(notBetween, new Dictionary<string, object?> { ["x"] = null });

        // NULL bound makes in-range UNKNOWN -> both BETWEEN and NOT BETWEEN (when not strictly outside) reject
        var betweenNullHi = new BetweenExpression(Col("x"), Num(10), NullLit(), IsNotBetween: false);
        AssertRowMapRejects(betweenNullHi, new Dictionary<string, object?> { ["x"] = 15 });
        var notBetweenNullHi = new BetweenExpression(Col("x"), Num(10), NullLit(), IsNotBetween: true);
        AssertRowMapAllows(notBetweenNullHi, new Dictionary<string, object?> { ["x"] = 5 });
        AssertRowMapRejects(notBetweenNullHi, new Dictionary<string, object?> { ["x"] = 15 });
    }

    [Fact]
    public void IsDistinctFrom_AndIsNotDistinctFrom()
    {
        var distinct = new IsDistinctFromExpression(Col("x"), Col("y"), IsNotDistinctFrom: false);
        var notDistinct = new IsDistinctFromExpression(Col("x"), Col("y"), IsNotDistinctFrom: true);

        AssertRowMapAllows(notDistinct, new Dictionary<string, object?> { ["x"] = null, ["y"] = null });
        AssertRowMapRejects(distinct, new Dictionary<string, object?> { ["x"] = null, ["y"] = null });

        AssertRowMapAllows(distinct, new Dictionary<string, object?> { ["x"] = null, ["y"] = "A" });
        AssertRowMapRejects(notDistinct, new Dictionary<string, object?> { ["x"] = null, ["y"] = "A" });

        AssertRowMapAllows(distinct, new Dictionary<string, object?> { ["x"] = "A", ["y"] = null });
        AssertRowMapAllows(distinct, new Dictionary<string, object?> { ["x"] = "A", ["y"] = "B" });
        AssertRowMapRejects(distinct, new Dictionary<string, object?> { ["x"] = "A", ["y"] = "A" });
        AssertRowMapAllows(notDistinct, new Dictionary<string, object?> { ["x"] = "A", ["y"] = "A" });
    }

    // -------------------------------------------------------------------------
    // PreExecutionRowCheck: Negate, Cast, and all runtime value comparisons
    // -------------------------------------------------------------------------

    [Fact]
    public void Negate_AndCast_CoverAllTypesAndFailClosedOnUnsupported()
    {
        // Negate across numeric types and null
        AssertRowMapAllows(new BinaryExpression(new UnaryExpression(UnaryOperator.Negate, Col("x")), BinaryOperator.Equal, Num(-5L)), new Dictionary<string, object?> { ["x"] = 5 });
        AssertRowMapAllows(new BinaryExpression(new UnaryExpression(UnaryOperator.Negate, Col("x")), BinaryOperator.Equal, Num(-5L)), new Dictionary<string, object?> { ["x"] = 5L });
        AssertRowMapAllows(new BinaryExpression(new UnaryExpression(UnaryOperator.Negate, Col("x")), BinaryOperator.Equal, Num(-5.5m)), new Dictionary<string, object?> { ["x"] = 5.5m });
        AssertRowMapAllows(new BinaryExpression(new UnaryExpression(UnaryOperator.Negate, Col("x")), BinaryOperator.Equal, Num(-2.5d)), new Dictionary<string, object?> { ["x"] = 2.5d });
        AssertRowMapAllows(new BinaryExpression(new UnaryExpression(UnaryOperator.Negate, Col("x")), BinaryOperator.Equal, Num(-2.5d)), new Dictionary<string, object?> { ["x"] = 2.5f });
        AssertRowMapAllows(new UnaryExpression(UnaryOperator.IsNull, new UnaryExpression(UnaryOperator.Negate, Col("x"))), new Dictionary<string, object?> { ["x"] = null });
        Assert.Throws<SecurityException>(() =>
            PreExecutionRowCheck.ValidateRowMap(
                new BinaryExpression(new UnaryExpression(UnaryOperator.Negate, Col("x")), BinaryOperator.Equal, Num(1)),
                new HashSet<string> { "x" },
                NoPol,
                new Dictionary<string, object?> { ["x"] = "not-a-number" },
                TargetSqlDialect.Databricks));

        // Cast across varchar(n), int, decimal, bool, uuid, unknown, null
        var g = Guid.NewGuid();
        AssertRowMapAllows(new BinaryExpression(new CastExpression(Col("x"), "varchar(32)"), BinaryOperator.Equal, Str("42")), new Dictionary<string, object?> { ["x"] = 42 });
        AssertRowMapAllows(new BinaryExpression(new CastExpression(Col("x"), "integer"), BinaryOperator.Equal, Num(42L)), new Dictionary<string, object?> { ["x"] = "42" });
        AssertRowMapAllows(new BinaryExpression(new CastExpression(Col("x"), "decimal(10,2)"), BinaryOperator.Equal, Num(3.5m)), new Dictionary<string, object?> { ["x"] = "3.5" });
        AssertRowMapAllows(new BinaryExpression(new CastExpression(Col("x"), "boolean"), BinaryOperator.Equal, BoolLit(true)), new Dictionary<string, object?> { ["x"] = "true" });
        AssertRowMapAllows(new BinaryExpression(new CastExpression(Col("x"), "uuid"), BinaryOperator.Equal, new LiteralExpression(g, LiteralType.String)), new Dictionary<string, object?> { ["x"] = g.ToString("D") });
        AssertRowMapAllows(new BinaryExpression(new CastExpression(Col("x"), "uniqueidentifier"), BinaryOperator.Equal, new LiteralExpression(g, LiteralType.String)), new Dictionary<string, object?> { ["x"] = g });
        AssertRowMapAllows(new BinaryExpression(new CastExpression(Col("x"), "custom_type"), BinaryOperator.Equal, Str("raw")), new Dictionary<string, object?> { ["x"] = "raw" });
        AssertRowMapAllows(new UnaryExpression(UnaryOperator.IsNull, new CastExpression(Col("x"), "varchar(10)")), new Dictionary<string, object?> { ["x"] = null });
    }

    [Fact]
    public void CompareValues_SupportsAllNumericBoolGuidDateAndRejectsIncompatible()
    {
        var eq = new BinaryExpression(Col("a"), BinaryOperator.Equal, Col("b"));

        // Numeric family
        object[] numerics = [(sbyte)7, (byte)7, (short)7, (ushort)7, 7, 7u, 7L, 7ul, 7m, 7f, 7d];
        foreach (var n in numerics)
        {
            AssertRowMapAllows(eq, new Dictionary<string, object?> { ["a"] = n, ["b"] = 7 });
        }

        // NaN / Infinity fail closed (not numeric)
        AssertRowMapRejects(eq, new Dictionary<string, object?> { ["a"] = float.NaN, ["b"] = 0f });
        AssertRowMapRejects(eq, new Dictionary<string, object?> { ["a"] = double.PositiveInfinity, ["b"] = 0d });

        // String ordinal (case-sensitive)
        AssertRowMapAllows(eq, new Dictionary<string, object?> { ["a"] = "Fin", ["b"] = "Fin" });
        AssertRowMapRejects(eq, new Dictionary<string, object?> { ["a"] = "Fin", ["b"] = "fin" });

        // Bool
        AssertRowMapAllows(eq, new Dictionary<string, object?> { ["a"] = true, ["b"] = true });
        AssertRowMapRejects(eq, new Dictionary<string, object?> { ["a"] = true, ["b"] = false });

        // Guid and Guid-vs-string
        var g = Guid.NewGuid();
        AssertRowMapAllows(eq, new Dictionary<string, object?> { ["a"] = g, ["b"] = g.ToString("D") });
        AssertRowMapRejects(eq, new Dictionary<string, object?> { ["a"] = g, ["b"] = "not-a-guid" });

        // DateTimeOffset, DateTime (Utc & Unspecified), DateOnly
        var dto = new DateTimeOffset(2026, 11, 26, 0, 0, 0, TimeSpan.Zero);
        AssertRowMapAllows(eq, new Dictionary<string, object?> { ["a"] = dto, ["b"] = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc) });
        AssertRowMapAllows(eq, new Dictionary<string, object?> { ["a"] = dto, ["b"] = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Unspecified) });
        AssertRowMapAllows(eq, new Dictionary<string, object?> { ["a"] = dto, ["b"] = new DateOnly(2026, 11, 26) });

        // Incompatible runtime types fail closed
        AssertRowMapRejects(eq, new Dictionary<string, object?> { ["a"] = 7, ["b"] = "7" });
    }

    // -------------------------------------------------------------------------
    // BoundedPkPredicateValidator
    // -------------------------------------------------------------------------

    [Fact]
    public void BoundedPkValidator_RejectsInvalidPreconditionsSubqueriesAndDisjunctions()
    {
        var pkEq = new BinaryExpression(Col("id"), BinaryOperator.Equal, Num(1));

        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(pkEq, ParamsEntry, null, 10));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(pkEq, ParamsEntry, [], 10));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(pkEq, ParamsEntry, ["id"], 0));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(pkEq, ParamsEntry, ["id"], -1));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(null, ParamsEntry, ["id"], 10));

        // Top-level OR rejected
        var orPred = new BinaryExpression(pkEq, BinaryOperator.Or, new BinaryExpression(Col("id"), BinaryOperator.Equal, Num(2)));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(orPred, ParamsEntry, ["id"], 10));

        // Nested OR under AND rejected
        var nestedOr = new BinaryExpression(pkEq, BinaryOperator.And, orPred);
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(nestedOr, ParamsEntry, ["id"], 10));

        // Subquery inside WHERE rejected
        var subq = new SelectStatement(null, new ValuesQueryBody([new RowValueExpression([Num(1)])]), null, null);
        var inSubq = new ExistsExpression(subq);
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(
            new BinaryExpression(pkEq, BinaryOperator.And, inSubq), ParamsEntry, ["id"], 10));
    }

    [Fact]
    public void BoundedPkValidator_HandlesSingleCompositeReversedQualifiedAndInLists()
    {
        // Reversed equality (1 = id) with parens, cast, negate, param, policy param
        var revEq = new ParenthesizedExpression(new BinaryExpression(
            new CastExpression(new UnaryExpression(UnaryOperator.Negate, Num(1)), "bigint"),
            BinaryOperator.Equal,
            Col("report_params", "id")));
        Assert.Equal(1L, BoundedPkPredicateValidator.Validate(revEq, ParamsEntry, ["id"], 1));

        // Wrong table qualifier rejected
        var wrongQualifier = new BinaryExpression(Col("other_table", "id"), BinaryOperator.Equal, Param("p1"));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(wrongQualifier, ParamsEntry, ["id"], 10));

        // 3-part column reference rejected
        var threePart = new BinaryExpression(Col("cat", "report_params", "id"), BinaryOperator.Equal, Num(1));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(threePart, ParamsEntry, ["id"], 10));

        // NULL literal or column-to-column equality does not satisfy PK bound
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(
            new BinaryExpression(Col("id"), BinaryOperator.Equal, NullLit()), ParamsEntry, ["id"], 10));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(
            new BinaryExpression(Col("id"), BinaryOperator.Equal, Col("val")), ParamsEntry, ["id"], 10));

        // Composite PK: both bound -> 1; only one bound -> throws
        var comp = new BinaryExpression(
            new BinaryExpression(Col("id"), BinaryOperator.Equal, Param("p1")),
            BinaryOperator.And,
            new BinaryExpression(Col("sub_id"), BinaryOperator.Equal, Pol("__pol_sub")));
        Assert.Equal(1L, BoundedPkPredicateValidator.Validate(comp, ParamsEntry, ["id", "sub_id"], 5));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(
            new BinaryExpression(Col("id"), BinaryOperator.Equal, Num(1)), ParamsEntry, ["id", "sub_id"], 5));

        // Single-PK IN list returns count, takes min across multiple INs, enforces maxRows, rejects NOT IN and composite IN
        var in3 = new InListExpression(Col("id"), [Num(1), Num(2), Num(3)], IsNotIn: false);
        Assert.Equal(3L, BoundedPkPredicateValidator.Validate(in3, ParamsEntry, ["id"], 5));
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(in3, ParamsEntry, ["id"], 2));

        var in2 = new InListExpression(Col("id"), [Num(1), Num(2)], IsNotIn: false);
        Assert.Equal(2L, BoundedPkPredicateValidator.Validate(
            new BinaryExpression(in3, BinaryOperator.And, in2), ParamsEntry, ["id"], 5));

        var notIn = new InListExpression(Col("id"), [Num(1)], IsNotIn: true);
        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(notIn, ParamsEntry, ["id"], 5));

        Assert.Throws<SecurityException>(() => BoundedPkPredicateValidator.Validate(in3, ParamsEntry, ["id", "sub_id"], 5));
    }
}
