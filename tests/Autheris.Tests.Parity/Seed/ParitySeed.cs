using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Autheris.Tests.Parity.Seed;

public sealed record RegionRow(int Id, string Name, string Tz);

public sealed record SaleRow(int Id, int? RegionId, string? Region, string Product, int? Qty, decimal? Amount, string SaleDate, string SoldAt, bool? IsReturn, string? Note);

/// <summary>Synthetic seed dataset (D-11). Row literals are the single source of truth; <see cref="ParityExpected"/> holds the hand-computed answers.</summary>
public static class ParitySeed
{
    public const string SalesTable = "parity_sales";
    public const string RegionsTable = "parity_regions";

    /// <summary>Copy of the sales rows whose catalog entry requires a filter on <c>region</c> (#163, #236).</summary>
    public const string ScopedTable = "parity_scoped";

    /// <summary>Copy of the sales rows whose <c>amount</c> column carries a masking rule (#28).</summary>
    public const string MaskedTable = "parity_masked";

    /// <summary>1000 rows (<c>id</c> 1..1000, <c>grp</c> <c>g0</c>..<c>g9</c>) for paging and truncation scenarios (#39).</summary>
    public const string BigTable = "parity_big";

    public const int BigRowCount = 1000;

    public static readonly IReadOnlyList<RegionRow> Regions =
    [
        new(1, "North", "Europe/Berlin"),
        new(2, "South", "UTC"),
        new(3, "Empty", "UTC"),
    ];

    public static readonly IReadOnlyList<SaleRow> Sales =
    [
        new(1, 1, "North", "Widget", 10, 100.00m, "2023-03-15", "2023-03-15T10:00:00Z", false, "plain"),
        new(2, 1, "North", "Widget", 5, 50.50m, "2023-06-01", "2023-06-01T08:30:00Z", false, "50% off"),
        new(3, 1, "North", "Gadget", 2, 200.00m, "2024-01-10", "2024-01-10T12:00:00Z", false, "under_score"),
        new(4, 1, "North", "Gadget", 2, 200.00m, "2024-01-10", "2024-01-10T12:00:00Z", false, "it's"),
        new(5, 1, "North", "Widget", 3, 30.00m, "2024-02-29", "2024-02-29T23:59:59Z", true, "back\\slash"),
        new(6, 1, "North", "Gizmo", 1, 15.25m, "2024-03-31", "2024-03-31T00:30:00Z", false, "SELECT * FROM x"),
        new(7, 2, "South", "Widget", 7, 70.00m, "2024-05-05", "2024-05-05T09:00:00Z", false, null),
        new(8, 2, "South", "Gadget", 4, 400.00m, "2024-07-20", "2024-07-20T15:45:00Z", false, "emoji \U0001F600"),
        new(9, 2, "South", "Gadget", 4, 400.00m, "2024-07-20", "2024-07-20T15:45:00Z", true, null),
        new(10, 2, "South", "Gizmo", 6, 90.00m, "2024-12-31", "2024-12-31T23:30:00Z", false, null),
        new(11, 2, "South", "Widget", 8, 80.00m, "2025-01-15", "2025-01-15T07:00:00Z", false, null),
        new(12, 2, "South", "Gizmo", 9, 135.75m, "2025-02-28", "2025-02-28T11:11:11Z", null, null),
        new(13, 3, "Empty", "Widget", null, null, "2025-03-01", "2025-03-01T00:00:00Z", null, null),
        new(14, 3, "Empty", "Gadget", null, null, "2025-03-02", "2025-03-02T00:00:00Z", null, null),
        new(15, 3, "Empty", "Gizmo", null, null, "2025-03-03", "2025-03-03T00:00:00Z", null, null),
        new(16, null, null, "Widget", 1, 10.00m, "2025-04-01", "2025-04-01T06:00:00Z", false, null),
        new(17, null, null, "Gadget", 3, 60.00m, "2025-04-02", "2025-04-02T06:00:00Z", true, null),
        new(18, 1, "North", "Widget", 4, 40.00m, "2025-05-05", "2025-05-05T05:05:05Z", false, null),
    ];

    /// <summary>Rows of the second source <c>second</c> (same schema, 4 rows; federation scenarios).</summary>
    public static IEnumerable<SaleRow> SecondSales => Sales.Take(4);

    private static string SalesDdl(string table) =>
        $"CREATE TABLE {table} (id INTEGER PRIMARY KEY, region_id INTEGER NULL, region TEXT NULL, product TEXT NOT NULL, qty INTEGER NULL, " +
        "amount NUMERIC(12,2) NULL, sale_date DATE NOT NULL, sold_at TIMESTAMP NOT NULL, is_return BOOLEAN NULL, note TEXT NULL);";

