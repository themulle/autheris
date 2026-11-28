using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Sql;

public sealed class SqlDatasetFactsTests
{
    private const string FilterText = "Row filter applies (defined by data owner).";
    private static readonly TableIdentifier Orders = new("sales", "public", "orders");
    private static readonly TableIdentifier Customers = new("sales", "public", "customers");
    private static readonly TableIdentifier Secret = new("hr", "public", "salaries");
    private static readonly CatalogCaller Caller = new(new ClaimsPrincipal(new ClaimsIdentity()), new TenantId("t1"), CatalogChannel.Rest);

    private sealed class VisibilityStub(params TableMetadata[] visible) : ICatalogVisibilityService
    {
        public bool Throw { get; init; }

        public ValueTask<CatalogVisibleSet> GetVisibleAsync(CatalogCaller caller, Autheris.Application.Governance.Contracts.SchemaContractDefinition? contract = null, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask<CatalogVisibleRow?> FindVisibleAsync(CatalogCaller caller, TableIdentifier table, CancellationToken ct = default)
        {
            if (Throw)
            {
                throw new InvalidOperationException("boom");
            }

            var hit = visible.FirstOrDefault(t => t.Identifier.Equals(table));
            return ValueTask.FromResult<CatalogVisibleRow?>(hit is null ? null : new CatalogVisibleRow(hit, CatalogOutputShape.Full));
        }

        public ValueTask<IReadOnlySet<TableIdentifier>> FilterVisibleAsync(CatalogCaller caller, IReadOnlyCollection<TableIdentifier> candidates, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private static TableMetadata Meta(TableIdentifier id, params string[] tags) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, SourceType = "PostgreSql" },
        CatalogTags = tags,
    };

    private static DatasetAccessFacts Fact(TableIdentifier id, params string[] filters) => new(id, [], filters);

    [Fact]
    public async Task JoinOfTaggedAndUntaggedTable_ReportsDatasetTagsAndRowFilterPerTable()
    {
        var visibility = new VisibilityStub(Meta(Orders, "certified"), Meta(Customers));

        var tables = await SqlDatasetFacts.ResolveAsync(visibility, Caller, [Fact(Orders, FilterText), Fact(Customers)], default);

        tables.Count.ShouldBe(2);
        tables[0].Tags.ShouldBe(["certified"]);
        tables[0].RowFilterApplied.ShouldBeTrue();
        tables[0].RowFilters.ShouldBe([FilterText]);
        tables[1].Dataset.ShouldBe("sales.public.customers");
        tables[1].Tags.ShouldBeEmpty();
        tables[1].RowFilterApplied.ShouldBeFalse();
        tables[1].RowFilters.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task InvisibleTable_HasNoEntryAndIsNotNamed()
    {
        var visibility = new VisibilityStub(Meta(Orders));

        var tables = await SqlDatasetFacts.ResolveAsync(visibility, Caller, [Fact(Orders), Fact(Secret, FilterText)], default);

        tables.Select(t => t.Dataset).ShouldBe(["sales.public.orders"]);
        JsonSerializer.Serialize(SqlDatasetFacts.ToResponse(tables, false)).ShouldNotContain("salaries");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VisibilityFailureOrMissingService_FailsClosedWithNoFacts()
    {
        (await SqlDatasetFacts.ResolveAsync(new VisibilityStub(Meta(Orders)) { Throw = true }, Caller, [Fact(Orders)], default)).ShouldBeEmpty();
        (await SqlDatasetFacts.ResolveAsync(null, Caller, [Fact(Orders)], default)).ShouldBeEmpty();
        (await SqlDatasetFacts.ResolveAsync(new VisibilityStub(Meta(Orders)), Caller, null, default)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SameTableTwice_IsReportedOnceAndTagsAreBounded()
    {
        var manyTags = Enumerable.Range(0, 200).Select(i => new string('x', 300) + i).ToArray();
        var visibility = new VisibilityStub(Meta(Orders, manyTags));

        var tables = await SqlDatasetFacts.ResolveAsync(visibility, Caller, [Fact(Orders), Fact(Orders)], default);

        tables.Count.ShouldBe(1);
        tables[0].Tags.Count.ShouldBe(64);
        tables[0].Tags.ShouldAllBe(t => t.Length <= 128);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Response_CarriesOnlyFixedFields_NoSqlOrValues()
    {
        var visibility = new VisibilityStub(Meta(Orders, "certified"));
        var tables = await SqlDatasetFacts.ResolveAsync(visibility, Caller, [Fact(Orders, FilterText)], default);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(SqlDatasetFacts.ToResponse(tables, true)));

        doc.RootElement.EnumerateObject().Select(p => p.Name).ShouldBe(["truncated", "tables"], ignoreOrder: true);
        doc.RootElement.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        var entry = doc.RootElement.GetProperty("tables")[0];
        entry.EnumerateObject().Select(p => p.Name).ShouldBe(["dataset", "tags", "rowFilterApplied", "rowFilters"], ignoreOrder: true);
        entry.GetProperty("rowFilters")[0].GetProperty("description").GetString().ShouldBe(FilterText);
    }
}
