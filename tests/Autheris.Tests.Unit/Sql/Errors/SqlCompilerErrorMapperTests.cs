namespace Autheris.Tests.Unit.Sql.Errors;

using System;
using System.Security;
using Antlr4.Runtime.Misc;
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
        { "coverage", () => new SecurityCoverageException(HostileMarker), 500, "SQL_COMPILER_INVARIANT", null },
        { "config", () => new SqlCompileConfigurationException(), 500, "SQL_COMPILER_INVARIANT", null },
        { "checked-required", () => new CheckedExecutionRequiredException(TargetSqlDialect.PostgreSql), 500, "DML_CHECKED_EXECUTION_REQUIRED", null },
        { "timeout", () => new TimeoutException(HostileMarker), 504, "SQL_TIMEOUT", null },
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
    public void Map_WrappedInWebSqlPolicyException_UsesInnerCode()
    {
        var wrapped = new WebSqlPolicyException("outer", new SqlLimitExceededException(SqlLimitKind.QueryLength, TargetSqlDialect.SqlServer, 5, 4));

        SqlCompilerErrorMapper.TryMap(wrapped, out var mapping).ShouldBeTrue();

        mapping.Code.ShouldBe("SQL_LIMIT_EXCEEDED");
        mapping.Limit.ShouldBe("QueryLength");
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
}
