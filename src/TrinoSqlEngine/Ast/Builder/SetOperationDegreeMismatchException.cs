namespace TrinoSqlEngine.Ast.Builder;

using System;

/// <summary>
/// #321 (SEC-RG-03): the operands of a UNION, INTERSECT or EXCEPT project a different number of columns. A caller mistake that is
/// answered before the provider is reached. The message is fixed: no column text, no count.
/// </summary>
public sealed class SetOperationDegreeMismatchException : Exception
{
    public SetOperationDegreeMismatchException()
        : base("The operands of a set operation must have the same number of columns.")
    {
    }
}
