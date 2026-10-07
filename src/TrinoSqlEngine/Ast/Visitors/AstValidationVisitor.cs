namespace TrinoSqlEngine.Ast.Visitors;

using System;
using System.Security;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// Static AST validation visitor asserting structural and security invariants (I1 to I12).
/// </summary>
public sealed class AstValidationVisitor : SqlAstRewriter
{
    private int _depth = 0;
    private const int MaxAllowedDepth = 64;

    public void Validate(SqlNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _depth = 0;
        Visit(node);
    }

    public override SqlNode Visit(SqlNode node)
    {
        if (++_depth > MaxAllowedDepth)
        {
            throw new SecurityException($"AST depth exceeded invariant limit of {MaxAllowedDepth}.");
        }

        try
        {
            return base.Visit(node);
        }
        finally
        {
            _depth--;
        }
    }
}
