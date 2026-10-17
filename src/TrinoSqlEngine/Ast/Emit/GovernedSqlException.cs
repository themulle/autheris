namespace TrinoSqlEngine.Ast.Emit;

using System;

/// <summary>Stable error codes of the governed compile and execute path (CR-ADG-34, CR-ADG-35). The codes never change.</summary>
public static class GovernedSqlErrorCodes
{
    public const string ConstraintViolation = "DML_CONSTRAINT_VIOLATION";
    public const string DataError = "SQL_DATA_ERROR";
    public const string ProviderError = "SQL_PROVIDER_ERROR";
    public const string CheckOptionViolation = "DML_CHECK_OPTION_VIOLATION";
    public const string CheckedExecutionRequired = "DML_CHECKED_EXECUTION_REQUIRED";

    // WP-X0a (plan section 28.4.3): codes of the compiler and execution error mapping.
    public const string SyntaxError = "SQL_SYNTAX_ERROR";
    public const string ParameterMissing = "SQL_PARAMETER_MISSING";
    public const string ConstructNotSupported = "SQL_CONSTRUCT_NOT_SUPPORTED";
    public const string StatementNotPermitted = "SQL_STATEMENT_NOT_PERMITTED";
    public const string DialectNotSupported = "SQL_DIALECT_NOT_SUPPORTED";
    public const string LimitExceeded = "SQL_LIMIT_EXCEEDED";
    public const string UnfilteredDml = "SQL_UNFILTERED_DML";

    /// <summary>A masked column was used as an aggregate argument or grouping expression.</summary>
    public const string AggregationOnMaskedColumn = "AGGREGATION_ON_MASKED_COLUMN";

    /// <summary>The statement uses an identifier with the reserved gateway prefix (a user error, 400; not a gateway defect).</summary>
    public const string ReservedIdentifier = "SQL_RESERVED_IDENTIFIER";

    /// <summary>One code for every "table not usable" cause (not registered, inactive, no columns, denied, hidden column, load failure; SEC-X1-05).</summary>
    public const string PolicyDenied = "SQL_POLICY_DENIED";

    /// <summary>Statement-level rules that do not depend on whether a table exists (DDL, function not permitted, read-only token, missing writer role).</summary>
    public const string PolicyViolation = "SQL_POLICY_VIOLATION";
    public const string TenantIdCollision = "TENANT_ID_COLLISION";
    public const string AffectedRowsLimit = "DML_AFFECTED_ROWS_LIMIT";
    public const string CompilerInvariant = "SQL_COMPILER_INVARIANT";
    public const string Timeout = "SQL_TIMEOUT";
    public const string SourceUnavailable = "SQL_SOURCE_UNAVAILABLE";

    // REQ-SQL-128: client list parameters. Fixed messages, never counts, names or values.
    public const string ListParamPosition = "SQL_LIST_PARAM_POSITION";
    public const string ListParamType = "SQL_LIST_PARAM_TYPE";
    public const string ListParamNull = "SQL_LIST_PARAM_NULL";
    public const string ListParamTooLarge = "SQL_LIST_PARAM_TOO_LARGE";
    public const string ListParamUnsupported = "SQL_LIST_PARAM_UNSUPPORTED";

    // REQ-SQL-130: time zone conversion. Fixed messages, never the zone, the timestamp, a count or the dialect-specific zone id.
    public const string TimeZoneInvalid = "SQL_TIMEZONE_INVALID";
    public const string TimeZoneArgument = "SQL_TIMEZONE_ARGUMENT";
    public const string FunctionUnsupportedDialect = "SQL_FUNCTION_UNSUPPORTED_DIALECT";
    public const string TimeZoneUnavailable = "SQL_TIMEZONE_UNAVAILABLE";

