using System.Collections.Frozen;
using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace TrinoSqlEngine.Tests.Compiler;

internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 10, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

internal sealed class DictPolicyProvider : IPolicyPredicateProvider
{
    public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
    public HashSet<TableIdentity> NoPolicy { get; } = new();
    public bool ShouldApplyPolicy(TableIdentity table) => !NoPolicy.Contains(table);
    public PolicyPredicate GetPredicate(TableIdentity table) =>
        Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
}

internal sealed class DictMaskProvider : IColumnMaskProvider
{
    public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
    public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
    public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
}

internal static class PolicyFixtures
{
    public static readonly TableIdentity Orders = new("dbo", "Orders");
    public static readonly TableIdentity Entitlements = new("dbo", "Entitlements");
    public static readonly TableIdentity Lookup = new("dbo", "Lookup");

    public static InMemoryTableCatalog Catalog() => new(new[]
    {
        new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("Region", "nvarchar(20)"),
            new CatalogColumn("Status", "nvarchar(20)"), new CatalogColumn("Amount", "decimal(18,2)"), new CatalogColumn("Email", "nvarchar(200)")),
            "TenantId", 1),
        new TableCatalogEntry(Entitlements, ImmutableArray.Create(
            new CatalogColumn("Id", "int"), new CatalogColumn("TenantId", "nvarchar(64)"), new CatalogColumn("OrderId", "int")),
            "TenantId", 1),
        new TableCatalogEntry(Lookup, ImmutableArray.Create(new CatalogColumn("Code", "nvarchar(10)")), null, 1)
    }, "dbo");

    public static PolicyContextBuilder Context(TableIdentity table) => new(table);
}

internal sealed class PolicyContextBuilder(TableIdentity table)
{
    public string[] Columns { get; set; } = { "Id", "TenantId", "Region", "Status", "Amount", "Email" };
    public HashSet<string> Functions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, PolicyValue> Parameters { get; } = new();
    public long Version { get; set; } = 1;
    public ITableCatalog? Catalog { get; set; }
    public string? Partition { get; set; }

    public PolicyParseContext Build() => new(table, Columns, Functions, Parameters, Version) { Catalog = Catalog, CachePartition = Partition };
}
