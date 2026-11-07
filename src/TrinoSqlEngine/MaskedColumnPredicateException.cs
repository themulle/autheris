using System.Security;

namespace TrinoSqlEngine;

/// <summary>
/// A statement used a masked column in a filter, join, or ordering clause (code <c>FILTER_ON_MASKED_COLUMN</c>).
/// </summary>
public sealed class MaskedColumnPredicateException : SecurityException
{
    public MaskedColumnPredicateException()
        : base("Masked columns cannot be used in filters, joins, or ordering.")
    {
    }

    public MaskedColumnPredicateException(string message)
        : base(message)
    {
    }
}
