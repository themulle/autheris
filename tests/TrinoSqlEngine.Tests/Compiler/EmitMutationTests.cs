using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>Mutation-gate tests (M-12) for the emitted-text checker, the error code table, the cache key and the audit digest.</summary>
[Trait("Category", "SqlGate")]
public class EmitMutationTests
{
    private static readonly ImmutableArray<BoundParameter> NoParams = ImmutableArray<BoundParameter>.Empty;
    private static readonly IReadOnlyList<EmittedRange> NoRanges = Array.Empty<EmittedRange>();

    private static EmittedSqlInvariantViolationException Violation(string sql, ImmutableArray<BoundParameter>? ps = null,
        IReadOnlyList<EmittedRange>? ranges = null, SqlStatementClass statementClass = SqlStatementClass.Select) =>
        Assert.Throws<EmittedSqlInvariantViolationException>(() =>
            EmittedSqlInvariantChecker.Check(sql, TargetSqlDialect.SqlServer, ps ?? NoParams, ranges ?? NoRanges, false, statementClass));

    private static BoundParameter P(int ordinal) =>
        new($"@p{ordinal}", $"@p{ordinal}", ordinal, "v", SqlParameterType.String, ParameterOrigin.QueryLiteral);

    private static void Ok(string sql, ImmutableArray<BoundParameter>? ps = null, IReadOnlyList<EmittedRange>? ranges = null) =>
        EmittedSqlInvariantChecker.Check(sql, TargetSqlDialect.SqlServer, ps ?? NoParams, ranges ?? NoRanges);

    private static void AssertViolation(EmittedSqlInvariantViolationException ex, string rule, int position)
    {
        Assert.Equal(rule, ex.Rule);
        Assert.Equal($"Emitted SQL violates invariant '{rule}' at position {position}.", ex.Message);
    }

    [Fact]
    public void Violations_NameTheRuleAndThePosition()
    {
        const string numeric = "SELECT [a] FROM [t] WHERE [a] = 1";
        AssertViolation(Violation(numeric), "unregistered-numeric-token", numeric.Length - 1);
        AssertViolation(Violation("SELECT 'a'"), "forbidden-character", 7);
        AssertViolation(Violation("SELECT [a];"), "forbidden-character", 10);
        AssertViolation(Violation("SELECT [a] `"), "forbidden-character", 11);
        AssertViolation(Violation("SELECT [a] -- x"), "comment", 11);
        AssertViolation(Violation("SELECT [a] /* x */"), "comment", 11);
        AssertViolation(Violation("SELECT [a]\u0001"), "control-character", 10);
        AssertViolation(Violation("SELECT [abc"), "unterminated-delimited-identifier", 7);
    }

    [Fact]
    public void ATrailingDashOrSlash_IsNotAComment_AndNeverReadsPastTheText()
    {
        Ok("SELECT [a]-");
        Ok("SELECT [a]/");
        Ok("SELECT [a] - [b]");
        Ok("SELECT [a] / [b]");
        Ok("SELECT [a]\n\r\t");
    }

    [Fact]
    public void MarkerAndParameterMismatches_AreNamed()
    {
        AssertViolation(Violation("SELECT @p1, @p1", ImmutableArray.Create(P(1), P(1))), "duplicate-parameter-marker", 0);
        AssertViolation(Violation("SELECT [a]", ImmutableArray.Create(P(1))), "parameter-without-marker", 0);
        AssertViolation(Violation("SELECT @p1"), "marker-without-parameter", 0);
        Ok("SELECT @p1, @p1", ImmutableArray.Create(P(1)));
    }

    [Fact]
    public void ATerminatorIsOnlyAllowedAsTheLastCharacterOfASqlServerMerge()
    {
        const string merge = "MERGE INTO [t] AS [x] USING [s] AS [y] ON [x].[a] = [y].[a] WHEN MATCHED THEN DELETE;";
        var last = new EmittedRange(merge.Length - 1, 1, EmittedRangeKind.StatementTerminator);
        EmittedSqlInvariantChecker.Check(merge, TargetSqlDialect.SqlServer, NoParams, new[] { last }, false, SqlStatementClass.Merge);

        AssertViolation(Violation(merge, ranges: new[] { last }, statementClass: SqlStatementClass.Select), "unregistered-terminator", merge.Length - 1);
    }

