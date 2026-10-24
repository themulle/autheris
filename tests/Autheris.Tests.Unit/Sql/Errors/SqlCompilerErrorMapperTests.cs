namespace Autheris.Tests.Unit.Sql.Errors;

using System;
using System.Security;
using Antlr4.Runtime.Misc;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Services;
using TrinoSqlEngine.Analysis;
using Autheris.Domain.Exceptions;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>WP-X0a: the one mapping of compiler and DML errors to status, stable code and fixed message (plan section 28.4.3).</summary>
public sealed class SqlCompilerErrorMapperTests
{
    private const string HostileMarker = "A-17'--";

    private sealed class SqliteLikeProviderException(string message) : System.Data.Common.DbException(message)
    {
        public int SqliteErrorCode => 1;
    }

    private static TableAccessTarget Target(string table) => new("ds", "dbo", table, null, $"ds.dbo.{table}");

    public static TheoryData<string, Func<Exception>, int, string, string?> Rows => new()
    {
        { "parse-canceled", () => new ParseCanceledException(HostileMarker), 400, "SQL_SYNTAX_ERROR", null },
        { "parse-canceled-inner", () => new InvalidOperationException(HostileMarker, new ParseCanceledException(HostileMarker)), 400, "SQL_SYNTAX_ERROR", null },
        { "missing-param", () => new MissingClientParameterException("secret_name"), 400, "SQL_PARAMETER_MISSING", null },
        { "construct", () => new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Construct, HostileMarker), 422, "SQL_CONSTRUCT_NOT_SUPPORTED", null },
        { "ast-build", () => new AstBuildException(HostileMarker), 422, "SQL_CONSTRUCT_NOT_SUPPORTED", null },
        { "statement-class", () => new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.StatementClass, HostileMarker), 403, "SQL_STATEMENT_NOT_PERMITTED", null },
        { "dialect", () => new SqlCompileNotSupportedException(SqlCompileNotSupportedReason.Dialect, HostileMarker), 501, "SQL_DIALECT_NOT_SUPPORTED", null },
        { "limit", () => new SqlLimitExceededException(SqlLimitKind.InListItems, TargetSqlDialect.Oracle, 1001, 1000), 422, "SQL_LIMIT_EXCEEDED", "InListItems" },
        // FastSqlEngine.RunWithParseBudget throws exactly this shape for a parser time budget overrun and for parser-slot exhaustion.
        { "parser-timeout", () => new ParseCanceledException("SQL parsing exceeded the time budget.", new TimeoutException("budget")), 422, "SQL_LIMIT_EXCEEDED", "CompileTime" },
        { "parser-slot-exhaustion", () => new ParseCanceledException("no slot", new TimeoutException("No parser thread slot became available within the parse time budget.")), 422, "SQL_LIMIT_EXCEEDED", "CompileTime" },
        { "parser-timeout-wrapped", () => new InvalidOperationException(HostileMarker, new ParseCanceledException("x", new TimeoutException())), 422, "SQL_LIMIT_EXCEEDED", "CompileTime" },
        { "parse-canceled-recognition", () => new ParseCanceledException("line 1:3: mismatched input", new InvalidOperationException("syntax")), 400, "SQL_SYNTAX_ERROR", null },
        { "parser-budget", () => new OperationCanceledException(HostileMarker, new TimeoutException()), 422, "SQL_LIMIT_EXCEEDED", "CompileTime" },
        { "unfiltered-dml", () => new UnfilteredDmlException(HostileMarker), 403, "SQL_UNFILTERED_DML", null },
        { "policy-parse", () => new PolicyParseException(HostileMarker), 403, "SQL_POLICY_DENIED", null },
        { "table-denied", () => SqlDataSourceResolver.TableDenied(Target("secret_table")), 403, "SQL_POLICY_DENIED", null },
        { "policy-violation", () => new WebSqlPolicyException(HostileMarker), 403, "SQL_POLICY_VIOLATION", null },
        { "other-security", () => new SecurityException(HostileMarker), 403, "SQL_POLICY_VIOLATION", null },
        { "tenant-collision", () => new GatewaySecurityException(HostileMarker, "TENANT_ID_COLLISION"), 403, "TENANT_ID_COLLISION", null },
        { "check-option", () => new DmlCheckOptionViolationException(TargetSqlDialect.PostgreSql), 403, "DML_CHECK_OPTION_VIOLATION", null },
        { "constraint", () => new DmlConstraintViolationException(DmlConstraintKind.Unique, TargetSqlDialect.PostgreSql), 409, "DML_CONSTRAINT_VIOLATION", null },
        { "affected-rows", () => new GovernedSqlException(GovernedSqlErrorCodes.AffectedRowsLimit, TargetSqlDialect.PostgreSql), 422, "DML_AFFECTED_ROWS_LIMIT", null },
        { "data-error", () => new GovernedSqlException(GovernedSqlErrorCodes.DataError, TargetSqlDialect.PostgreSql), 422, "SQL_DATA_ERROR", null },
        { "provider-error", () => new GovernedSqlException(GovernedSqlErrorCodes.ProviderError, TargetSqlDialect.PostgreSql), 502, "SQL_PROVIDER_ERROR", null },
        { "recursion-limit", () => new GovernedSqlException(GovernedSqlErrorCodes.LimitExceeded, TargetSqlDialect.SqlServer), 422, "SQL_LIMIT_EXCEEDED", null },
        // REQ-SQL-130 (T-27): one stable code per cause; the exception is not a SecurityException, so it is never the generic 403.
        { "timezone-invalid", () => new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneInvalid), 400, "SQL_TIMEZONE_INVALID", null },
        { "timezone-argument", () => new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneArgument), 422, "SQL_TIMEZONE_ARGUMENT", null },
        { "timezone-unsupported-dialect", () => new SqlTimeZoneException(GovernedSqlErrorCodes.FunctionUnsupportedDialect), 422, "SQL_FUNCTION_UNSUPPORTED_DIALECT", null },
        // GAP-002 (#308): the mapping is 422 for a function the dialect cannot offer.
        // #296 (SEC-PG-08): the typed literal rejection; a plain ParseCanceledException stays SQL_SYNTAX_ERROR (row "parse-canceled").
        { "literal-token-rejected", () => new ParseCanceledException("line 1:8: reason", new LiteralTokenRejectedException()), 400, "SQL_LITERAL_NOT_PERMITTED", null },
        { "literal-token-rejected-wrapped", () => new InvalidOperationException(HostileMarker, new ParseCanceledException("x", new LiteralTokenRejectedException())), 400, "SQL_LITERAL_NOT_PERMITTED", null },
        // #310 (SEC-PG-09)
        { "aggregate-type-invalid", () => new TrinoSqlEngine.Ast.Visitors.AggregateTypeInvalidException(), 400, "AGGREGATION_INVALID_FUNCTION_FOR_TYPE", null },
        // #312 (SEC-PG-10): the executor's classification of an unknown identifier answers like a denied column; an unrecognised provider error stays 502.
        { "governed-policy-denied", () => new GovernedSqlException(GovernedSqlErrorCodes.PolicyDenied, TargetSqlDialect.Sqlite), 403, "SQL_POLICY_DENIED", null },
        { "governed-provider-error", () => new GovernedSqlException(GovernedSqlErrorCodes.ProviderError, TargetSqlDialect.Sqlite), 502, "SQL_PROVIDER_ERROR", null },
        // #321 (SEC-RG-03): a set operation column-count mismatch (AST check and provider classification) is a 400, never a 502.
        { "set-operation-degree", () => new SetOperationDegreeMismatchException(), 400, "SQL_SYNTAX_ERROR", null },
        { "governed-syntax-error", () => new GovernedSqlException(GovernedSqlErrorCodes.SyntaxError, TargetSqlDialect.Sqlite), 400, "SQL_SYNTAX_ERROR", null },
        { "timezone-unavailable", () => new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneUnavailable), 503, "SQL_TIMEZONE_UNAVAILABLE", null },
        { "timezone-wrapped", () => new InvalidOperationException(HostileMarker, new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneInvalid)), 400, "SQL_TIMEZONE_INVALID", null },
        { "timezone-unknown-code", () => new SqlTimeZoneException("SQL_SOMETHING_ELSE"), 500, "SQL_COMPILER_INVARIANT", null },
        { "coverage", () => new SecurityCoverageException(HostileMarker), 500, "SQL_COMPILER_INVARIANT", null },
        { "config", () => new SqlCompileConfigurationException(), 500, "SQL_COMPILER_INVARIANT", null },
        { "checked-required", () => new CheckedExecutionRequiredException(TargetSqlDialect.PostgreSql), 500, "DML_CHECKED_EXECUTION_REQUIRED", null },
        { "timeout", () => new TimeoutException(HostileMarker), 504, "SQL_TIMEOUT", null },
        // X1 / Phase 5 M4: a data source that cannot hand out a correctly pinned session (SessionPinning, RequireDefaultSchema).
        // X1 / Phase 5 L3: a reserved identifier is a user error (400), never a gateway defect.
        { "reserved-identifier", () => new ReservedIdentifierException(), 400, "SQL_RESERVED_IDENTIFIER", null },
        { "reserved-identifier-wrapped", () => new InvalidOperationException(HostileMarker, new ReservedIdentifierException()), 400, "SQL_RESERVED_IDENTIFIER", null },
        { "source-unavailable", () => new SqlSourceUnavailableException(HostileMarker), 503, "SQL_SOURCE_UNAVAILABLE", null },
        { "source-unavailable-wrapped", () => new InvalidOperationException(HostileMarker, new SqlSourceUnavailableException(HostileMarker)), 503, "SQL_SOURCE_UNAVAILABLE", null },
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void Map_EveryRowOfTable28_4_3(string row, Func<Exception> factory, int status, string code, string? limit)
    {
        SqlCompilerErrorMapper.TryMap(factory(), out var mapping).ShouldBeTrue(row);

        mapping.Status.ShouldBe(status, row);
        mapping.Code.ShouldBe(code, row);
        mapping.Limit.ShouldBe(limit, row);
        mapping.Title.ShouldBe(GovernedSqlErrorCodes.FixedMessage(code), row);
        mapping.Title.ShouldNotContain(HostileMarker);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void SetOperationColumnCountProviderError_Maps400_NoProviderText()
    {
        var provider = new SqliteLikeProviderException(HostileMarker + " SELECTs to the left and right of UNION do not have the same number of result columns");
        var mapped = DmlErrorSanitizer.Map(TargetSqlDialect.Sqlite, provider);

        SqlCompilerErrorMapper.TryMap(mapped, out var mapping).ShouldBeTrue();

        mapping.Status.ShouldBe(400);
        mapping.Code.ShouldBe("SQL_SYNTAX_ERROR");
        mapping.Title.ShouldNotContain(HostileMarker);
        mapping.Title.ShouldNotContain("UNION");
    }

    [Fact]
    public void Map_WrappedInWebSqlPolicyException_UsesInnerCode()
    {
        var wrapped = new WebSqlPolicyException("outer", new SqlLimitExceededException(SqlLimitKind.QueryLength, TargetSqlDialect.SqlServer, 5, 4));

        SqlCompilerErrorMapper.TryMap(wrapped, out var mapping).ShouldBeTrue();

        mapping.Code.ShouldBe("SQL_LIMIT_EXCEEDED");
        mapping.Limit.ShouldBe("QueryLength");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Map_ATimeZoneErrorIsNeverTheGenericPolicyViolation_EvenInsideASecurityException()
    {
        var wrapped = new SecurityException("outer", new SqlTimeZoneException(GovernedSqlErrorCodes.TimeZoneArgument));

        SqlCompilerErrorMapper.TryMap(wrapped, out var mapping).ShouldBeTrue();

        mapping.Status.ShouldBe(422);
        mapping.Code.ShouldBe("SQL_TIMEZONE_ARGUMENT");
        mapping.Title.ShouldBe("The time zone conversion arguments are not valid.");
    }

    [Fact]
    public void Map_SecurityExceptionWrappingDataError_IsNeverDowngradedToA4xxDataCode()
    {
        var wrapped = new SecurityException("outer", new GovernedSqlException(GovernedSqlErrorCodes.DataError, TargetSqlDialect.Oracle));

        SqlCompilerErrorMapper.TryMap(wrapped, out var mapping).ShouldBeTrue();

        mapping.Code.ShouldBe("SQL_POLICY_VIOLATION");
    }

    [Fact]
    public void Map_DepthBeyondEight_IsNotWalked()
    {
        Exception deep = new ParseCanceledException("x");
        for (var i = 0; i < 9; i++)
        {
            deep = new InvalidOperationException("w", deep);
        }

        SqlCompilerErrorMapper.TryMap(deep, out _).ShouldBeFalse();
    }

    [Fact]
    public void Map_UnknownException_ReturnsFalse()
    {
        SqlCompilerErrorMapper.TryMap(new InvalidOperationException("boom"), out _).ShouldBeFalse();
    }

    [Fact]
    public void Map_UnregisteredAndDeniedTable_AreByteIdentical()
    {
        SqlCompilerErrorMapper.TryMap(SqlDataSourceResolver.TableDenied(Target("exists")), out var a).ShouldBeTrue();
        SqlCompilerErrorMapper.TryMap(new PolicyConflictException("exists"), out var b).ShouldBeTrue();
        SqlCompilerErrorMapper.TryMap(SqlDataSourceResolver.TableDenied(Target("missing")), out var c).ShouldBeTrue();

        b.ShouldBe(a);
        c.ShouldBe(a);
    }

    [Fact]
    public void LimitResponse_CarriesKindOnly_NoCounts()
    {
        SqlCompilerErrorMapper.TryMap(new SqlLimitExceededException(SqlLimitKind.BindParameters, TargetSqlDialect.Oracle, 987654, 321), out var mapping).ShouldBeTrue();

        mapping.Limit.ShouldBe("BindParameters");
        mapping.Title.ShouldNotContain("987654");
        mapping.Title.ShouldNotContain("321");
    }

    [Fact]
    public void TimeoutBeforeSanitizer_Stays504()
    {
        SqlCompilerErrorMapper.TryMap(new InvalidOperationException("wrap", new TimeoutException()), out var mapping).ShouldBeTrue();

        mapping.Status.ShouldBe(504);
        mapping.Code.ShouldBe("SQL_TIMEOUT");
    }

    [Fact]
    public void FixedMessage_IsDefinedForEveryMappedCode()
    {
        foreach (var row in Rows)
        {
            var factory = (Func<Exception>)row[1];
            SqlCompilerErrorMapper.TryMap(factory(), out var mapping).ShouldBeTrue();
            mapping.Title.ShouldNotBe(GovernedSqlErrorCodes.FixedMessage("UNKNOWN_CODE"), mapping.Code);
        }
    }

    [Fact]
    public void FunctionUnsupportedDialect_Is422()
    {
        SqlCompilerErrorMapper.TryMap(new SqlTimeZoneException(GovernedSqlErrorCodes.FunctionUnsupportedDialect), out var mapping).ShouldBeTrue();
        mapping.Status.ShouldBe(422);
        mapping.Code.ShouldBe("SQL_FUNCTION_UNSUPPORTED_DIALECT");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void FunctionUnsupportedDialect_FixedMessage()
    {
        // SEC-PG-11: the message is the fixed text of the code, whatever the exception carried.
        var error = new InvalidOperationException(HostileMarker, new SqlTimeZoneException(GovernedSqlErrorCodes.FunctionUnsupportedDialect));
        SqlCompilerErrorMapper.TryMap(error, out var mapping).ShouldBeTrue();
        mapping.Title.ShouldBe("The function is not supported for this data source dialect.");
        mapping.Title.ShouldNotContain(HostileMarker);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void OtherParseCanceled_StaysSyntaxError()
    {
        // SEC-PG-08: only the typed literal cause is SQL_LITERAL_NOT_PERMITTED; every other parse rejection keeps the fail-closed default.
        SqlCompilerErrorMapper.TryMap(new ParseCanceledException("line 1:1: Square brackets are not permitted", new InvalidOperationException()), out var mapping).ShouldBeTrue();
        mapping.Code.ShouldBe("SQL_SYNTAX_ERROR");
        mapping.Status.ShouldBe(400);
    }
}
