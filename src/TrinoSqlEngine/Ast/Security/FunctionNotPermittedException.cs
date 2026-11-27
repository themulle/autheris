namespace TrinoSqlEngine.Ast.Security;

using System.Security;

/// <summary>
/// A function of a client statement is not permitted. Carries the function name as written in the client text and its 1-based position;
/// the message is for the server log only, response writers must go through <c>PublicSqlName</c>.
/// </summary>
public sealed class FunctionNotPermittedException : SecurityException
{
    public string FunctionName { get; }

    public int? Line { get; private set; }

    public int? Column { get; private set; }

    public FunctionNotPermittedException(string functionName, int? line, int? column)
        : base($"Function '{functionName}' is not permitted.")
    {
        FunctionName = functionName;
        Line = line;
        Column = column;
    }

    /// <summary>Forgets the position (it was computed against text the client did not write).</summary>
    public void DropPosition()
    {
        Line = null;
        Column = null;
    }
}