    [Fact]
    public void ATerminatorRegistration_ThatIsNotLastSingleOrSqlServer_IsRejected()
    {
        const string merge = "MERGE INTO [t] AS [x] USING [s] AS [y] ON [x].[a] = [y].[a] WHEN MATCHED THEN DELETE;";
        var last = new EmittedRange(merge.Length - 1, 1, EmittedRangeKind.StatementTerminator);

        var notLast = Assert.Throws<EmittedSqlInvariantViolationException>(() => EmittedSqlInvariantChecker.Check(
            merge + " ", TargetSqlDialect.SqlServer, NoParams, new[] { last }, false, SqlStatementClass.Merge));
        AssertViolation(notLast, "unregistered-terminator", merge.Length - 1);

        var twice = Assert.Throws<EmittedSqlInvariantViolationException>(() => EmittedSqlInvariantChecker.Check(
            merge, TargetSqlDialect.SqlServer, NoParams, new[] { last, last }, false, SqlStatementClass.Merge));
        Assert.Equal("unregistered-terminator", twice.Rule);

        var wrongLength = Assert.Throws<EmittedSqlInvariantViolationException>(() => EmittedSqlInvariantChecker.Check(
            merge, TargetSqlDialect.SqlServer, NoParams, new[] { new EmittedRange(merge.Length - 2, 2, EmittedRangeKind.StatementTerminator) }, false, SqlStatementClass.Merge));
        Assert.Equal("unregistered-terminator", wrongLength.Rule);

        var notSemicolon = Assert.Throws<EmittedSqlInvariantViolationException>(() => EmittedSqlInvariantChecker.Check(
            merge.TrimEnd(';') + " ", TargetSqlDialect.SqlServer, NoParams, new[] { last }, false, SqlStatementClass.Merge));
        Assert.Equal("unregistered-terminator", notSemicolon.Rule);

        var otherDialect = Assert.Throws<EmittedSqlInvariantViolationException>(() => EmittedSqlInvariantChecker.Check(
            merge, TargetSqlDialect.PostgreSql, NoParams, new[] { last }, false, SqlStatementClass.Merge));
        Assert.Equal("unregistered-terminator", otherDialect.Rule);
    }

    [Theory]
    [InlineData(":12", true, 3)]
    [InlineData(":1x", false, 0)]
    [InlineData(":", false, 0)]
    [InlineData("a:1", true, 3)]
    [InlineData("::1", false, 0)]
    [InlineData("1", false, 0)]
    public void ColonOrdinalMarkers_AreReadOnlyAsAWholeToken(string sql, bool isMarker, int end)
    {
        var read = typeof(EmittedSqlInvariantChecker).GetMethod("TryReadMarker", BindingFlags.NonPublic | BindingFlags.Static)!;
        int start = sql.StartsWith("a", StringComparison.Ordinal) ? 1 : sql == "::1" ? 1 : 0;
        var args = new object?[] { sql, start, ParameterMarkerStyle.ColonOrdinal, 0 };
        bool result = (bool)read.Invoke(null, args)!;
        Assert.Equal(isMarker, result);
        Assert.Equal(isMarker ? end : start, (int)args[3]!);
    }

