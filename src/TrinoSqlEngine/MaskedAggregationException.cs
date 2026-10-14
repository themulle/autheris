using System.Security;

namespace TrinoSqlEngine;

/// <summary>
/// A statement used a masked column as an aggregate argument or grouping expression (code <c>AGGREGATION_ON_MASKED_COLUMN</c>).
/// The message carries no column or table name.
/// </summary>
public sealed class MaskedAggregationException : SecurityException
{
    public MaskedAggregationException()
        : base("Masked columns must not be used in aggregate functions or GROUP BY.")
    {
    }
}
