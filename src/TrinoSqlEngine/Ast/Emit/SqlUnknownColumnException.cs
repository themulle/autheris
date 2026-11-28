namespace TrinoSqlEngine.Ast.Emit;

/// <summary>
/// The client statement references a column that does not exist on tables that are fully visible to the caller
/// (<see cref="GovernedSqlErrorCodes.UnknownColumn"/>). The name comes from the client statement text, never from driver text.
/// </summary>
public sealed class SqlUnknownColumnException : GovernedSqlException
{
    public string ColumnName { get; }

    public int? Line { get; }

    public int? Column { get; }

    public SqlUnknownColumnException(string columnName, int? line, int? column, TargetSqlDialect dialect)
        : base(GovernedSqlErrorCodes.UnknownColumn, dialect)
    {
        ColumnName = columnName;
        Line = line;
        Column = column;
    }
}
