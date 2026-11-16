using TrinoSqlEngine.Ast.Emit;
using Xunit;

namespace TrinoSqlEngine.Tests.Compiler;

/// <summary>
/// Mutation-gate tests (M-12) of the governed error surface: every stable code keeps its wire string and its fixed, value-free message
/// (clients and the error mapping rely on both), an unknown code falls back to the generic message, and the exception types carry
/// exactly the code, dialect and message they are documented to carry.
/// </summary>
[Trait("Category", "SqlGate")]
public class GovernedSqlErrorCodesTests
{
    public static IEnumerable<object[]> CodesAndMessages() =>
    [
        [GovernedSqlErrorCodes.MethodNotAllowed, "This SQL endpoint cannot be executed via HTTP GET."],
        [GovernedSqlErrorCodes.WriteBudgetExhausted, "Write budget exhausted for this tenant and identity. Retry after the budget window resets."],
        [GovernedSqlErrorCodes.DmlWriteConnectionMissing, "DML execution is not enabled for this data source because no dedicated write connection string is configured."],
        [GovernedSqlErrorCodes.QueryCostExceeded, "The statement cost exceeds the allowed limit. Add a LIMIT, a filter or fewer joins."],
        [GovernedSqlErrorCodes.RateLimitExceeded, "Query cost quota exhausted."],
        [GovernedSqlErrorCodes.ServiceUnavailable, "The service is temporarily unavailable."],
        [GovernedSqlErrorCodes.PagingUnsupported, "Statement paging is not supported for this request."],
        [GovernedSqlErrorCodes.PagingTokenInvalid, "The paging token is invalid or expired."],
        [GovernedSqlErrorCodes.ConstraintViolation, "The statement violated a data constraint."],
        [GovernedSqlErrorCodes.DataError, "The statement contained a value that could not be stored or converted."],
        [GovernedSqlErrorCodes.CheckOptionViolation, "The statement wrote a row that the row policy does not permit."],
        [GovernedSqlErrorCodes.CheckedExecutionRequired, "The statement must run through the checked DML executor."],
        [GovernedSqlErrorCodes.ProviderError, "The data source could not execute the statement."],
        [GovernedSqlErrorCodes.SyntaxError, "Invalid SQL syntax."],
        [GovernedSqlErrorCodes.ParameterMissing, "A required statement parameter was not supplied."],
        [GovernedSqlErrorCodes.ConstructNotSupported, "The statement uses a construct that is not supported."],
        [GovernedSqlErrorCodes.StatementNotPermitted, "This kind of statement is not permitted."],
        [GovernedSqlErrorCodes.DialectNotSupported, "The statement is not supported for this data source dialect."],
        [GovernedSqlErrorCodes.LimitExceeded, "The statement exceeds a resource limit."],
        [GovernedSqlErrorCodes.UnfilteredDml, "UPDATE and DELETE statements require a restricting WHERE clause."],
        [GovernedSqlErrorCodes.AggregationOnMaskedColumn, "Masked columns cannot be aggregated or grouped."],
        [GovernedSqlErrorCodes.FilterOnMaskedColumn, "Masked columns cannot be used in filters, joins, or ordering."],
        [GovernedSqlErrorCodes.EndpointDisabled, "The SQL endpoint is disabled by configuration."],
        [GovernedSqlErrorCodes.ReservedIdentifier, "The statement uses an identifier that is reserved by the gateway."],
        [GovernedSqlErrorCodes.PolicyDenied, "Access to a referenced table or column is denied."],
        [GovernedSqlErrorCodes.PolicyViolation, "The SQL statement was rejected by the gateway security policy."],
        [GovernedSqlErrorCodes.TenantIdCollision, "Access denied."],
        [GovernedSqlErrorCodes.AffectedRowsLimit, "The statement would change more rows than permitted."],
        [GovernedSqlErrorCodes.CompilerInvariant, "The statement could not be processed."],
        [GovernedSqlErrorCodes.Timeout, "The SQL statement exceeded the execution time limit. Narrow the query (filter, fewer columns, LIMIT) or contact support with the trace id."],
        [GovernedSqlErrorCodes.SourceUnavailable, "The data source is temporarily unavailable. Retry later."],
        [GovernedSqlErrorCodes.StatementRejected, "The data source rejected the SQL statement."],
        [GovernedSqlErrorCodes.ListParamPosition, "A list parameter is only allowed as the single item of an IN or NOT IN list."],
        [GovernedSqlErrorCodes.ListParamType, "A list parameter has an invalid or mixed element type, or a scalar parameter received a list."],
        [GovernedSqlErrorCodes.ListParamNull, "A list parameter contains an element that is not allowed (null, or text the data source cannot store as given)."],
        [GovernedSqlErrorCodes.ListParamTooLarge, "A list parameter exceeds the maximum number of values."],
        [GovernedSqlErrorCodes.ListParamUnsupported, "List parameters are not supported for this statement."],
        [GovernedSqlErrorCodes.TimeZoneInvalid, "The time zone is not supported."],
        [GovernedSqlErrorCodes.TimeZoneArgument, "The time zone conversion arguments are not valid."],
        [GovernedSqlErrorCodes.FunctionUnsupportedDialect, "The function is not supported for this data source dialect."],
        [GovernedSqlErrorCodes.TimeZoneUnavailable, "Time zone conversion is temporarily unavailable."],
        [GovernedSqlErrorCodes.RequiredFilterColumnMissing, "A required filter column was not constrained by the query or an active row policy."],
        [GovernedSqlErrorCodes.InvalidIdentifierQualification, "Database-qualified or server-qualified object names are not permitted."],
        [GovernedSqlErrorCodes.LiteralNotPermitted, "A string literal contains a character sequence that is not permitted; pass the value as a parameter."],
        [GovernedSqlErrorCodes.AggregationInvalidFunctionForType, "The aggregate function is not valid for the type of its argument."],
    ];

