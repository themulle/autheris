namespace Autheris.Tests.Unit.Mcp;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
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

/// <summary>Store read path of <see cref="GoldenQueryService"/>: merge with configured entries, per-caller filtering before the cap and cache scoping.</summary>
public sealed class GoldenQueryStoreReadTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    [Fact]
    public async Task Describe_dataset_examples_after_sync_without_restart()
    {
        var fixture = new Fixture();
        var context = new GoldenQueryReadContext(TenantA);

        (await fixture.Service.GetGoldenQueriesAsync(context, "warehouse", "orders", CancellationToken.None)).ShouldBeEmpty();

        // A dbt sync stores an entry and bumps the catalog version; the running service serves it without a restart.
        fixture.Rows = [Row("orders", "dbt-1", "orders { id }")];
        fixture.Version = new CatalogVersion(1, 2);
        var served = await fixture.Service.GetGoldenQueriesAsync(context, "warehouse", "orders", CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["dbt-1"]);

        // The entry is removed again in dbt: the next version drops it.
        fixture.Rows = [];
        fixture.Version = new CatalogVersion(1, 3);
        (await fixture.Service.GetGoldenQueriesAsync(context, "warehouse", "orders", CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Store_is_read_once_per_tenant_and_catalog_version()
    {
        var fixture = new Fixture { Rows = [Row("orders", "dbt-1", "orders { id }")] };
        var context = new GoldenQueryReadContext(TenantA);

        await fixture.Service.GetGoldenQueriesAsync(context, null, null, CancellationToken.None);
        await fixture.Service.GetGoldenQueriesAsync(context, "warehouse", "orders", CancellationToken.None);

        await fixture.Repository.Received(1).GetForTenantAsync(TenantA, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Config_entries_still_served()
    {
        var fixture = new Fixture(configured: [new GoldenQueryDefinition("cfg-1", "warehouse", "orders", "Configured", "d", "orders { id }", null, ["x"])])
        {
            Rows = [Row("orders", "dbt-1", "orders { amount }")]
        };

        var served = await fixture.Service.GetGoldenQueriesAsync(new GoldenQueryReadContext(TenantA), "warehouse", "orders", CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["cfg-1", "dbt-1"]);
    }

    [Fact]
    public async Task Config_wins_on_equal_id_and_store_wins_on_equal_config_source_key()
    {
        var fixture = new Fixture(configured:
        [
            new GoldenQueryDefinition("same", "warehouse", "orders", "Configured", "d", "orders { id }", null, []),
            new GoldenQueryDefinition("copy", "warehouse", "orders", "Configured copy", "d", "orders { id }", null, [])
        ])
        {
            Rows =
            [
                Row("orders", "same", "orders { amount }"),
                Row("orders", "copy", "orders { amount }", GoldenQuerySource.Config)
            ]
        };

        var served = (await fixture.Service.GetGoldenQueriesAsync(new GoldenQueryReadContext(TenantA), null, null, CancellationToken.None))
            .ToDictionary(q => q.Id);

        served["same"].Title.ShouldBe("Configured");
        served["copy"].Title.ShouldBe("Stored copy");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Entry_with_invisible_relation_hidden()
    {
        var fixture = new Fixture
        {
            Rows =
            [
                Row("orders", "dbt-rel", "orders { id customer { name } }"),
                Row("orders", "dbt-own", "orders { id }")
            ]
        };
        var context = new GoldenQueryReadContext(
            TenantA,
            IsDatasetVisible: (_, table) => table != "customers",
            ReferencedDatasets: q => q.QueryText.Contains("customer", StringComparison.Ordinal) ? [("warehouse", "customers")] : []);

        var served = await fixture.Service.GetGoldenQueriesAsync(context, "warehouse", "orders", CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["dbt-own"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Entry_for_invisible_owning_dataset_hidden()
    {
        var fixture = new Fixture { Rows = [Row("payroll", "dbt-1", "payroll { id }"), Row("orders", "dbt-2", "orders { id }")] };
        var context = new GoldenQueryReadContext(TenantA, IsDatasetVisible: (_, table) => table == "orders");

        var served = await fixture.Service.GetGoldenQueriesAsync(context, null, null, CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["dbt-2"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Entry_selecting_denied_column_hidden()
    {
        var fixture = new Fixture
        {
            Rows =
            [
                Row("orders", "dbt-1", "orders { id salary }"),
                Row("orders", "dbt-2", "orders(filter: { salary: 1 }) { id }"),
                Row("orders", "dbt-3", "orders { id amount }")
            ]
        };
        var context = new GoldenQueryReadContext(
            TenantA,
            HiddenIdentifiers: (_, _) => new HashSet<string>(["salary"], StringComparer.OrdinalIgnoreCase));

        var served = await fixture.Service.GetGoldenQueriesAsync(context, "warehouse", "orders", CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["dbt-3"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Visibility_is_applied_before_the_result_cap()
    {
        var rows = Enumerable.Range(0, 5).Select(i => Row("orders", $"a-hidden-{i}", "orders { secret }")).Append(Row("orders", "z-visible", "orders { id }")).ToList();
        var fixture = new Fixture(maxResults: 2) { Rows = rows };
        var context = new GoldenQueryReadContext(
            TenantA,
            HiddenIdentifiers: (_, _) => new HashSet<string>(["secret"], StringComparer.OrdinalIgnoreCase));

        var served = await fixture.Service.GetGoldenQueriesAsync(context, "warehouse", "orders", CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["z-visible"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Throwing_caller_delegate_drops_the_entry()
    {
        var fixture = new Fixture { Rows = [Row("orders", "dbt-1", "orders { id }")] };
        var context = new GoldenQueryReadContext(TenantA, HiddenIdentifiers: (_, _) => throw new InvalidOperationException("boom"));

        (await fixture.Service.GetGoldenQueriesAsync(context, null, null, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Cached_entry_set_is_filtered_per_caller()
    {
        var fixture = new Fixture { Rows = [Row("orders", "dbt-1", "orders { id salary }"), Row("orders", "dbt-2", "orders { id }")] };
        var restricted = new GoldenQueryReadContext(TenantA, HiddenIdentifiers: (_, _) => new HashSet<string>(["salary"], StringComparer.OrdinalIgnoreCase));
        var open = new GoldenQueryReadContext(TenantA);

        var first = await fixture.Service.GetGoldenQueriesAsync(restricted, null, null, CancellationToken.None);
        var second = await fixture.Service.GetGoldenQueriesAsync(open, null, null, CancellationToken.None);
        var third = await fixture.Service.GetGoldenQueriesAsync(restricted, null, null, CancellationToken.None);

        first.Select(q => q.Id).ShouldBe(["dbt-2"]);
        second.Select(q => q.Id).ShouldBe(["dbt-1", "dbt-2"]);
        third.Select(q => q.Id).ShouldBe(["dbt-2"]);
        await fixture.Repository.Received(1).GetForTenantAsync(TenantA, Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Entries_never_cross_tenants()
    {
        var fixture = new Fixture();
        fixture.RowsByTenant[TenantA.Value] = [Row("orders", "dbt-a", "orders { id }")];
        fixture.RowsByTenant[TenantB.Value] = [Row("orders", "dbt-b", "orders { id }")];

        var a = await fixture.Service.GetGoldenQueriesAsync(new GoldenQueryReadContext(TenantA), null, null, CancellationToken.None);
        var b = await fixture.Service.GetGoldenQueriesAsync(new GoldenQueryReadContext(TenantB), null, null, CancellationToken.None);

        a.Select(q => q.Id).ShouldBe(["dbt-a"]);
        b.Select(q => q.Id).ShouldBe(["dbt-b"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Default_tenant_reads_no_stored_entries()
    {
        var fixture = new Fixture { Rows = [Row("orders", "dbt-1", "orders { id }")] };

        (await fixture.Service.GetGoldenQueriesAsync(new GoldenQueryReadContext(default), null, null, CancellationToken.None)).ShouldBeEmpty();

        await fixture.Repository.DidNotReceiveWithAnyArgs().GetForTenantAsync(default, CancellationToken.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Store_failure_serves_configured_entries_only()
    {
        var fixture = new Fixture(configured: [new GoldenQueryDefinition("cfg-1", "warehouse", "orders", "Configured", "d", "orders { id }", null, [])]);
        fixture.Repository.GetForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns<Task<IReadOnlyList<GoldenQueryStoreRecord>>>(_ => throw new InvalidOperationException("store down"));

        var served = await fixture.Service.GetGoldenQueriesAsync(new GoldenQueryReadContext(TenantA), null, null, CancellationToken.None);

        served.Select(q => q.Id).ShouldBe(["cfg-1"]);
    }

    [Fact]
    public async Task Disabled_service_serves_nothing()
    {
        var fixture = new Fixture(enabled: false) { Rows = [Row("orders", "dbt-1", "orders { id }")] };

        (await fixture.Service.GetGoldenQueriesAsync(new GoldenQueryReadContext(TenantA), null, null, CancellationToken.None)).ShouldBeEmpty();
    }

    private static GoldenQueryStoreRecord Row(string table, string key, string query, GoldenQuerySource source = GoldenQuerySource.Dbt) =>
        new(Guid.NewGuid().ToString(), "tbl-" + table, "warehouse", "dbo", table, source, key,
            source == GoldenQuerySource.Config ? "Stored copy" : "Stored " + key, null, query, null, new string('a', 64), DateTimeOffset.UtcNow);

    private sealed class Fixture
    {
        public Fixture(IReadOnlyList<GoldenQueryDefinition>? configured = null, int maxResults = 20, bool enabled = true)
        {
            Repository = Substitute.For<IGoldenQueryRepository>();
            Repository.GetForTenantAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult<IReadOnlyList<GoldenQueryStoreRecord>>(
                    RowsByTenant.TryGetValue(call.Arg<TenantId>().Value, out var perTenant) ? perTenant : Rows));
            var versions = Substitute.For<ICatalogVersionProvider>();
            versions.GetCatalogVersionAsync(Arg.Any<CancellationToken>()).Returns(_ => ValueTask.FromResult(Version));
            var demo = Substitute.For<IDemoDataSwitch>();
            demo.Enabled.Returns(false);
            var options = Options.Create(new GatewayOptions
            {
                GoldenQueries = new GoldenQueryOptions
                {
                    Enabled = enabled,
                    MaxResultsPerRequest = maxResults,
                    InitialQueries = configured?.ToList() ?? []
                }
            });
            Service = new GoldenQueryService(options, NullLogger<GoldenQueryService>.Instance, demo, Repository, versions);
        }

        public IGoldenQueryRepository Repository { get; }
        public GoldenQueryService Service { get; }
        public IReadOnlyList<GoldenQueryStoreRecord> Rows { get; set; } = [];
        public Dictionary<string, IReadOnlyList<GoldenQueryStoreRecord>> RowsByTenant { get; } = new(StringComparer.Ordinal);
        public CatalogVersion Version { get; set; } = new(1, 1);
    }
}
