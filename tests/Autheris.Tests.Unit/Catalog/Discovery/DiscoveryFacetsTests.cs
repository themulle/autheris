namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

public sealed class DiscoveryFacetsTests
{
    private static CatalogCaller Caller(string sid = "S-USER", string tenant = "tenant-a") =>
        DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(sid, tenant), new TenantId(tenant));

    [Fact]
    public async Task Facets_ExcludesHiddenAndForeignTenantObjects_AndSortsOrdinalIgnoreCase()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("zeta", "t1", schema: "s2", ownerTenantId: "tenant-a"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("alpha", "t2", schema: "B_schema", ownerTenantId: "tenant-a"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("alpha", "t3", schema: "a_schema", ownerTenantId: "tenant-a"));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("alpha", "t4", schema: "a_schema", ownerTenantId: "tenant-a", objectKind: DiscoveryObjectKind.View));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("alpha", "hidden_t", schema: "a_schema", ownerTenantId: "tenant-a", hidden: true));
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("foreign", "other_t", schema: "dbo", ownerTenantId: "tenant-b"));

        var pdp = FakeAccessDecisionPoint.AllowAll();
        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);

        var facets = await service.FacetsAsync(access: null, Caller());

        facets.TotalCount.ShouldBe(4);
        facets.Domains.Select(static d => d.Domain).ShouldBe(["alpha", "zeta"]);
        facets.Domains[0].Count.ShouldBe(3);
        facets.Domains[0].Schemas.Select(static s => s.Schema).ShouldBe(["a_schema", "B_schema"]);
        facets.Domains[0].Schemas.Select(static s => s.Count).ShouldBe([2, 1]);
        facets.Domains[1].Count.ShouldBe(1);
        facets.Domains[1].Schemas.Select(static s => (s.Schema, s.Count)).ShouldBe([("s2", 1)]);

        // Unfiltered facets run without any PDP projection.
        pdp.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task Facets_WithAccessStatus_NarrowsAndMatchesKeysetTotalCount()
    {
        var repo = new FakeDiscoveryTableRepository();
        var granted = DiscoveryTestBuilder.MakeTable("sales", "orders", schema: "public");
        var pending = DiscoveryTestBuilder.MakeTable("sales", "invoices", schema: "public");
        var ungranted = DiscoveryTestBuilder.MakeTable("hr", "staff", schema: "core");
        repo.Tables.AddRange([granted, pending, ungranted]);

        var consents = new FakeDiscoveryConsentRepository();
        consents.Active.Add(new Consent
        {
            TableIdentifier = granted.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-USER"),
            TenantId = new TenantId("tenant-a"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        });

        var approvals = new FakeDiscoveryApprovalRepository();
        approvals.Requests.Add(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = pending.Identifier,
            RequesterSid = new Sid("S-USER"),
            Status = "PENDING"
        });

        var pdp = new FakeAccessDecisionPoint(r =>
            r.Object.Identifier == granted.Identifier
                ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
                : TableAccessDecision.Denied(r.Object.Identifier, "Denied"));

        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp, consents: consents, approvals: approvals);
        var caller = Caller();

        foreach (var state in new DiscoveryAccessState?[] { null, DiscoveryAccessState.Granted, DiscoveryAccessState.Requested, DiscoveryAccessState.None })
        {
            var facets = await service.FacetsAsync(state, caller);
            var list = await service.ListKeysetAsync(new DiscoveryKeysetQuery(Access: state, First: 200), caller);

            facets.TotalCount.ShouldBe(list.TotalCount!.Value);
            facets.Domains.Sum(static d => d.Count).ShouldBe(facets.TotalCount);
            facets.Domains.Sum(static d => d.Schemas.Sum(static s => s.Count)).ShouldBe(facets.TotalCount);
        }
    }

    [Fact]
    public async Task Facets_EnforcesCallerGateAndRateLimit()
    {
        var repo = new FakeDiscoveryTableRepository();
        repo.Tables.Add(DiscoveryTestBuilder.MakeTable("sales", "orders"));

        var rateLimiter = new FakeDiscoveryRateLimiter();
        var discoveryOpts = new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings { RequestsPerMinute = 1 } };
        var service = DiscoveryTestBuilder.CreateService(tables: repo, discoveryOptions: discoveryOpts, rateLimiter: rateLimiter);

        await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await service.FacetsAsync(null, DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.Anonymous())));

        var caller = Caller();
        (await service.FacetsAsync(null, caller)).TotalCount.ShouldBe(1);

        var ex = await Should.ThrowAsync<DiscoveryStatusException>(async () =>
            await service.FacetsAsync(null, caller));
        ex.Kind.ShouldBe(DiscoveryFailureKind.RateLimited);
    }
}