    /// <summary>The fixed, value-free message of a code. It never carries driver text, values or object names.</summary>
    public static string FixedMessage(string code) => code switch
    {
        ConstraintViolation => "The statement violated a data constraint.",
        DataError => "The statement contained a value that could not be stored or converted.",
        CheckOptionViolation => "The statement wrote a row that the row policy does not permit.",
        CheckedExecutionRequired => "The statement must run through the checked DML executor.",
        ProviderError => "The data source could not execute the statement.",
        SyntaxError => "Invalid SQL syntax.",
        ParameterMissing => "A required statement parameter was not supplied.",
        ConstructNotSupported => "The statement uses a construct that is not supported.",
        StatementNotPermitted => "This kind of statement is not permitted.",
        DialectNotSupported => "The statement is not supported for this data source dialect.",
        LimitExceeded => "The statement exceeds a resource limit.",
        UnfilteredDml => "UPDATE and DELETE statements require a restricting WHERE clause.",
        AggregationOnMaskedColumn => "Masked columns cannot be aggregated or grouped.",
        ReservedIdentifier => "The statement uses an identifier that is reserved by the gateway.",
        PolicyDenied => "Access to a referenced table or column is denied.",
        PolicyViolation => "The SQL statement was rejected by the gateway security policy.",
        TenantIdCollision => "Access denied.",
        AffectedRowsLimit => "The statement would change more rows than permitted.",
        CompilerInvariant => "The statement could not be processed.",
        Timeout => "The SQL statement exceeded the execution time limit. Narrow the query (filter, fewer columns, LIMIT) or contact support with the trace id.",
        SourceUnavailable => "The data source is temporarily unavailable. Retry later.",
        ListParamPosition => "A list parameter is only allowed as the single item of an IN or NOT IN list.",
        ListParamType => "A list parameter has an invalid or mixed element type, or a scalar parameter received a list.",
        ListParamNull => "A list parameter contains an element that is not allowed (null, or text the data source cannot store as given).",
        ListParamTooLarge => "A list parameter exceeds the maximum number of values.",
        ListParamUnsupported => "List parameters are not supported for this statement.",
        TimeZoneInvalid => "The time zone is not supported.",
        TimeZoneArgument => "The time zone conversion arguments are not valid.",
        FunctionUnsupportedDialect => "The function is not supported for this data source dialect.",
        TimeZoneUnavailable => "Time zone conversion is temporarily unavailable.",
        _ => "The statement could not be executed."
    };
}

/// <summary>
/// A provider error of the governed path, reduced to a stable code and a fixed message. No driver text, no value, no object name
/// and no inner exception (the inner exception would carry the driver message); the original error stays in the server-side log.
/// </summary>
public class GovernedSqlException : Exception
{
    public string Code { get; }

    public TargetSqlDialect Dialect { get; }

    public GovernedSqlException(string code, TargetSqlDialect dialect)
        : base(GovernedSqlErrorCodes.FixedMessage(code))
    {
        Code = code;
        Dialect = dialect;
    }
}

/// <summary>
/// CR-ADG-35: an INSERT would have written a row that the table's row policy does not permit (check-option semantics). The
/// statement was rolled back; nothing was written. The message carries no value and no name.
/// </summary>
public sealed class DmlCheckOptionViolationException : GovernedSqlException
{
    public DmlCheckOptionViolationException(TargetSqlDialect dialect)
        : base(GovernedSqlErrorCodes.CheckOptionViolation, dialect)
    {
    }
}

/// <summary>
/// CR-ADG-43: a statement with a row-count check (<see cref="CompiledSql.RequiresRowCountCheck"/>) was bound outside the checked
/// execution path. Running it unchecked would write rows the policy does not permit, so it is refused. Use
/// <see cref="CheckedDmlExecutor"/>.
/// </summary>
public sealed class CheckedExecutionRequiredException : GovernedSqlException
{
    public CheckedExecutionRequiredException(TargetSqlDialect dialect)
        : base(GovernedSqlErrorCodes.CheckedExecutionRequired, dialect)
    {
    }
}
