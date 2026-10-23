namespace Autheris.Tests.Parity.Differential;

/// <summary>A tabular query result: column names and rows (values are the CLR values the producer yields: null, bool, long, double, decimal, string).</summary>
public sealed record ResultSet(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows);
