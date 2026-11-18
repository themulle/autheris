namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>Issues #798 / #804: the caller access filter of the discovery service (list and search).</summary>
public sealed class DiscoveryAccessStatusFilterTests
{
    private const string Caller = "S-A";
    private const string Other = "S-OTHER";

    /// <summary>
    /// <paramref name="count"/> tables <c>sales.dbo.t000..</c>; the caller holds Allow consents on <paramref name="grantedIndexes"/>. The fake
    /// decision point allows exactly the tables a consent of the requesting caller targets (zero trust, as the real one).
    /// </summary>
    private static Fixture Create(int count, int[] grantedIndexes, int[]? otherCallerGrants = null, int? pendingIndex = null, int? expiredIndex = null, int? hiddenGrantedIndex = null)
    {
        var repo = new FakeDiscoveryTableRepository();
        for (var i = 0; i < count; i++)
        {
            repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", $"t{i:000}", hidden: i == hiddenGrantedIndex));
        }

        var consents = new FakeDiscoveryConsentRepository();
        foreach (var i in grantedIndexes.Concat(hiddenGrantedIndex is { } h ? [h] : []))
        {
            consents.Active.Add(Allow(Caller, repo.Tables[i].Identifier));
        }

        foreach (var i in otherCallerGrants ?? [])
        {
            consents.Active.Add(Allow(Other, repo.Tables[i].Identifier));
        }

        if (expiredIndex is { } e)
        {
            consents.RecentlyExpired = new HashSet<TableIdentifier> { repo.Tables[e].Identifier };
        }

        var approvals = new FakeDiscoveryApprovalRepository();
        if (pendingIndex is { } p)
        {
            approvals.Requests.Add(new ConsentRequest
            {
                Id = Guid.NewGuid(),
                TableIdentifier = repo.Tables[p].Identifier,
                RequesterSid = new Sid(Caller),
                Status = "PENDING"
            });
        }

        var pdp = new FakeAccessDecisionPoint(r =>
            consents.Active.Any(c => c.GranteeSid == r.Subject.UserSid && c.TableIdentifier == r.Object.Identifier)
                ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                : TableAccessDecision.Denied(r.Object.Identifier, "Denied"));

        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp, consents: consents, approvals: approvals);
        return new Fixture(service, pdp, consents, repo.Tables);
    }

    private static Consent Allow(string sid, TableIdentifier table) => new()
    {
        TableIdentifier = table,
        Effect = ConsentEffect.Allow,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(sid),
        TenantId = new TenantId("tenant-a"),
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(30)
    };

    private static CatalogCaller CallerOf(string sid) => DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(sid, "tenant-a"));

    [Fact]
    public async Task List_Granted_CountsAndPagesTheFilteredSet()
    {
        var f = Create(300, grantedIndexes: [17, 180, 250]);

        var first = await f.Service.ListAsync(new DiscoveryQuery(First: 2, After: 0, Access: DiscoveryAccessState.Granted), CallerOf(Caller));
        var second = await f.Service.ListAsync(new DiscoveryQuery(First: 2, After: 2, Access: DiscoveryAccessState.Granted), CallerOf(Caller));

        first.TotalCount.ShouldBe(3);
        first.HasNextPage.ShouldBeTrue();
        first.Items.Select(o => o.Name).ShouldBe(["t017", "t180"]);
        second.TotalCount.ShouldBe(3);
        second.HasNextPage.ShouldBeFalse();
        second.Items.Select(o => o.Name).ShouldBe(["t250"]);
        first.Items.Concat(second.Items).ShouldAllBe(o => o.Access == DiscoveryAccessState.Granted);
    }

    [Fact]
    public async Task List_Granted_AsksTheDecisionPointOnlyForGrantCandidates()
    {
        var f = Create(300, grantedIndexes: [17, 180, 250]);

        await f.Service.ListAsync(new DiscoveryQuery(First: 50, Access: DiscoveryAccessState.Granted), CallerOf(Caller));

        // 3 candidate decisions for the filter, then the 3 page objects (read plus three write operations each); never 300.
        f.Pdp.Requests.Count(r => r.Operation == AccessOperation.Discover).ShouldBe(6);
        f.Consents.AllActiveReads.ShouldBe(1);
    }

    [Fact]
    public async Task List_EveryState_MatchesTheStateOfTheUnfilteredListing()
    {
        var f = Create(8, grantedIndexes: [1, 2], pendingIndex: 3, expiredIndex: 4);
        var caller = CallerOf(Caller);
        var all = await f.Service.ListAsync(new DiscoveryQuery(First: 200), caller);

        foreach (var state in Enum.GetValues<DiscoveryAccessState>())
        {
            var filtered = await f.Service.ListAsync(new DiscoveryQuery(First: 200, Access: state), caller);
            var expected = all.Items.Where(o => o.Access == state).Select(o => o.Name).ToList();
            filtered.Items.Select(o => o.Name).ShouldBe(expected, $"state {state}");
            filtered.TotalCount.ShouldBe(expected.Count);
        }

        all.Items.Single(o => o.Name == "t003").Access.ShouldBe(DiscoveryAccessState.Requested);
        all.Items.Single(o => o.Name == "t004").Access.ShouldBe(DiscoveryAccessState.Expired);
        (await f.Service.ListAsync(new DiscoveryQuery(First: 200, Access: DiscoveryAccessState.None), caller)).TotalCount.ShouldBe(4);
    }

    [Fact]
    public async Task Search_Granted_IsAppliedBeforeTheLimit()
    {
        // The readable tables sort last: a filter after the limit would return none of them.
        var f = Create(30, grantedIndexes: [27, 28, 29]);

        var hits = await f.Service.SearchAsync("t0", new DiscoverySearchOptions(Limit: 2, Access: DiscoveryAccessState.Granted), CallerOf(Caller));

        hits.Select(o => o.Name).ShouldBe(["t027", "t028"]);
        hits.ShouldAllBe(o => o.Access == DiscoveryAccessState.Granted);
    }

    [Fact]
    public async Task Search_RequestAccess_LeavesOutReadableTables()
    {
        var f = Create(5, grantedIndexes: [0, 1]);

        var hits = await f.Service.SearchAsync("t0", new DiscoverySearchOptions(Limit: 10, Access: DiscoveryAccessState.None), CallerOf(Caller));

        hits.Select(o => o.Name).ShouldBe(["t002", "t003", "t004"]);
        hits.ShouldAllBe(o => o.Access == DiscoveryAccessState.None);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Filter_NeverRevealsAHiddenTable_EvenWithAGrant()
    {
        var f = Create(4, grantedIndexes: [0], hiddenGrantedIndex: 3);
        var caller = CallerOf(Caller);

        foreach (var state in Enum.GetValues<DiscoveryAccessState>())
        {
            var page = await f.Service.ListAsync(new DiscoveryQuery(First: 200, Access: state), caller);
            page.Items.ShouldNotContain(o => o.Name == "t003");
            var hits = await f.Service.SearchAsync("t003", new DiscoverySearchOptions(Access: state), caller);
            hits.ShouldBeEmpty();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Filter_UsesOnlyTheCallersOwnGrants()
    {
        var f = Create(4, grantedIndexes: [0], otherCallerGrants: [1, 2]);

        var granted = await f.Service.ListAsync(new DiscoveryQuery(First: 200, Access: DiscoveryAccessState.Granted), CallerOf(Caller));
        var requestable = await f.Service.ListAsync(new DiscoveryQuery(First: 200, Access: DiscoveryAccessState.None), CallerOf(Caller));
        var otherGranted = await f.Service.ListAsync(new DiscoveryQuery(First: 200, Access: DiscoveryAccessState.Granted), CallerOf(Other));

        granted.Items.Select(o => o.Name).ShouldBe(["t000"]);
        requestable.Items.Select(o => o.Name).ShouldBe(["t001", "t002", "t003"]);
        otherGranted.Items.Select(o => o.Name).ShouldBe(["t001", "t002"]);
        f.Pdp.Requests.Where(r => r.Operation == AccessOperation.Discover).ShouldAllBe(
            r => r.Subject.UserSid.Value == Caller || r.Subject.UserSid.Value == Other);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Filter_ADeniedCandidate_IsNotGranted()
    {
        // A consent targets the table, but the decision point denies (for example an unconditional Deny): the PDP decides, not the consent.
        var f = Create(3, grantedIndexes: [0, 1]);
        var deniedPdp = new FakeAccessDecisionPoint(r => r.Object.Identifier.TableName == "t000"
            ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(r.Object.Identifier, "Denied"));
        var service = DiscoveryTestBuilder.CreateService(
            tables: CopyOf(f.Tables), pdp: deniedPdp, consents: f.Consents);

        var page = await service.ListAsync(new DiscoveryQuery(First: 200, Access: DiscoveryAccessState.Granted), CallerOf(Caller));

        page.Items.Select(o => o.Name).ShouldBe(["t000"]);
        page.TotalCount.ShouldBe(1);
    }

    [Fact]
    public async Task PatternGrant_MakesMatchingTablesCandidates()
    {
        var f = Create(3, grantedIndexes: []);
        f.Consents.Active.Add(new Consent
        {
            ObjectPattern = "sales.dbo.t00*",
            DataSource = "sales",
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(Caller),
            TenantId = new TenantId("tenant-a"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });
        var pdp = FakeAccessDecisionPoint.AllowAll();
        var service = DiscoveryTestBuilder.CreateService(tables: CopyOf(f.Tables), pdp: pdp, consents: f.Consents);

        var page = await service.ListAsync(new DiscoveryQuery(First: 200, Access: DiscoveryAccessState.Granted), CallerOf(Caller));

        page.TotalCount.ShouldBe(3);
    }

    private static FakeDiscoveryTableRepository CopyOf(List<TableMetadata> tables)
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.AddRange(tables);
        return repo;
    }

    // Declared last: the security test inventory scanner attributes a test to the nearest preceding type declaration.
    private sealed record Fixture(
        DiscoveryService Service,
        FakeAccessDecisionPoint Pdp,
        FakeDiscoveryConsentRepository Consents,
        List<TableMetadata> Tables);
}
