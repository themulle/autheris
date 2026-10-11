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

    /// <summary>The fixed, value-free message of a code. It never carries driver text, values or object names.</summary>
    public static string FixedMessage(string code) => code switch
    {
        ConstraintViolation => "The statement violated a data constraint.",
        DataError => "The statement contained a value that could not be stored or converted.",
        CheckOptionViolation => "The statement wrote a row that the row policy does not permit.",
        CheckedExecutionRequired => "The statement must run through the checked DML executor.",
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
