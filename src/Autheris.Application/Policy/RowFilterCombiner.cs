namespace Autheris.Application.Policy;

using System;

/// <summary>
/// Combines row filter predicates. Each operand is wrapped in parentheses so an operand containing OR cannot
/// change the meaning of the conjunction.
/// </summary>
public static class RowFilterCombiner
{
    /// <summary>
    /// ANDs the operands. Null, empty and whitespace operands are ignored; returns null when no operand remains.
    /// </summary>
    public static string? And(string? left, string? right)
    {
        var hasLeft = !string.IsNullOrWhiteSpace(left);
        var hasRight = !string.IsNullOrWhiteSpace(right);
        if (hasLeft && hasRight)
        {
            return $"({left}) AND ({right})";
        }

        if (hasLeft)
        {
            return $"({left})";
        }

        return hasRight ? $"({right})" : null;
    }
}
