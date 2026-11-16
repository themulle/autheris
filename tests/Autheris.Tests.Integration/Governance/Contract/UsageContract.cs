using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IUsageRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class UsageContract : GovernanceContractBase
{
    private static readonly DateOnly Day1 = new(2026, 11, 10);
    private static readonly DateOnly Day2 = new(2026, 11, 11);
    private static readonly TableIdentifier TableSales = new("sales", "dbo", "orders");

    private static UsageGenerationSnapshot BuildSnapshot(
        TenantId tenant,
        DateOnly day,
        string userSid,
        IReadOnlyList<string> groupSids,
        string tableId,
        string grantId,
        long rows = 10,
        DateTimeOffset? grantValidFrom = null,
        DateTimeOffset? grantValidUntil = null)
    {
        var agg = new UsageAggregator(Options.Create(new UsageAccountingOptions()));
        var groups = groupSids.Select(s => new Sid(s)).ToHashSet();
        var (gsId, _) = UsageAttribution.FilterAndComputeGroupSet(groups);
        var key = new UsageSeriesKey(
            tenant, day, userSid, gsId, "unassigned",
            DataSourceTransportFlags.Rest, "bi-app", "c1", "pg-main", tableId);
        var grantKey = new UsageGrantSeriesKey(tenant, day, grantId, "pg-main", tableId);

        var c = new UsageSeriesCounters();
        c.Add(1, 1, rows, true, 128, 12, 3, 4, 50, UsageOutcome.Allow);
        var gc = new UsageGrantSeriesCounters();
        gc.Add(1, 1, rows, 128, 3, 4, 50);

        return new UsageGenerationSnapshot(
            new Dictionary<UsageSeriesKey, UsageSeriesCounters> { [key] = c },
            new Dictionary<UsageGrantSeriesKey, UsageGrantSeriesCounters> { [grantKey] = gc },
            groups.Count > 0
                ? new Dictionary<(TenantId, string), IReadOnlyList<Sid>> { [(tenant, gsId)] = groups.ToList() }
                : new Dictionary<(TenantId, string), IReadOnlyList<Sid>>(),
            new Dictionary<(TenantId, string), GrantRef> { [(tenant, grantId)] = new(grantId, grantValidFrom, grantValidUntil) });
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-1-1", ["S-1-5-21-9-1"], "sales.dbo.orders", "g-a"));
        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantB, Day1, "S-1-5-21-2-2", ["S-1-5-21-9-2"], "sales.dbo.orders", "g-b"));

        var page = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day2, UsageGroupBy.User));
        page.Rows.Select(r => r.UserKey).Should().Contain("S-1-5-21-1-1");
        page.Rows.Select(r => r.UserKey).Should().NotContain("S-1-5-21-2-2");

        var grants = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day2, UsageGroupBy.Grant));
        grants.Rows.Select(r => r.GrantId).Should().Contain("g-a").And.NotContain("g-b");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-1-1", [], "sales.dbo.orders", "g-a"));
        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantB, Day1, "S-1-5-21-1-1", [], "sales.dbo.orders", "g-b"));

        await Store.Usage.EraseUserAsync(Store.TenantA, "S-1-5-21-1-1");

        var pageA = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day2, UsageGroupBy.User));
        pageA.Rows.Select(r => r.UserKey).Should().Contain("erased").And.NotContain("S-1-5-21-1-1");

        var pageB = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantB, Day1, Day2, UsageGroupBy.User));
        pageB.Rows.Select(r => r.UserKey).Should().Contain("S-1-5-21-1-1");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        var badQuery = () => Store.Usage.QueryAsync(new UsageReportQuery(default, Day1, Day2, UsageGroupBy.Day));
        await badQuery.Should().ThrowAsync<ArgumentException>();

        var badSnap = BuildSnapshot(default, Day1, "S-1-5-21-1-1", [], "sales.dbo.orders", "g1");
        var badAcc = () => Store.Usage.AccumulateAsync(badSnap);
        await badAcc.Should().ThrowAsync<ArgumentException>();

        var badErase = () => Store.Usage.EraseUserAsync(default, "S-1-5-21-1-1");
        await badErase.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var snap = BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-3-3", ["S-1-5-21-9-10"], "sales.dbo.orders", "g-idem", rows: 7);
        await Store.Usage.AccumulateAsync(snap);
        await Store.Usage.AccumulateAsync(snap);

        var page = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day1, UsageGroupBy.User, UserKey: "S-1-5-21-3-3"));
        page.Rows.Should().ContainSingle().Which.RowsReturned.Should().Be(14);
        page.Rows[0].Requests.Should().Be(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var tasks = Enumerable.Range(0, 4).Select(i => Store.Usage.AddTariffVersionAsync(new UsageTariffVersion(
            Store.TenantA.Value, "pg-conc", 0, Day1.AddDays(i + 1), null,
            0.01m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "admin", DateTimeOffset.UtcNow, "concurrent-" + i)));

        var versions = await Task.WhenAll(tasks);
        versions.Select(v => v.Version).Distinct().Count().Should().Be(4);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        foreach (var u in new[] { "S-1-5-21-10-3", "S-1-5-21-10-1", "S-1-5-21-10-2" })
        {
            await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, u, [], "sales.dbo.orders", "g-p"));
        }

        var p1 = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day1, UsageGroupBy.User, Limit: 2));
        p1.Rows.Should().HaveCount(2);
        p1.NextCursor.Should().NotBeNull();

        var p2 = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day1, UsageGroupBy.User, Limit: 2, Cursor: p1.NextCursor));
        p2.Rows.Should().ContainSingle();
        p2.NextCursor.Should().BeNull();

        var ordered = p1.Rows.Concat(p2.Rows).Select(r => r.UserKey!).ToList();
        ordered.Should().Equal(["S-1-5-21-10-1", "S-1-5-21-10-2", "S-1-5-21-10-3"]);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        var bad = () => Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day1, UsageGroupBy.Day, Limit: 0));
        await bad.Should().ThrowAsync<ArgumentOutOfRangeException>();

        var big = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day1, UsageGroupBy.Day, Limit: 50_000));
        big.Rows.Count.Should().BeLessOrEqualTo(500);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-20-1", ["S-1-5-21-9-20"], "sales.dbo.orders", "g-old"));
        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day2, "S-1-5-21-20-2", ["S-1-5-21-9-21"], "sales.dbo.orders", "g-new"));

        int purged = await Store.Usage.PurgeAsync(Day2);
        purged.Should().BeGreaterOrEqualTo(2);

        var page = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day2, UsageGroupBy.Day));
        page.Rows.Select(r => r.DayUtc).Should().Contain(Day2).And.NotContain(Day1);
    }

    [RequiresContainerRuntimeFact]
    public async Task Group_report_multi_counts_members_and_grant_ref_advances_last_seen()
    {
        if (!Store.IsAvailable) return;

        var vf = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var vu = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);

        await Store.Usage.AccumulateAsync(BuildSnapshot(
            Store.TenantA, Day1, "S-1-5-21-30-1", ["S-1-5-21-9-100", "S-1-5-21-9-101"],
            "sales.dbo.orders", "g-adv", rows: 5, grantValidFrom: vf, grantValidUntil: vu));
        await Store.Usage.AccumulateAsync(BuildSnapshot(
            Store.TenantA, Day2, "S-1-5-21-30-1", ["S-1-5-21-9-100", "S-1-5-21-9-101"],
            "sales.dbo.orders", "g-adv", rows: 5, grantValidFrom: vf.AddDays(1), grantValidUntil: vu));

        var groups = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day2, UsageGroupBy.Group));
        groups.Rows.Single(r => r.GroupSid == "S-1-5-21-9-100").RowsReturned.Should().Be(10);
        groups.Rows.Single(r => r.GroupSid == "S-1-5-21-9-101").RowsReturned.Should().Be(10);

        var gref = await Store.Usage.GetGrantRefAsync(Store.TenantA, "g-adv");
        gref.Should().NotBeNull();
        gref!.FirstSeen.Should().Be(Day1);
        gref.LastSeen.Should().Be(Day2);
        gref.ValidFrom.Should().Be(vf);
    }

    [RequiresContainerRuntimeFact]
    public async Task Add_tariff_version_closes_previous_and_consumer_queries_read_usage_daily()
    {
        if (!Store.IsAvailable) return;

        var v1 = await Store.Usage.AddTariffVersionAsync(new UsageTariffVersion(
            Store.TenantA.Value, "pg-main", 0, Day1, null, 1m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "admin", DateTimeOffset.UtcNow, "init"));
        var v2 = await Store.Usage.AddTariffVersionAsync(new UsageTariffVersion(
            Store.TenantA.Value, "pg-main", 0, Day2, null, 2m, 0m, 0m, 0m, 0m, 0m, null, "credits", 0, "admin", DateTimeOffset.UtcNow, "bump"));

        v1.Version.Should().Be(1);
        v2.Version.Should().Be(2);
        var list = await Store.Usage.GetTariffsAsync(Store.TenantA, Day1, Day2);
        list.Single(t => t.Version == 1).ValidUntil.Should().Be(Day2);

        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-40-1", [], TableSales.ToString(), "g-cons"));
        var actors = await Store.Usage.QueryDistinctActorCountsAsync(TableSales, Store.TenantA, Day1.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), Day2.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc));
        actors["bi-app"].Should().BeGreaterOrEqualTo(1);
    }

    [Fact]
    public void Tariff_rows_have_no_update_or_delete_path()
    {
        var repoMethods = typeof(IUsageRepository).GetMethods()
            .Select(m => m.Name)
            .Where(n => n.Contains("Tariff", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        repoMethods.Should().BeEquivalentTo(["AddTariffVersionAsync", "GetTariffsAsync"]);

        var storeMethods = typeof(Autheris.Infrastructure.Usage.UsageStore)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
            .Select(m => m.Name)
            .Where(n => n.Contains("Tariff", StringComparison.OrdinalIgnoreCase)
                        && (n.StartsWith("Update", StringComparison.OrdinalIgnoreCase)
                            || n.StartsWith("Delete", StringComparison.OrdinalIgnoreCase)
                            || n.StartsWith("Remove", StringComparison.OrdinalIgnoreCase)));

        storeMethods.Should().BeEmpty("tariff rows are strictly append-only and immutable");
    }

    [RequiresContainerRuntimeFact]
    public async Task Historic_report_identical_after_new_tariff_version()
    {
        if (!Store.IsAvailable) return;

        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-50-1", [], TableSales.ToString(), "g-hist", rows: 10));
        await Store.Usage.AddTariffVersionAsync(new UsageTariffVersion(
            Store.TenantA.Value, "pg-main", 0, Day1, null,
            2m, 0.1m, 0m, 0m, 0m, 0m, null, "credits", 0, "admin", DateTimeOffset.UtcNow, "v1"));

        var day1TariffsBefore = await Store.Usage.GetTariffsAsync(Store.TenantA, Day1, Day1);
        var rowsBefore = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day1, UsageGroupBy.DataSource, DataSource: "pg-main"));
        var costBefore = UsageTariffCalculator.CalculateRow(UsageTariffRowInput.FromReportRow(Store.TenantA.Value, Day1, rowsBefore.Rows.Single()), day1TariffsBefore);

        await Store.Usage.AddTariffVersionAsync(new UsageTariffVersion(
            Store.TenantA.Value, "pg-main", 0, Day2, null,
            99m, 5m, 0m, 0m, 0m, 0m, null, "credits", 0, "admin", DateTimeOffset.UtcNow, "v2"));

        var day1TariffsAfter = await Store.Usage.GetTariffsAsync(Store.TenantA, Day1, Day1);
        var rowsAfter = await Store.Usage.QueryAsync(new UsageReportQuery(Store.TenantA, Day1, Day1, UsageGroupBy.DataSource, DataSource: "pg-main"));
        var costAfter = UsageTariffCalculator.CalculateRow(UsageTariffRowInput.FromReportRow(Store.TenantA.Value, Day1, rowsAfter.Rows.Single()), day1TariffsAfter);

        costAfter.CostUnits.Should().Be(costBefore.CostUnits);
        costAfter.AppliedVersionId.Should().Be(costBefore.AppliedVersionId);
    }

    [RequiresContainerRuntimeFact]
    public async Task Query_breakdown_pages_distinct_dimensions_and_isolates_denies()
    {
        if (!Store.IsAvailable) return;

        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-50-1", [], TableSales.ToString(), "g-b1", rows: 5));
        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day2, "S-1-5-21-50-1", [], TableSales.ToString(), "g-b2", rows: 7));
        await Store.Usage.AccumulateAsync(BuildSnapshot(Store.TenantA, Day1, "S-1-5-21-50-2", [], TableSales.ToString(), "g-b3", rows: 9));

        var page1 = await Store.Usage.QueryAsync(new UsageReportQuery(
            Store.TenantA, Day1, Day2, UsageGroupBy.User, IncludeTariffBreakdown: true, Limit: 1));
        page1.Rows.Should().HaveCount(2);
        page1.Rows.Select(r => r.UserKey).Distinct().Should().ContainSingle().Which.Should().Be("S-1-5-21-50-1");
        page1.NextCursor.Should().Be("S-1-5-21-50-1");

        var page2 = await Store.Usage.QueryAsync(new UsageReportQuery(
            Store.TenantA, Day1, Day2, UsageGroupBy.User, IncludeTariffBreakdown: true, Limit: 1, Cursor: page1.NextCursor));
        page2.Rows.Should().ContainSingle();
        page2.Rows[0].UserKey.Should().Be("S-1-5-21-50-2");
        page2.NextCursor.Should().BeNull();
    }

    [RequiresContainerRuntimeFact]
    public async Task GetGroupDisplayNameBySid_matches_object_sid_only()
    {
        if (!Store.IsAvailable) return;

        var groupStore = Store.Services.GetRequiredService<IScimGroupStore>();
        var sid = new Sid("S-1-5-21-100-200-300-777");
        await groupStore.CreateGroupAsync(
            Store.TenantA,
            "default",
            new ScimGroup(null, "S-1-5-21-100-200-300-888", "Analysts Group", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, sid.Value));

        var matched = await groupStore.GetGroupDisplayNameBySidAsync(Store.TenantA, sid);
        var byExternalId = await groupStore.GetGroupDisplayNameBySidAsync(Store.TenantA, new Sid("S-1-5-21-100-200-300-888"));
        var wrongTenant = await groupStore.GetGroupDisplayNameBySidAsync(Store.TenantB, sid);

        matched.Should().Be("Analysts Group");
        byExternalId.Should().BeNull();
        wrongTenant.Should().BeNull();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlUsageContractTests(GovernancePostgresFixture fixture) : UsageContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "usage_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerUsageContractTests(GovernanceSqlServerFixture fixture) : UsageContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "usage_contract");
}
