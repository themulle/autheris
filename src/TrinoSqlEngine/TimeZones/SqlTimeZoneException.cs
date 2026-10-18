namespace TrinoSqlEngine;

using TrinoSqlEngine.Ast.Emit;

/// <summary>
/// REQ-SQL-130: a time zone conversion was refused. Carries only a stable code (<c>SQL_TIMEZONE_*</c> or
/// <c>SQL_FUNCTION_UNSUPPORTED_DIALECT</c>); the message is the fixed message of the code and never contains the zone, the timestamp,
/// a count or a dialect-specific zone id. It derives from <see cref="Exception"/>, not from <c>SecurityException</c>, so the generic
/// 403 row of the error mapper can never absorb it.
/// </summary>
public sealed class SqlTimeZoneException(string code) : Exception(GovernedSqlErrorCodes.FixedMessage(code))
{
    public string Code { get; } = code;
}