    [Fact]
    public void ColonOrdinalMarker_AfterAColon_IsNotAMarker_AndAFollowingColonOrdinalIsRead()
    {
        var read = typeof(EmittedSqlInvariantChecker).GetMethod("TryReadMarker", BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object?[] { "x ::1 :2 ", 3, ParameterMarkerStyle.ColonOrdinal, 0 };
        Assert.False((bool)read.Invoke(null, args)!);   // the second colon of "::1"
        args = new object?[] { "x ::1 :2 ", 6, ParameterMarkerStyle.ColonOrdinal, 0 };
        Assert.True((bool)read.Invoke(null, args)!);
        Assert.Equal(8, (int)args[3]!);
    }

    // ---- error codes ----

    [Theory]
    [InlineData(GovernedSqlErrorCodes.ConstraintViolation, "The statement violated a data constraint.")]
    [InlineData(GovernedSqlErrorCodes.DataError, "The statement contained a value that could not be stored or converted.")]
    [InlineData(GovernedSqlErrorCodes.CheckOptionViolation, "The statement wrote a row that the row policy does not permit.")]
    [InlineData(GovernedSqlErrorCodes.CheckedExecutionRequired, "The statement must run through the checked DML executor.")]
    [InlineData(GovernedSqlErrorCodes.ProviderError, "The data source could not execute the statement.")]
    [InlineData(GovernedSqlErrorCodes.SyntaxError, "Invalid SQL syntax.")]
    [InlineData(GovernedSqlErrorCodes.ParameterMissing, "A required statement parameter was not supplied.")]
    [InlineData(GovernedSqlErrorCodes.ConstructNotSupported, "The statement uses a construct that is not supported.")]
    [InlineData(GovernedSqlErrorCodes.StatementNotPermitted, "This kind of statement is not permitted.")]
    [InlineData(GovernedSqlErrorCodes.DialectNotSupported, "The statement is not supported for this data source dialect.")]
    [InlineData(GovernedSqlErrorCodes.LimitExceeded, "The statement exceeds a resource limit.")]
    [InlineData(GovernedSqlErrorCodes.UnfilteredDml, "UPDATE and DELETE statements require a restricting WHERE clause.")]
    [InlineData(GovernedSqlErrorCodes.PolicyDenied, "Access to a referenced table or column is denied.")]
    [InlineData(GovernedSqlErrorCodes.PolicyViolation, "The SQL statement was rejected by the gateway security policy.")]
    [InlineData(GovernedSqlErrorCodes.TenantIdCollision, "Access denied.")]
    [InlineData(GovernedSqlErrorCodes.AffectedRowsLimit, "The statement would change more rows than permitted.")]
    [InlineData(GovernedSqlErrorCodes.CompilerInvariant, "The statement could not be processed.")]
    [InlineData(GovernedSqlErrorCodes.Timeout, "The SQL statement exceeded the execution time limit. Narrow the query (filter, fewer columns, LIMIT) or contact support with the trace id.")]
    [InlineData(GovernedSqlErrorCodes.SourceUnavailable, "The data source is temporarily unavailable. Retry later.")]
    [InlineData(GovernedSqlErrorCodes.ReservedIdentifier, "The statement uses an identifier that is reserved by the gateway.")]
    [InlineData("SOMETHING_UNKNOWN", "The statement could not be executed.")]
    public void FixedMessage_IsStablePerCode(string code, string message)
    {
        Assert.Equal(message, GovernedSqlErrorCodes.FixedMessage(code));
        var ex = new GovernedSqlException(code, TargetSqlDialect.Oracle);
        Assert.Equal(message, ex.Message);
        Assert.Equal(code, ex.Code);
        Assert.Equal(TargetSqlDialect.Oracle, ex.Dialect);
    }

    [Fact]
    public void ErrorCodes_AreTheDocumentedLiterals()
    {
        Assert.Equal("DML_CONSTRAINT_VIOLATION", GovernedSqlErrorCodes.ConstraintViolation);
        Assert.Equal("SQL_DATA_ERROR", GovernedSqlErrorCodes.DataError);
        Assert.Equal("SQL_PROVIDER_ERROR", GovernedSqlErrorCodes.ProviderError);
        Assert.Equal("DML_CHECK_OPTION_VIOLATION", GovernedSqlErrorCodes.CheckOptionViolation);
        Assert.Equal("DML_CHECKED_EXECUTION_REQUIRED", GovernedSqlErrorCodes.CheckedExecutionRequired);
        Assert.Equal("SQL_SYNTAX_ERROR", GovernedSqlErrorCodes.SyntaxError);
        Assert.Equal("SQL_PARAMETER_MISSING", GovernedSqlErrorCodes.ParameterMissing);
        Assert.Equal("SQL_CONSTRUCT_NOT_SUPPORTED", GovernedSqlErrorCodes.ConstructNotSupported);
        Assert.Equal("SQL_STATEMENT_NOT_PERMITTED", GovernedSqlErrorCodes.StatementNotPermitted);
        Assert.Equal("SQL_DIALECT_NOT_SUPPORTED", GovernedSqlErrorCodes.DialectNotSupported);
        Assert.Equal("SQL_LIMIT_EXCEEDED", GovernedSqlErrorCodes.LimitExceeded);
        Assert.Equal("SQL_UNFILTERED_DML", GovernedSqlErrorCodes.UnfilteredDml);
        Assert.Equal("SQL_POLICY_DENIED", GovernedSqlErrorCodes.PolicyDenied);
        Assert.Equal("SQL_POLICY_VIOLATION", GovernedSqlErrorCodes.PolicyViolation);
        Assert.Equal("TENANT_ID_COLLISION", GovernedSqlErrorCodes.TenantIdCollision);
        Assert.Equal("DML_AFFECTED_ROWS_LIMIT", GovernedSqlErrorCodes.AffectedRowsLimit);
        Assert.Equal("SQL_COMPILER_INVARIANT", GovernedSqlErrorCodes.CompilerInvariant);
        Assert.Equal("SQL_TIMEOUT", GovernedSqlErrorCodes.Timeout);
        Assert.Equal("SQL_SOURCE_UNAVAILABLE", GovernedSqlErrorCodes.SourceUnavailable);
    }

    [Fact]
    public void TheTypedGovernedExceptions_CarryTheirCode()
    {
        Assert.Equal(GovernedSqlErrorCodes.CheckOptionViolation, new DmlCheckOptionViolationException(TargetSqlDialect.Sqlite).Code);
        Assert.Equal(GovernedSqlErrorCodes.CheckedExecutionRequired, new CheckedExecutionRequiredException(TargetSqlDialect.Sqlite).Code);
        Assert.Equal(GovernedSqlErrorCodes.AffectedRowsLimit, new DmlAffectedRowsLimitException(TargetSqlDialect.Sqlite).Code);
    }

    // ---- cache key and audit digest ----

    [Fact]
    public void HashOf_IsTheUppercaseSha256OfTheUtf8Key_ForShortAndLongKeys()
    {
        Assert.Equal("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", CompiledSqlTemplateCache.HashOf("abc"));
        string small = new('é', 200);   // 400 bytes, below the stack-buffer limit
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(small))), CompiledSqlTemplateCache.HashOf(small));
        string large = new('é', 2000);   // above the stack-buffer limit
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(large))), CompiledSqlTemplateCache.HashOf(large));
    }

    [Fact]
    public void CompiledSqlDigest_IsAnHmacOverVersionDialectSqlAndTheParameterShape()
    {
        byte[] key = Encoding.UTF8.GetBytes("audit-key");
        var compiled = new CompiledSql("SELECT 1", ImmutableArray.Create(
                new BoundParameter("?1", "?1", 0, "secret", SqlParameterType.String, ParameterOrigin.Tenant),
                new BoundParameter("?2", "?2", 1, 5, SqlParameterType.Int32, ParameterOrigin.QueryLiteral)),
            TargetSqlDialect.Sqlite, SqlStatementClass.Select, ImmutableArray<SecurityPredicateId>.Empty, "ast-compiler/1.0");
        string expected = Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes("ast-compiler/1.0|Sqlite|SELECT 1|0:String:Tenant;1:Int32:QueryLiteral;")));
        Assert.Equal(expected, CompiledSqlDigest.Compute(key, compiled));

        var changed = compiled with { Parameters = ImmutableArray.Create(compiled.Parameters[0]) };
        Assert.NotEqual(expected, CompiledSqlDigest.Compute(key, changed));
        Assert.Equal("auditKey", Assert.Throws<ArgumentNullException>(() => CompiledSqlDigest.Compute(null!, compiled)).ParamName);
        Assert.Equal("compiled", Assert.Throws<ArgumentNullException>(() => CompiledSqlDigest.Compute(key, null!)).ParamName);
    }

    [Fact]
    public void CacheKeyMaterial_ListsStringCollectionsSortedAndClosed()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer);
        CompileRequest Request(params string[] functions) => new()
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = new InMemoryTableCatalog(Array.Empty<TableCatalogEntry>(), "dbo"),
                Tenant = new TenantBinding("__t", "acme", SqlParameterType.String)
            },
            AllowedFunctions = new HashSet<string>(functions)
        };

        string ab = CompileCacheKey.Material(Request("b", "a"), "SELECT 1", caps);
        Assert.Contains("AllowedFunctions={\"1:a\"\"1:b\"},", ab, StringComparison.Ordinal);
        Assert.Equal(ab, CompileCacheKey.Material(Request("a", "b"), "SELECT 1", caps));
        Assert.StartsWith("v=ast-compiler/1.0;cap=", ab, StringComparison.Ordinal);
        Assert.EndsWith(";maxq=0;sql=8:SELECT 1", ab, StringComparison.Ordinal);
    }

    [Fact]
    public void BoundParameterToString_NeverContainsTheValue()
    {
        var p = new BoundParameter("?1", "?1", 0, "top-secret", SqlParameterType.String, ParameterOrigin.Tenant, "t", "varchar(5)");
        Assert.Equal("BoundParameter { Marker = ?1, Type = String, Origin = Tenant }", p.ToString());
    }

    [Fact]
    public void CheckOptionEnforce_RequiresAStatement_AndComparesTheExpectedCount()
    {
        Assert.Equal("compiled", Assert.Throws<ArgumentNullException>(() => DmlCheckOption.Enforce(null!, 1)).ParamName);
        var insert = new CompiledSql("INSERT INTO t SELECT 1", NoParams, TargetSqlDialect.Sqlite, SqlStatementClass.Insert, ImmutableArray<SecurityPredicateId>.Empty, "v")
        {
            ExpectedAffectedRows = 2
        };
        DmlCheckOption.Enforce(insert, 2);
        Assert.Throws<DmlCheckOptionViolationException>(() => DmlCheckOption.Enforce(insert, 1));
        Assert.Throws<DmlCheckOptionViolationException>(() => DmlCheckOption.Enforce(insert, 3));
        DmlCheckOption.Enforce(insert with { }, 2);
        DmlCheckOption.Enforce(new CompiledSql("UPDATE t SET a = 1", NoParams, TargetSqlDialect.Sqlite, SqlStatementClass.Update, ImmutableArray<SecurityPredicateId>.Empty, "v"), 99);
    }

    [Theory]
    [InlineData("nvarchar(10)", ColumnTypeClass.UnicodeText)]
    [InlineData("NCHAR(3)", ColumnTypeClass.UnicodeText)]
    [InlineData("ntext", ColumnTypeClass.UnicodeText)]
    [InlineData("nvarchar2(5)", ColumnTypeClass.UnicodeText)]
    [InlineData("nclob", ColumnTypeClass.UnicodeText)]
    [InlineData("national character varying", ColumnTypeClass.UnicodeText)]
    [InlineData("national char", ColumnTypeClass.UnicodeText)]
    [InlineData("varchar(max)", ColumnTypeClass.AnsiText)]
    [InlineData("char(1)", ColumnTypeClass.AnsiText)]
    [InlineData("text", ColumnTypeClass.AnsiText)]
    [InlineData("varchar2(10)", ColumnTypeClass.AnsiText)]
    [InlineData("clob", ColumnTypeClass.AnsiText)]
    [InlineData("character varying(10)", ColumnTypeClass.AnsiText)]
    [InlineData("character", ColumnTypeClass.AnsiText)]
    [InlineData("citext", ColumnTypeClass.AnsiText)]
    [InlineData("int", ColumnTypeClass.Numeric)]
    [InlineData("integer", ColumnTypeClass.Numeric)]
    [InlineData("bigint", ColumnTypeClass.Numeric)]
    [InlineData("smallint", ColumnTypeClass.Numeric)]
    [InlineData("tinyint", ColumnTypeClass.Numeric)]
    [InlineData("decimal(10,2)", ColumnTypeClass.Numeric)]
    [InlineData("numeric(10, 2)", ColumnTypeClass.Numeric)]
    [InlineData("number", ColumnTypeClass.Numeric)]
    [InlineData("float", ColumnTypeClass.Numeric)]
    [InlineData("real", ColumnTypeClass.Numeric)]
    [InlineData("double", ColumnTypeClass.Numeric)]
    [InlineData("double precision", ColumnTypeClass.Numeric)]
    [InlineData("money", ColumnTypeClass.Numeric)]
    [InlineData("smallmoney", ColumnTypeClass.Numeric)]
    [InlineData("binary_float", ColumnTypeClass.Numeric)]
    [InlineData("binary_double", ColumnTypeClass.Numeric)]
    [InlineData("date", ColumnTypeClass.Other)]
    [InlineData("uuid", ColumnTypeClass.Other)]
    [InlineData("1abc", ColumnTypeClass.Unknown)]
    [InlineData("", ColumnTypeClass.Unknown)]
    [InlineData("   ", ColumnTypeClass.Unknown)]
    public void Classify_MapsEveryKnownTypeName(string dataType, ColumnTypeClass expected) =>
        Assert.Equal(expected, ColumnBindAdapter.Classify(dataType));

    [Fact]
    public void Classify_NullIsUnknown() => Assert.Equal(ColumnTypeClass.Unknown, ColumnBindAdapter.Classify(null));

    [Fact]
    public void CacheKeyMaterial_ListsRequestPropertiesInOrdinalNameOrder()
    {
        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.SqlServer);
        var request = new CompileRequest
        {
            TargetDialect = TargetSqlDialect.SqlServer,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Policy = new GovernancePolicy
            {
                RowFilters = new DictPolicyProvider(),
                Masks = new DictMaskProvider(),
                Catalog = new InMemoryTableCatalog(Array.Empty<TableCatalogEntry>(), "dbo"),
                Tenant = new TenantBinding("__t", "acme", SqlParameterType.String)
            }
        };
        string key = CompileCacheKey.Material(request, "SELECT 1", caps);
        string[] names = { "AllowExperimentalDialect=", "AllowedFunctions=", "AllowedTableFunctions=", "CompileTimeout=", "EnforceCatalogProjection=", "EnforcedMaxRows=", "TargetDialect=", "TokenGuards=" };
        int previous = -1;
        foreach (var name in names)
        {
            int at = key.IndexOf(name, StringComparison.Ordinal);
            Assert.True(at > previous, $"{name} must follow the preceding property in the key: {key}");
            previous = at;
        }

        Assert.Contains("CompileRequest(", key, StringComparison.Ordinal);
    }
}
