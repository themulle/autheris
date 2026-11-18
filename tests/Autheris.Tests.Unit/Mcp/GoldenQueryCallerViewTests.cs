namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>The shared caller view: transitive relation walk, hidden identifiers of every reached dataset and fail-closed resolution.</summary>
public sealed class GoldenQueryCallerViewTests
{
    private static readonly TableIdentifier Orders = new("warehouse", "dbo", "orders");
    private static readonly TableIdentifier Customers = new("warehouse", "dbo", "customers");
    private static readonly TableIdentifier Accounts = new("warehouse", "dbo", "accounts");
    private static readonly TenantId Tenant = new("tenant-a");

    [Fact]
    [Trait("Category", "Security")]
    public async Task Two_hop_relation_to_hidden_dataset_drops_example()
    {
        const string query = "{ orders { id customer { name account { number } } } }";

        (await Serve(query, visible: [Orders, Customers])).ShouldBeEmpty();
        (await Serve(query, visible: [Orders, Customers, Accounts])).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hidden_column_of_a_reached_dataset_drops_example()
    {
        var hidden = new Dictionary<TableIdentifier, string[]> { [Customers] = ["secret"] };

        (await Serve("{ orders { customer { secret } } }", hidden: hidden)).ShouldBeEmpty();
        (await Serve("{ orders { customer { sec: secret } } }", hidden: hidden)).ShouldBeEmpty();
        (await Serve("{ orders { customer { name } } }", hidden: hidden)).Count.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hidden_column_of_the_owner_drops_example()
    {
        var hidden = new Dictionary<TableIdentifier, string[]> { [Orders] = ["amount"] };

        (await Serve("{ orders { id amount } }", hidden: hidden)).ShouldBeEmpty();
        (await Serve("{ orders { id } }", hidden: hidden)).Count.ShouldBe(1);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("{ orders { id nonsense } }")]
    [InlineData("{ orders { ... on Order { id } } }")]
    [InlineData("{ orders { ...F } } fragment F on Order { id }")]
    [InlineData("{ other { id } }")]
    [InlineData("mutation { orders { id } }")]
    [InlineData("{ orders { id ")]
    public async Task Unresolvable_entry_is_dropped(string query) => (await Serve(query)).ShouldBeEmpty();

    [Fact]
    [Trait("Category", "Security")]
    public async Task Callers_with_different_views_get_different_results_from_the_same_entry()
    {
        var service = NewService("{ orders { customer { secret } } }");
        var open = await BuildView([Orders, Customers], null);
        var restricted = await BuildView([Orders, Customers], new Dictionary<TableIdentifier, string[]> { [Customers] = ["secret"] });

        (await service.GetGoldenQueriesAsync(open.ToContext(Tenant), null, null, CancellationToken.None)).Count.ShouldBe(1);
        (await service.GetGoldenQueriesAsync(restricted.ToContext(Tenant), null, null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("{ orders { id } }", 1)]
    [InlineData("{ orders { id customer { name } } }", 0)]
    [InlineData("{ orders { ... on Order { id } } }", 0)]
    public async Task Without_graphql_mapping_only_a_flat_selection_is_served(string query, int expected)
    {
        var service = NewService(query);
        var view = await GoldenQueryCallerView.BuildAsync(
            [new GoldenQueryCallerView.Seed(Orders.Domain, Orders.TableName, Orders)],
            null,
            (t, _) => Task.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>()),
            (_, _, _, _, _) => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>()),
            CancellationToken.None);

        (await service.GetGoldenQueriesAsync(view.ToContext(Tenant), null, null, CancellationToken.None)).Count.ShouldBe(expected);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Datasets_beyond_the_closure_cap_serve_no_stored_entries()
    {
        var seeds = Enumerable.Range(0, GoldenQueryCallerView.MaxDatasets + 1)
            .Select(i => new GoldenQueryCallerView.Seed("warehouse", "t" + i, new TableIdentifier("warehouse", "dbo", "t" + i)))
            .ToList();
        var view = await GoldenQueryCallerView.BuildAsync(
            seeds,
            null,
            (t, _) => Task.FromResult<IReadOnlySet<TableIdentifier>>(new HashSet<TableIdentifier>()),
            (_, _, _, _, _) => Task.FromResult<IReadOnlySet<string>?>(new HashSet<string>()),
            CancellationToken.None);
        var demo = Substitute.For<IDemoDataSwitch>();
        demo.Enabled.Returns(false);
        var repository = Substitute.For<IGoldenQueryRepository>();
        repository.GetForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<GoldenQueryStoreRecord>>(
        [
            new GoldenQueryStoreRecord("a", "tbl-0", "warehouse", "dbo", "t0", GoldenQuerySource.Dbt, "k0", "In", null, "{ t0 { id } }", null, new string('a', 64), DateTimeOffset.UtcNow),
            new GoldenQueryStoreRecord("b", "tbl-1", "warehouse", "dbo", "t" + GoldenQueryCallerView.MaxDatasets, GoldenQuerySource.Dbt, "k1", "Out", null, "{ t" + GoldenQueryCallerView.MaxDatasets + " { id } }", null, new string('a', 64), DateTimeOffset.UtcNow)
        ]));
        var service = new GoldenQueryService(Options.Create(new GatewayOptions()), NullLogger<GoldenQueryService>.Instance, demo, repository);

        var served = await service.GetGoldenQueriesAsync(view.ToContext(Tenant), null, null, CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["k0"]);
    }

    private static async Task<IReadOnlyList<GoldenQuery>> Serve(
        string query,
        TableIdentifier[]? visible = null,
        Dictionary<TableIdentifier, string[]>? hidden = null)
    {
        var view = await BuildView(visible ?? [Orders, Customers, Accounts], hidden);
        return await NewService(query).GetGoldenQueriesAsync(view.ToContext(Tenant), null, null, CancellationToken.None);
    }

    private static Task<GoldenQueryCallerView> BuildView(TableIdentifier[] visible, Dictionary<TableIdentifier, string[]>? hidden)
    {
        var map = Substitute.For<IGraphQlCatalogMap>();
        map.GetTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(call => Task.FromResult(MappingOf(call.Arg<TableIdentifier>())));
        var visibleSet = visible.ToHashSet();
        return GoldenQueryCallerView.BuildAsync(
            [new GoldenQueryCallerView.Seed(Orders.Domain, Orders.TableName, Orders)],
            map,
            (targets, _) => Task.FromResult<IReadOnlySet<TableIdentifier>>(targets.Where(visibleSet.Contains).ToHashSet()),
            (id, _, _, _, _) => Task.FromResult<IReadOnlySet<string>?>(
                id is { } ident && hidden is not null && hidden.TryGetValue(ident, out var cols)
                    ? cols.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase)),
            CancellationToken.None);
    }

    private static GoldenQueryService NewService(string query)
    {
        var demo = Substitute.For<IDemoDataSwitch>();
        demo.Enabled.Returns(false);
        var options = Options.Create(new GatewayOptions
        {
            GoldenQueries = new GoldenQueryOptions
            {
                InitialQueries = []
            }
        });
        var repository = Substitute.For<IGoldenQueryRepository>();
        repository.GetForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<GoldenQueryStoreRecord>>(
            [new GoldenQueryStoreRecord("id-1", "tbl-orders", "warehouse", "dbo", "orders", GoldenQuerySource.Dbt, "q-1", "Example", null, query, null, new string('a', 64), DateTimeOffset.UtcNow)]));
        return new GoldenQueryService(options, NullLogger<GoldenQueryService>.Instance, demo, repository);
    }

    private static GraphQlTableMapping? MappingOf(TableIdentifier id)
    {
        if (id == Orders)
        {
            return Mapping(Orders, "warehouse_dbo_orders", ["id", "amount"], [new GraphQlRelationMapping("customer", Customers, false)]);
        }

        if (id == Customers)
        {
            return Mapping(Customers, "warehouse_dbo_customers", ["name", "secret"], [new GraphQlRelationMapping("account", Accounts, false)]);
        }

        return id == Accounts ? Mapping(Accounts, "warehouse_dbo_accounts", ["number"], []) : null;
    }

    private static GraphQlTableMapping Mapping(TableIdentifier id, string root, string[] columns, IReadOnlyList<GraphQlRelationMapping> relations) =>
        new(id, root, root, root + "_filter", root + "_order", columns.Select(c => new GraphQlColumnMapping(c, c, "String")).ToList(), relations);
}
