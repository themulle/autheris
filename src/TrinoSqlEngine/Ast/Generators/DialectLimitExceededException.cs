namespace TrinoSqlEngine.Ast.Generators;

using System;

/// <summary>
/// Thrown when a SQL query exceeds backend dialect limits (e.g. parameter budget).
/// </summary>
public class DialectLimitExceededException : Exception
{
    public DialectLimitExceededException(string message) : base(message) { }
    public DialectLimitExceededException(string message, Exception innerException) : base(message, innerException) { }
}
