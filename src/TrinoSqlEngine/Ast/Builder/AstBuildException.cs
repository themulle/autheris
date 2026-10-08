namespace TrinoSqlEngine.Ast.Builder;

using System;

/// <summary>
/// Exception thrown when AST building fails due to invalid or unmappable syntax.
/// </summary>
public class AstBuildException : Exception
{
    public AstBuildException(string message) : base(message) { }
    public AstBuildException(string message, Exception innerException) : base(message, innerException) { }
}
