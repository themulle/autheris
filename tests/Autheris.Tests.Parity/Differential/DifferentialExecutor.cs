using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Autheris.Tests.Parity.Matrix;
using Autheris.Tests.Parity.Robin;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Differential;

/// <summary>Runs a <see cref="DifferentialCase"/> through the product and the SQLite reference and compares the results (D-16).</summary>
public sealed class DifferentialExecutor(ParityHostFixture host)
{
    private readonly SqliteReference _reference = new(host.DataPath);
    private readonly MatrixExecutor _product = new(host);

    public async Task RunAsync(DifferentialCase c)
    {
        ResultSet reference;
        try
        {
            reference = _reference.Execute(c.ReferenceSql, c.Parameters);
        }
        catch (Exception ex) when (ex is not ParityHardFailureException)
        {
            // a broken reference is a defect of the corpus, never a product gap
            throw new ParityHardFailureException($"{c.Id}: the reference SQL is not valid SQLite: {ex.Message}");
        }

        var product = await _product.ExecuteAsync(c.Request);
        var mismatch = Evaluate(c, reference, product);
        if (mismatch is null)
        {
            return;
        }

        if (DifferentialAllowList.Find(c, product) is { } allowed)
        {
            ParityObservationsNote(allowed);
            return;
        }

        throw new XunitException($"differential mismatch {c.Id} on {c.Channel}: {mismatch}; reference SQL: {c.ReferenceSql}");
    }

    /// <summary>Null when the product result equals the reference, otherwise the first difference. A rejected request is a difference.</summary>
    public static string? Evaluate(DifferentialCase c, ResultSet reference, ChannelResult product)
    {
        if (product.Status is < 200 or >= 300)
        {
            return $"product answered HTTP {product.Status} code '{product.Code}': {RobinAssert.Snippet(product.RawBody)}";
        }

        var columns = c.ProductColumns ?? [.. reference.Columns];
        var rows = new List<object?[]>(product.Rows.Count);
        foreach (var row in product.Rows)
        {
            rows.Add([.. columns.Select(name => Lookup(row, name))]);
        }

        // the comparer matches columns by name, so the product columns are presented under the reference aliases
        return ResultComparer.Compare(reference, new ResultSet(reference.Columns, rows), c.Ordered, c.DateGrouping);
    }

    private static object? Lookup(IReadOnlyDictionary<string, object?> row, string column)
    {
        if (row.TryGetValue(column, out var v))
        {
            return v;
        }

        var hit = row.FirstOrDefault(kv => string.Equals(kv.Key, column, StringComparison.OrdinalIgnoreCase));
        return hit.Key is null ? throw new XunitException($"column '{column}' missing in product row [{string.Join(",", row.Keys)}]") : hit.Value;
    }

    private static void ParityObservationsNote(AllowEntry allowed) => Console.WriteLine($"differential: allowed difference '{allowed.Name}': {allowed.Reason}");
}
