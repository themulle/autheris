namespace TrinoSqlEngine.Ast.Builder;

using System;

/// <summary>
/// Exception thrown when AST building fails due to invalid or unmappable syntax.
/// </summary>
public class AstBuildException : Exception
{
    public AstBuildException(string message) : base(message) { }
    public AstBuildException(string message, Exception innerException) : base(message, innerException) { }

    /// <summary>A rejection of a catalogued statement-structure construct; the label and position are safe to show the caller.</summary>
    public AstBuildException(string message, string publicConstruct, int? line, int? column) : base(message)
    {
        PublicConstruct = publicConstruct;
        Line = line;
        Column = column;
    }

    /// <summary>The <see cref="SqlConstructCatalog"/> label of the rejected construct, or <c>null</c> when it must stay unnamed.</summary>
    public string? PublicConstruct { get; }

    /// <summary>1-based line of the construct in the client text, when known.</summary>
    public int? Line { get; private set; }

    /// <summary>1-based column of the construct in the client text, when known.</summary>
    public int? Column { get; private set; }

    /// <summary>Sets the position once; a position that is already known is kept.</summary>
    public void LocateAt(int line, int column)
    {
        if (Line is null)
        {
            Line = line;
            Column = column;
        }
    }
}