    public static void Create(string path) => Create(path, Sales);

    public static void Create(string path, IEnumerable<SaleRow> sales)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE parity_regions (id INTEGER PRIMARY KEY, name TEXT NOT NULL, tz TEXT NOT NULL);" +
                SalesDdl(SalesTable) + SalesDdl(ScopedTable) + SalesDdl(MaskedTable);
            create.ExecuteNonQuery();
        }

        using (var extra = conn.CreateCommand())
        {
            extra.CommandText =
                "CREATE TABLE parity_big (id INTEGER PRIMARY KEY, grp TEXT NOT NULL);";
            extra.ExecuteNonQuery();
        }

        using var tx = conn.BeginTransaction();
        foreach (var r in Regions)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO parity_regions (id, name, tz) VALUES ($id, $name, $tz)";
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$name", r.Name);
            cmd.Parameters.AddWithValue("$tz", r.Tz);
            cmd.ExecuteNonQuery();
        }

        foreach (var s in sales)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "INSERT INTO parity_sales (id, region_id, region, product, qty, amount, sale_date, sold_at, is_return, note) " +
                              "VALUES ($id, $region_id, $region, $product, $qty, $amount, $sale_date, $sold_at, $is_return, $note)";
            cmd.Parameters.AddWithValue("$id", s.Id);
            cmd.Parameters.AddWithValue("$region_id", (object?)s.RegionId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$region", (object?)s.Region ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$product", s.Product);
            cmd.Parameters.AddWithValue("$qty", (object?)s.Qty ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$amount", s.Amount is { } a ? Convert.ToDouble(a, CultureInfo.InvariantCulture) : DBNull.Value);
            cmd.Parameters.AddWithValue("$sale_date", s.SaleDate);
            cmd.Parameters.AddWithValue("$sold_at", s.SoldAt);
            cmd.Parameters.AddWithValue("$is_return", s.IsReturn is { } b ? (b ? 1 : 0) : DBNull.Value);
            cmd.Parameters.AddWithValue("$note", (object?)s.Note ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        using (var copy = conn.CreateCommand())
        {
            copy.Transaction = tx;
            copy.CommandText =
                "INSERT INTO parity_scoped SELECT * FROM parity_sales;" +
                "INSERT INTO parity_masked SELECT * FROM parity_sales;" +
                $"WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < {BigRowCount}) " +
                "INSERT INTO parity_big (id, grp) SELECT i, 'g' || (i % 10) FROM n;";
            copy.ExecuteNonQuery();
        }

        tx.Commit();
    }
}

/// <summary>Hand-computed expected values (literals, not computed by SQL). Verified against the row literals by a LINQ test.</summary>
public static class ParityExpected
{
    public const int TotalRows = 18;
    public const int NonNullAmountRows = 15;
    public const decimal SumAmount = 1881.50m;
    public const decimal MinAmount = 10.00m;
    public const decimal MaxAmount = 400.00m;
    public const decimal AvgAmount = 125.43333333333333m;
    public const int SumQty = 69;
    public const int DistinctProducts = 3;

    public static readonly IReadOnlyDictionary<string, int> RowsByRegion = new Dictionary<string, int> { ["North"] = 7, ["South"] = 6, ["Empty"] = 3 };

    /// <summary>SUM(amount) per region; region Empty has only NULL amounts, so its sum is NULL (absent here).</summary>
    public static readonly IReadOnlyDictionary<string, decimal> SumAmountByRegion = new Dictionary<string, decimal> { ["North"] = 635.75m, ["South"] = 1175.75m };

    public const int RowsWithNullRegion = 2;
    public const decimal SumAmountNullRegion = 70.00m;

    public static readonly IReadOnlyDictionary<string, int> RowsByProduct = new Dictionary<string, int> { ["Widget"] = 8, ["Gadget"] = 6, ["Gizmo"] = 4 };

    public static readonly IReadOnlyDictionary<int, int> RowsByYear = new Dictionary<int, int> { [2023] = 2, [2024] = 8, [2025] = 8 };

    public static readonly IReadOnlyDictionary<int, decimal> SumAmountByYear = new Dictionary<int, decimal> { [2023] = 150.50m, [2024] = 1405.25m, [2025] = 325.75m };

    public const int DistinctYearMonths = 13;
    public const int DistinctSaleDates = 16;
    public const int DistinctRegionsNonNull = 3;

    /// <summary>AVG(qty): 69 over the 15 non-null quantities, a fractional result of an integer column.</summary>
    public const decimal AvgQty = 4.6m;

    public static readonly IReadOnlyDictionary<string, decimal> SumAmountByProduct = new Dictionary<string, decimal> { ["Widget"] = 380.50m, ["Gadget"] = 1260.00m, ["Gizmo"] = 241.00m };
}
