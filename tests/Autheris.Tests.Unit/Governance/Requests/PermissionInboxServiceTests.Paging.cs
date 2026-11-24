using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Requests;

/// <summary>Inbox paging, scan cost bounds (SEC-1009-50) and fail-closed reads of the container grant overview.</summary>
[Trait("Category", "Security")]
public sealed partial class PermissionInboxServiceTests
{
    private static List<ConsentRequest> Timeline(int count, Func<int, TableIdentifier> table, Func<int, string>? requester = null)
    {
        var list = new List<ConsentRequest>(count);
        for (var i = 0; i < count; i++)
        {
            list.Add(Pending(table(i), requester?.Invoke(i) ?? "user:bob", DateTimeOffset.UnixEpoch.AddMinutes(i)));
        }

        return list;
    }

    [Fact]
    public async Task Every_eligible_request_appears_exactly_once_across_inbox_pages_when_repository_pages_are_cut()
    {
        // 250 requests over three repository pages; every second one is decidable by the sales owner.
        var all = Timeline(250, i => i % 2 == 0 ? OrdersTable : PayrollTable);
        var eligible = all.Where(r => r.TableIdentifier.Equals(OrdersTable)).Select(r => r.Id).ToHashSet();
        var fx = new InboxFixture(all);
        var owner = ContextFor("user:owner", groups: [SalesOwners]);

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await fx.Service.GetInboxAsync(owner, TenantA, cursor, 7);
            page.Items.Count.ShouldBeLessThanOrEqualTo(7);
            seen.AddRange(page.Items.Select(i => i.Request.Id));
            cursor = page.NextCursor;
            pages++;
            pages.ShouldBeLessThan(100);
        }
        while (cursor is not null);

        seen.Count.ShouldBe(eligible.Count);
        seen.ToHashSet().SetEquals(eligible).ShouldBeTrue();
    }

    [Fact]
    public async Task Inbox_is_ordered_newest_first_and_its_cursor_resumes_after_the_last_item()
    {
        var all = Timeline(5, _ => OrdersTable);
        var fx = new InboxFixture(all);
        var owner = ContextFor("user:owner", groups: [SalesOwners]);

        var first = await fx.Service.GetInboxAsync(owner, TenantA, null, 2);
        first.Items.Select(i => i.Request.Id).ShouldBe([all[4].Id, all[3].Id]);
        first.NextCursor.ShouldBe(ConsentRequestCursor.For(all[3]));

        var rest = await fx.Service.GetInboxAsync(owner, TenantA, first.NextCursor, 50);
        rest.Items.Select(i => i.Request.Id).ShouldBe([all[2].Id, all[1].Id, all[0].Id]);
        rest.NextCursor.ShouldBeNull();
    }

    [Fact]
    public async Task Malformed_inbox_cursor_is_rejected_not_reset_to_the_first_page()
    {
        var fx = new InboxFixture(Timeline(3, _ => OrdersTable));

        await Should.ThrowAsync<ArgumentException>(() =>
            fx.Service.GetInboxAsync(ContextFor("user:owner", groups: [SalesOwners]), TenantA, "not-a-cursor", 10));
        fx.RequestPageQueries.ShouldBe(0);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("Consumer", false)]
    public async Task Caller_without_any_decider_capacity_never_triggers_a_scan(string? role, bool degraded)
    {
        var fx = new InboxFixture(Timeline(20, _ => OrdersTable));
        var caller = ContextFor(
            "user:mallory",
            role,
            groups: degraded ? [SalesOwners] : null,
            groupStatus: degraded ? GroupResolutionStatus.Degraded : GroupResolutionStatus.Complete);

        var page = await fx.Service.GetInboxAsync(caller, TenantA, null, 50);

        page.Items.ShouldBeEmpty();
        page.NextCursor.ShouldBeNull();
        fx.RequestPageQueries.ShouldBe(0);
        fx.ProtectionLookups.ShouldBe(0);
    }

    [Fact]
    public async Task Each_container_is_resolved_once_per_inbox_call()
    {
        // 60 requests from 60 requesters over two tables in two containers.
        var all = Timeline(60, i => i % 2 == 0 ? OrdersTable : PayrollTable, i => "user:r" + i);
        var fx = new InboxFixture(all);

        var page = await fx.Service.GetInboxAsync(ContextFor("user:owner", groups: [SalesOwners]), TenantA, null, 100);

        page.Items.Count.ShouldBe(30);
        fx.RequestPageQueries.ShouldBe(1);
        fx.ProtectionLookups.ShouldBe(2);
        fx.DomainLookups.ShouldBeLessThanOrEqualTo(2);
    }

    [Fact]
    public async Task Container_overview_requires_complete_group_resolution_even_for_governance()
    {
        var fx = new InboxFixture([]);
        var container = new ContainerRef(ContainerKind.Domain, SalesDomain);

        var degradedDg = ContextFor("user:dg", "GovernanceAdmin", groupStatus: GroupResolutionStatus.Degraded);
        (await fx.Service.GetContainerGrantsAsync(degradedDg, TenantA, container, null, 50, false)).ShouldBeNull();
        fx.GrantQueries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Container_overview_lists_matching_pattern_grants_and_catalog_grants_on_the_first_page()
    {
        var matching = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ObjectPattern = "sales.dbo.*",
            DataSource = "sales",
            TableIdentifier = default,
            Effect = ConsentEffect.Allow,
            GranteeSid = new Sid("user:alice")
        };
        var unrelated = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            ObjectPattern = "hr.dbo.*",
            DataSource = "hr",
            TableIdentifier = default,
            Effect = ConsentEffect.Allow,
            GranteeSid = new Sid("user:alice")
        };
        var catalogGrant = new CatalogVisibilityGrant
        {
            TenantId = TenantA,
            ContainerKind = ContainerKind.Domain,
            ContainerId = SalesDomain,
            GranteeSid = new Sid("user:alice")
        };
        var fx = new InboxFixture([], patternGrants: [matching, unrelated], catalogGrants: [catalogGrant], domainTables: [OrdersTable]);
        var container = new ContainerRef(ContainerKind.Domain, SalesDomain);
        var dg = ContextFor("user:dg", "GovernanceAdmin");

        var first = await fx.Service.GetContainerGrantsAsync(dg, TenantA, container, null, 50, false);
        first.ShouldNotBeNull();
        first.PatternGrants.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(matching.Id);
        first.CatalogGrants.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(catalogGrant.Id);
        first.PatternGrantsTruncated.ShouldBeFalse();
        first.CatalogGrantsTruncated.ShouldBeFalse();

        var later = await fx.Service.GetContainerGrantsAsync(dg, TenantA, container, Guid.NewGuid(), 50, false);
        later.ShouldNotBeNull();
        later.PatternGrants.ShouldBeNull();
        later.CatalogGrants.ShouldBeNull();
    }
}