    [Theory]
    [MemberData(nameof(CodesAndMessages))]
    public void FixedMessage_IsExactlyThePublishedMessage(string code, string message)
    {
        Assert.Equal(message, GovernedSqlErrorCodes.FixedMessage(code));
        var exception = new GovernedSqlException(code, TargetSqlDialect.PostgreSql);
        Assert.Equal(message, exception.Message);
        Assert.Equal(code, exception.Code);
        Assert.Null(exception.InnerException);
    }

    [Fact]
    public void CodeConstants_KeepTheirWireStrings()
    {
        var expected = new Dictionary<string, string>
        {
            ["ConstraintViolation"] = "DML_CONSTRAINT_VIOLATION",
            ["DataError"] = "SQL_DATA_ERROR",
            ["ProviderError"] = "SQL_PROVIDER_ERROR",
            ["CheckOptionViolation"] = "DML_CHECK_OPTION_VIOLATION",
            ["CheckedExecutionRequired"] = "DML_CHECKED_EXECUTION_REQUIRED",
            ["SyntaxError"] = "SQL_SYNTAX_ERROR",
            ["ParameterMissing"] = "SQL_PARAMETER_MISSING",
            ["ConstructNotSupported"] = "SQL_CONSTRUCT_NOT_SUPPORTED",
            ["StatementNotPermitted"] = "SQL_STATEMENT_NOT_PERMITTED",
            ["DialectNotSupported"] = "SQL_DIALECT_NOT_SUPPORTED",
            ["LimitExceeded"] = "SQL_LIMIT_EXCEEDED",
            ["UnfilteredDml"] = "SQL_UNFILTERED_DML",
            ["AggregationOnMaskedColumn"] = "AGGREGATION_ON_MASKED_COLUMN",
            ["FilterOnMaskedColumn"] = "FILTER_ON_MASKED_COLUMN",
            ["EndpointDisabled"] = "SQL_ENDPOINT_DISABLED",
            ["ReservedIdentifier"] = "SQL_RESERVED_IDENTIFIER",
            ["PolicyDenied"] = "SQL_POLICY_DENIED",
            ["PolicyViolation"] = "SQL_POLICY_VIOLATION",
            ["TenantIdCollision"] = "TENANT_ID_COLLISION",
            ["AffectedRowsLimit"] = "DML_AFFECTED_ROWS_LIMIT",
            ["CompilerInvariant"] = "SQL_COMPILER_INVARIANT",
            ["Timeout"] = "SQL_TIMEOUT",
            ["SourceUnavailable"] = "SQL_SOURCE_UNAVAILABLE",
            ["StatementRejected"] = "SQL_STATEMENT_REJECTED",
            ["RequiredFilterColumnMissing"] = "REQUIRED_FILTER_COLUMN_MISSING",
            ["ListParamPosition"] = "SQL_LIST_PARAM_POSITION",
            ["ListParamType"] = "SQL_LIST_PARAM_TYPE",
            ["ListParamNull"] = "SQL_LIST_PARAM_NULL",
            ["ListParamTooLarge"] = "SQL_LIST_PARAM_TOO_LARGE",
            ["ListParamUnsupported"] = "SQL_LIST_PARAM_UNSUPPORTED",
            ["TimeZoneInvalid"] = "SQL_TIMEZONE_INVALID",
            ["TimeZoneArgument"] = "SQL_TIMEZONE_ARGUMENT",
            ["FunctionUnsupportedDialect"] = "SQL_FUNCTION_UNSUPPORTED_DIALECT",
            ["TimeZoneUnavailable"] = "SQL_TIMEZONE_UNAVAILABLE",
            ["LiteralNotPermitted"] = "SQL_LITERAL_NOT_PERMITTED",
            ["AggregationInvalidFunctionForType"] = "AGGREGATION_INVALID_FUNCTION_FOR_TYPE",
            ["InvalidIdentifierQualification"] = "INVALID_IDENTIFIER_QUALIFICATION",
            ["QueryCostExceeded"] = "QUERY_COST_QUOTA_EXCEEDED",
            ["RateLimitExceeded"] = "RATE_LIMIT_EXCEEDED",
            ["ServiceUnavailable"] = "SERVICE_UNAVAILABLE",
            ["PagingUnsupported"] = "SQL_PAGING_UNSUPPORTED",
            ["PagingTokenInvalid"] = "SQL_PAGING_TOKEN_INVALID",
            ["MethodNotAllowed"] = "METHOD_NOT_ALLOWED",
            ["WriteBudgetExhausted"] = "WRITE_BUDGET_EXHAUSTED",
            ["DmlWriteConnectionMissing"] = "DML_WRITE_CONNECTION_MISSING",
        };
        foreach (var (name, value) in expected)
        {
            var field = typeof(GovernedSqlErrorCodes).GetField(name);
            Assert.NotNull(field);
            Assert.Equal(value, (string?)field!.GetRawConstantValue());
        }

        Assert.Equal(expected.Count, typeof(GovernedSqlErrorCodes).GetFields().Length);
        Assert.Equal(expected.Count, expected.Values.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("")]
    [InlineData("NOT_A_CODE")]
    [InlineData("sql_syntax_error")]
    public void UnknownCode_FallsBackToTheGenericMessage(string code)
    {
        Assert.Equal("The statement could not be executed.", GovernedSqlErrorCodes.FixedMessage(code));
    }

    [Theory]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void SpecializedExceptions_CarryTheirCodeAndTheDialect(TargetSqlDialect dialect)
    {
        var check = new DmlCheckOptionViolationException(dialect);
        Assert.Equal(GovernedSqlErrorCodes.CheckOptionViolation, check.Code);
        Assert.Equal(dialect, check.Dialect);
        Assert.Equal("The statement wrote a row that the row policy does not permit.", check.Message);

        var required = new CheckedExecutionRequiredException(dialect);
        Assert.Equal(GovernedSqlErrorCodes.CheckedExecutionRequired, required.Code);
        Assert.Equal(dialect, required.Dialect);
        Assert.Equal("The statement must run through the checked DML executor.", required.Message);

        var filter = new RequiredFilterColumnMissingException(dialect);
        Assert.Equal(GovernedSqlErrorCodes.RequiredFilterColumnMissing, filter.Code);
        Assert.Equal(dialect, filter.Dialect);
    }

    [Fact]
    public void RequiredFilterColumnMissing_DefaultsToPostgreSql_AndNeverEchoesACustomMessage()
    {
        var byDefault = new RequiredFilterColumnMissingException();
        Assert.Equal(TargetSqlDialect.PostgreSql, byDefault.Dialect);

        var custom = new RequiredFilterColumnMissingException("table secret.orders column region");
        Assert.Equal(TargetSqlDialect.PostgreSql, custom.Dialect);
        Assert.Equal(GovernedSqlErrorCodes.RequiredFilterColumnMissing, custom.Code);
        Assert.Equal("A required filter column was not constrained by the query or an active row policy.", custom.Message);
        Assert.DoesNotContain("secret", custom.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CostAndQuotaExceptions_CarryTheirNumbersAndTheFixedMessage()
    {
        var cost = new SqlCostExceededException(120, 100);
        Assert.Equal(120, cost.EstimatedCost);
        Assert.Equal(100, cost.MaxAllowedCost);
        Assert.Equal("The statement cost exceeds the allowed limit. Add a LIMIT, a filter or fewer joins.", cost.Message);

        var quota = new SqlQuotaExhaustedException(42);
        Assert.Equal(42, quota.RetryAfterSeconds);
        Assert.Equal("Query cost quota exhausted.", quota.Message);

        Assert.Equal("The service is temporarily unavailable.", new SqlQuotaUnavailableException().Message);
    }

    [Fact]
    public void PagingExceptions_UseTheFixedMessageUnlessOneIsGiven()
    {
        Assert.Equal("Statement paging is not supported for this request.", new SqlPagingUnsupportedException().Message);
        Assert.Equal("custom unsupported", new SqlPagingUnsupportedException("custom unsupported").Message);
        Assert.Equal("The paging token is invalid or expired.", new SqlPagingTokenInvalidException().Message);
        Assert.Equal("custom token", new SqlPagingTokenInvalidException("custom token").Message);
    }
}
