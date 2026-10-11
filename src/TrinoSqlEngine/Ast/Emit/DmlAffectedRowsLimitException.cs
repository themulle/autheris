namespace TrinoSqlEngine.Ast.Emit;

/// <summary>
/// X0k: a governed DML statement affected more rows than <see cref="CheckedDmlLimits.MaxAffectedRows"/> allows, or the provider did
/// not report a count while a limit is configured. The transaction was rolled back; nothing was written. The message carries no counts.
/// </summary>
public sealed class DmlAffectedRowsLimitException(TargetSqlDialect dialect)
    : GovernedSqlException(GovernedSqlErrorCodes.AffectedRowsLimit, dialect);
