namespace Autheris.Tests.SqlCompilerGate.Model;

using System.Globalization;
using System.Text;
using TrinoSqlEngine;

/// <summary>The engines the differential gate compares (WP-X0s). <see cref="Unfiltered"/> exists only as a negative control.</summary>
public enum EngineKind { Ast, Unfiltered }

/// <summary>The databases of the legacy-vs-AST differential (G4). DuckDB and Databricks have no legacy path (cross-dialect, WP-X0y).</summary>
public enum GateDialect { Sqlite, PostgreSql, SqlServer, Oracle }

public enum ColumnKind { Whole, Text, Fixed, Timestamp }

public sealed record ColumnDef(string Name, ColumnKind Kind, bool Masked = false);

public sealed record TableDef(string Name, IReadOnlyList<ColumnDef> Columns, bool HasConsentFilter = false)
{
    public int IndexOf(string column) => Columns.ToList().FindIndex(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A fixture row, the logical values by column index (null = SQL NULL).</summary>
public sealed record RowDef(string Table, string Tenant, long Id, IReadOnlyList<object?> Values, bool ConsentVisible)
{
    /// <summary>The reference policy: the row a tenant-A principal may see under <paramref name="scenario"/>.</summary>
    public bool VisibleTo(PolicyScenario scenario) =>
        Tenant == GateModel.TenantA && (scenario == PolicyScenario.Tenant || ConsentVisible);
}

/// <summary>
/// The policy scenarios of the gate. <see cref="Tenant"/>: tenant isolation only. <see cref="TenantConsentMask"/>: tenant plus a
/// consent filter on <c>orders.region = 'EU'</c> plus masks on every <c>email</c> column (constant <c>***</c>).
/// </summary>
public enum PolicyScenario { Tenant, TenantConsentMask }

/// <summary>
/// The fixture of the gate: one C# model that generates the DDL and the data of every dialect (plan 9.2). Every table has a tenant
/// column and rows of tenant A and tenant B. Every row carries distinct marker values (<c>orders-A-3</c>, <c>orders-B-1001</c>),
/// ids of rows that are not visible to tenant A under a scenario are large and unique (so a leak is visible in a plain
/// <c>SELECT id</c>), and the masked <c>email</c> values are unique clear values that must never appear in a result.
/// </summary>
public static class GateModel
{
    public const string TenantA = "A";
    public const string TenantB = "B";
    public const string TenantColumn = "tenant_id";
    public const string MaskValue = "***";
    public const string ConsentColumn = "region";
    public const string ConsentValue = "EU";

    public static IReadOnlyList<TableDef> Tables { get; } =
    [
        new("orders",
        [
            new("id", ColumnKind.Whole), new(TenantColumn, ColumnKind.Text), new("customer_id", ColumnKind.Whole),
            new("amount", ColumnKind.Fixed), new("status", ColumnKind.Text), new("region", ColumnKind.Text),
            new("dept", ColumnKind.Text), new("created_at", ColumnKind.Timestamp), new("email", ColumnKind.Text, Masked: true),
            new("note", ColumnKind.Text)
        ], HasConsentFilter: true),
        new("customers",
        [
            new("id", ColumnKind.Whole), new(TenantColumn, ColumnKind.Text), new("name", ColumnKind.Text),
            new("email", ColumnKind.Text, Masked: true), new("region", ColumnKind.Text)
        ]),
        new("entitlements",
        [
            new("id", ColumnKind.Whole), new(TenantColumn, ColumnKind.Text), new("order_id", ColumnKind.Whole),
            new("note", ColumnKind.Text)
        ]),
        new("employees",
        [
            new("id", ColumnKind.Whole), new(TenantColumn, ColumnKind.Text), new("name", ColumnKind.Text),
            new("dept", ColumnKind.Text), new("salary", ColumnKind.Fixed)
        ]),
        new("users",
        [
            new("id", ColumnKind.Whole), new(TenantColumn, ColumnKind.Text), new("name", ColumnKind.Text),
            new("status", ColumnKind.Text), new("email", ColumnKind.Text, Masked: true)
        ])
    ];

    public static TableDef Table(string name) => Tables.First(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] Statuses = ["open", "closed", "pending", "open"];
    private static readonly string[] Departments = ["sales", "ops", "hr", "it"];

    /// <summary>All fixture rows.</summary>
    public static IReadOnlyList<RowDef> Rows { get; } = BuildRows();

    private static List<RowDef> BuildRows()
    {
        var rows = new List<RowDef>();
        foreach (var table in Tables)
        {
            // Tenant A: ids 1..4 (region EU), tenant A rows with ids above a per-table base (region US: hidden by the consent filter
            // of orders), tenant B: 1001..1004. The per-table base keeps every hidden id unique across tables.
            long hidden = HiddenIdBase(table.Name);
            foreach (var (tenant, id, region) in new (string, long, string)[]
                     {
                         (TenantA, 1, "EU"), (TenantA, 2, "EU"), (TenantA, 3, "EU"), (TenantA, 4, "EU"),
                         (TenantA, hidden + 1, "US"), (TenantA, hidden + 2, "US"), (TenantA, hidden + 3, "US"), (TenantA, hidden + 4, "US"),
                         (TenantB, 1001, "EU"), (TenantB, 1002, "US"), (TenantB, 1003, "EU"), (TenantB, 1004, "US")
                     })
            {
                int n = (int)(id % 4);
                var values = new List<object?>();
                foreach (var column in table.Columns)
                {
                    values.Add(column.Name switch
                    {
                        "id" => id,
                        TenantColumn => tenant,
                        "customer_id" => tenant == TenantA ? (id % 2) + 1 : 1001 + (id % 2),
                        "amount" => 10m * ((id % 100) + 1) + 0.5m,
                        "status" => Statuses[n],
                        "region" => region,
                        "dept" => Departments[n],
                        "created_at" => new DateTime(2024, 1 + n, 5 + n, 10, 30, 0, DateTimeKind.Unspecified),
                        "email" => EmailOf(table.Name, tenant, id),
                        "note" => MarkerOf(table.Name, tenant, id),
                        "name" => MarkerOf(table.Name, tenant, id),
                        "order_id" => id,
                        "salary" => 70000m + (10 * (id % 100)),
                        _ => throw new InvalidOperationException($"No fixture value for column {column.Name}.")
                    });
                }

                rows.Add(new RowDef(table.Name, tenant, id, values, !table.HasConsentFilter || region == ConsentValue));
            }
        }

        return rows;
    }

    /// <summary>The base of the ids of the tenant-A rows with region US (2000 for orders, 3000 for customers, and so on).</summary>
    public static long HiddenIdBase(string table) => 2000 + (1000L * Tables.ToList().FindIndex(t => t.Name == table));

    public static string MarkerOf(string table, string tenant, long id) => $"{table}-{tenant}-{id}";

    public static string EmailOf(string table, string tenant, long id) => $"{table}.{tenant}.{id}@clear.example";

    /// <summary>The target dialect of the compiler for a gate database.</summary>
    public static TargetSqlDialect ToTarget(GateDialect dialect) => dialect switch
    {
        GateDialect.Sqlite => TargetSqlDialect.Sqlite,
        GateDialect.PostgreSql => TargetSqlDialect.PostgreSql,
        GateDialect.SqlServer => TargetSqlDialect.SqlServer,
        GateDialect.Oracle => TargetSqlDialect.Oracle,
        _ => throw new ArgumentOutOfRangeException(nameof(dialect))
    };

    // ---- per-dialect DDL and data, generated from the one model ----

    /// <summary>The database name of an identifier: Oracle folds unquoted names to upper case, so the catalog spelling is upper case.</summary>
    public static string Canon(GateDialect dialect, string name) => dialect == GateDialect.Oracle ? name.ToUpperInvariant() : name;

    public static string TypeOf(GateDialect dialect, ColumnDef column) => (dialect, column.Kind) switch
    {
        (GateDialect.Sqlite, ColumnKind.Whole) => "INTEGER",
        (GateDialect.Sqlite, ColumnKind.Text) => "TEXT",
        (GateDialect.Sqlite, ColumnKind.Fixed) => "NUMERIC",
        (GateDialect.Sqlite, ColumnKind.Timestamp) => "TEXT",
        (GateDialect.PostgreSql, ColumnKind.Whole) => "integer",
        (GateDialect.PostgreSql, ColumnKind.Text) => "text",
        (GateDialect.PostgreSql, ColumnKind.Fixed) => "numeric(18,2)",
        (GateDialect.PostgreSql, ColumnKind.Timestamp) => "timestamp",
        (GateDialect.SqlServer, ColumnKind.Whole) => "int",
        (GateDialect.SqlServer, ColumnKind.Text) => "varchar(200)",
        (GateDialect.SqlServer, ColumnKind.Fixed) => "decimal(18,2)",
        (GateDialect.SqlServer, ColumnKind.Timestamp) => "datetime2",
        (GateDialect.Oracle, ColumnKind.Whole) => "NUMBER(10)",
        (GateDialect.Oracle, ColumnKind.Text) => "VARCHAR2(200)",
        (GateDialect.Oracle, ColumnKind.Fixed) => "NUMBER(18,2)",
        (GateDialect.Oracle, ColumnKind.Timestamp) => "TIMESTAMP",
        _ => throw new ArgumentOutOfRangeException(nameof(column))
    };

    /// <summary>CREATE TABLE statements (unquoted names, so unqualified legacy SQL resolves on every dialect).</summary>
    public static IEnumerable<string> CreateStatements(GateDialect dialect, string? schemaPrefix = null)
    {
        foreach (var table in Tables)
        {
            var sb = new StringBuilder();
            sb.Append("CREATE TABLE ").Append(schemaPrefix).Append(table.Name).Append(" (");
            for (int i = 0; i < table.Columns.Count; i++)
            {
                var c = table.Columns[i];
                if (i > 0) sb.Append(", ");
                sb.Append(c.Name).Append(' ').Append(TypeOf(dialect, c));
                if (c.Name == "id") sb.Append(" PRIMARY KEY");
            }

            yield return sb.Append(')').ToString();
        }
    }

    /// <summary>The INSERT statements of the whole fixture (inline literals: fixture data, not governed SQL).</summary>
    public static IEnumerable<string> InsertStatements(GateDialect dialect, string? schemaPrefix = null, Func<string, bool>? tableFilter = null)
    {
        foreach (var row in Rows)
        {
            if (tableFilter is not null && !tableFilter(row.Table)) continue;
            var table = Table(row.Table);
            var sb = new StringBuilder();
            sb.Append("INSERT INTO ").Append(schemaPrefix).Append(table.Name).Append(" (")
                .Append(string.Join(", ", table.Columns.Select(c => c.Name))).Append(") VALUES (");
            for (int i = 0; i < table.Columns.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Literal(dialect, row.Values[i]));
            }

            yield return sb.Append(')').ToString();
        }
    }

    private static string Literal(GateDialect dialect, object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        long l => l.ToString(CultureInfo.InvariantCulture),
        int i => i.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateTime t => dialect == GateDialect.Oracle
            ? "TIMESTAMP '" + t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'"
            : "'" + t.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + "'",
        _ => throw new InvalidOperationException("Unsupported fixture value type.")
    };
}
