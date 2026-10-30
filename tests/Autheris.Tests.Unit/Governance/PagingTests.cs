using Autheris.Domain.Security;
using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>WP-SC-10 / SEC-SC-16: keyset cursors, tenant-bound paged audit and consent listings, clamped-grant report.</summary>
public sealed class PagingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier Orders = new("default", "sales", "orders");

    [Theory]
    [InlineData(1L)]
    [InlineData(42L)]
    [InlineData(long.MaxValue)]
    public void SEC_SC_16_Cursor_RoundTrips(long position)
    {
        PageCursor.TryDecodeLong(PageCursor.Encode(position), out var decoded).ShouldBeTrue();
        decoded.ShouldBe(position);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("c1.")]
    [InlineData("c1.!!!")]
    [InlineData("c2.MQ")]
    public void SEC_SC_16_MalformedOrForeignCursor_IsRejected(string? cursor) =>
        PageCursor.TryDecodeLong(cursor, out _).ShouldBeFalse();

    [Fact]
    public void SEC_SC_16_Cursor_RejectsNegativeZeroAndNonNumericPositions()
    {
        PageCursor.TryDecodeLong(PageCursor.Encode("-5"), out _).ShouldBeFalse();
        PageCursor.TryDecodeLong(PageCursor.Encode("0"), out _).ShouldBeFalse();
        PageCursor.TryDecodeLong(PageCursor.Encode("abc"), out _).ShouldBeFalse();
        PageCursor.TryDecodeLong("c1." + new string('A', 400), out _).ShouldBeFalse();
    }

    [Fact]
    public void SEC_SC_16_PageSize_IsClamped()
    {
        new AuditLogPageQuery(Autheris.Tests.Common.TestTenants.Default, PageSize: 0).EffectivePageSize.ShouldBe(1);
        new AuditLogPageQuery(Autheris.Tests.Common.TestTenants.Default, PageSize: 100000).EffectivePageSize.ShouldBe(AuditLogPageQuery.MaxPageSize);
        new ConsentPageQuery(Autheris.Tests.Common.TestTenants.Default, PageSize: -3).EffectivePageSize.ShouldBe(1);
        new ConsentPageQuery(Autheris.Tests.Common.TestTenants.Default, PageSize: 100000).EffectivePageSize.ShouldBe(ConsentPageQuery.MaxPageSize);
    }

    // ---- consent reader --------------------------------------------------------------------------------------------------------

    private sealed class ConsentPages(List<Consent> all) : IConsentRepository
    {
        public int PageCalls { get; private set; }

        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
        {
            PageCalls++;
            var ordered = all.Where(c => c.TenantId == query.Tenant).OrderBy(c => c.Id).ToList();
            var rest = query.AfterId is { } a ? ordered.Where(c => c.Id.CompareTo(a) > 0).ToList() : ordered;
            var page = rest.Take(query.EffectivePageSize).ToList();
            return Task.FromResult(new ConsentPage(page, rest.Count > page.Count ? page[^1].Id : null));
        }

        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private static Consent C(int n, GranteeType type = GranteeType.User, DateTimeOffset? validTo = null, DateTimeOffset? validFrom = null, string tenant = "t1") => new()
    {
        Id = new Guid(n, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]),
        TenantId = new TenantId(tenant),
        TableIdentifier = Orders,
        GranteeType = type,
        GranteeSid = type == GranteeType.Role ? (Sid?)null : new Sid($"S-1-5-21-{n}"),
        RoleName = type == GranteeType.Role ? $"role{n}" : null,
        ValidFrom = validFrom ?? Now.AddDays(-1),
        ValidTo = validTo ?? Now.AddDays(10)
    };

    [Fact]
    public async Task SEC_SC_16_ConsentPages_WalkTheWholeSetOnce_WithoutDuplicatesOrGaps()
    {
        var all = Enumerable.Range(1, 25).Select(i => C(i)).ToList();
        var repo = new ConsentPages(all);

        var seen = new List<Guid>();
        Guid? after = null;
        int pages = 0;
        do
        {
            var page = await ConsentPageReader.ReadAsync(repo, new ConsentPageQuery(new TenantId("t1"), AfterId: after, PageSize: 10), false, Now);
            seen.AddRange(page.Items.Select(c => c.Id));
            after = page.NextAfterId;
            pages++;
        }
        while (after != null);

        seen.Count.ShouldBe(25);
        seen.Distinct().Count().ShouldBe(25);
        pages.ShouldBe(3);
    }

    [Fact]
    public async Task SEC_SC_16_ConsentPages_AreTenantBound()
    {
        var repo = new ConsentPages([C(1, tenant: "t1"), C(2, tenant: "t2")]);

        var page = await ConsentPageReader.ReadAsync(repo, new ConsentPageQuery(new TenantId("t1")), false, Now);

        page.Items.ShouldHaveSingleItem().TenantId.Value.ShouldBe("t1");
    }

    [Fact]
    public async Task SEC_SC_20_ClampedReport_ListsOnlyLegacyIndividualGrants_AndInactiveOnesAreHiddenByDefault()
    {
        var legacyActive = C(1, validFrom: Now.AddDays(-10), validTo: DateTimeOffset.MaxValue);           // clamped to From+180d, still active
        var legacyExpired = C(2, validFrom: Now.AddDays(-400), validTo: DateTimeOffset.MaxValue);          // clamped, already inactive
        var normal = C(3);
        var role = C(4, GranteeType.Role, validFrom: Now.AddDays(-400), validTo: DateTimeOffset.MaxValue); // standing, never clamped
        var repo = new ConsentPages([legacyActive, legacyExpired, normal, role]);
        var q = new ConsentPageQuery(new TenantId("t1"));

        var activeClamped = await ConsentPageReader.ReadAsync(repo, q, clampedOnly: true, Now);
        activeClamped.Items.Select(c => c.Id).ShouldBe([legacyActive.Id]);

        var allClamped = await ConsentPageReader.ReadAsync(repo, q with { IncludeInactive = true }, clampedOnly: true, Now);
        allClamped.Items.Select(c => c.Id).OrderBy(i => i).ShouldBe([legacyActive.Id, legacyExpired.Id]);
    }

    [Fact]
    public async Task SEC_SC_16_FilteredScan_IsBounded()
    {
        var all = Enumerable.Range(1, 400).Select(i => C(i)).ToList(); // nothing is clamped
        var repo = new ConsentPages(all);

        var page = await ConsentPageReader.ReadAsync(repo, new ConsentPageQuery(new TenantId("t1"), PageSize: 10), clampedOnly: true, Now);

        page.Items.ShouldBeEmpty();
        repo.PageCalls.ShouldBe(ConsentPageReader.MaxScanPages);
        page.NextAfterId.ShouldNotBeNull("the caller can resume after the last scanned row");
    }

    // ---- endpoints -------------------------------------------------------------------------------------------------------------

    private static DefaultHttpContext Ctx(string role, string tenant = "t1", IDataOwnershipRepository? owners = null)
    {
        var services = new ServiceCollection();
        if (owners != null) services.AddSingleton(owners);
        var ctx = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ME")], "test"));
        ctx.Items[SecurityPrincipalContext.TenantIdItemKey] = new TenantId(tenant);
        return ctx;
    }

    private static AuditLogEntry Entry(long seq) => new()
    {
        ChainSequence = seq,
        TenantId = new TenantId("t1"),
        EventType = "TABLE_QUERY",
        ActorSid = new Sid("S-1-5-21-X"),
        TargetTable = "default.sales.orders",
        Decision = "ALLOW",
        TraceId = "t"
    };

    [Fact]
    public async Task SEC_SC_16_AuditPage_GovernanceAdmin_GetsTenantPage_WithNextCursor()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        AuditLogPageQuery? seen = null;
        repo.QueryAuditLogPageAsync(Arg.Do<AuditLogPageQuery>(q => seen = q), Arg.Any<CancellationToken>())
            .Returns(new AuditLogPage([Entry(10), Entry(9)], 9));

        var result = await GovernanceEndpoints.ListAuditPageAsync(Ctx("GovernanceAdmin"), repo, null, 2, null, null, null, null);

        seen!.Tenant.ShouldBe(new TenantId("t1"));
        seen.PageSize.ShouldBe(2);
        seen.BeforeSequence.ShouldBeNull();
        (await Body(result)).ShouldContain("\"nextCursor\":\"" + PageCursor.Encode(9) + "\"");
    }

    [Fact]
    public async Task SEC_SC_16_AuditPage_ContinuesFromTheCursor_AndRejectsABadOne()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        AuditLogPageQuery? seen = null;
        repo.QueryAuditLogPageAsync(Arg.Do<AuditLogPageQuery>(q => seen = q), Arg.Any<CancellationToken>()).Returns(new AuditLogPage([], null));

        await GovernanceEndpoints.ListAuditPageAsync(Ctx("GovernanceAdmin"), repo, PageCursor.Encode(9), null, null, null, null, null);
        seen!.BeforeSequence.ShouldBe(9);

        repo.ClearReceivedCalls();
        var bad = await GovernanceEndpoints.ListAuditPageAsync(Ctx("GovernanceAdmin"), repo, "nope", null, null, null, null, null);
        StatusOf(bad).ShouldBe(StatusCodes.Status400BadRequest);
        await repo.DidNotReceiveWithAnyArgs().QueryAuditLogPageAsync(default!, default);
    }

    [Fact]
    public async Task SEC_SC_16_AuditPage_NonPrivilegedCaller_IsForbidden_AndDataOwnerNeedsAnOwnedTable()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        var owners = Substitute.For<IDataOwnershipRepository>();
        owners.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(false);

        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("Consumer"), repo, null, null, null, null, null, null)).ShouldBe(403);
        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("DataOwner", owners: owners), repo, null, null, null, null, null, null)).ShouldBe(403);
        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("DataOwner", owners: owners), repo, null, null, "default.sales.orders", null, null, null)).ShouldBe(403);
        await repo.DidNotReceiveWithAnyArgs().QueryAuditLogPageAsync(default!, default);

        owners.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(true);
        repo.QueryAuditLogPageAsync(Arg.Any<AuditLogPageQuery>(), Arg.Any<CancellationToken>()).Returns(new AuditLogPage([], null));
        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("DataOwner", owners: owners), repo, null, null, "default.sales.orders", null, null, null)).ShouldBe(200);
    }

    [Fact]
    public async Task SEC_SC_16_AuditPage_ForeignTenant_IsClusterAdminOnly()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        AuditLogPageQuery? seen = null;
        repo.QueryAuditLogPageAsync(Arg.Do<AuditLogPageQuery>(q => seen = q), Arg.Any<CancellationToken>()).Returns(new AuditLogPage([], null));

        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("GovernanceAdmin"), repo, null, null, null, null, null, "t2")).ShouldBe(403);
        await repo.DidNotReceiveWithAnyArgs().QueryAuditLogPageAsync(default!, default);

        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("ClusterAdmin"), repo, null, null, null, null, null, "t2")).ShouldBe(200);
        seen!.Tenant.ShouldBe(new TenantId("t2"));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Platform_Audit_Visible_To_ClusterAdmin_Only()
    {
        var repo = Substitute.For<IAuditLogRepository>();
        AuditLogPageQuery? seen = null;
        repo.QueryAuditLogPageAsync(Arg.Do<AuditLogPageQuery>(q => seen = q), Arg.Any<CancellationToken>()).Returns(new AuditLogPage([], null));

        // DT-A-6: a tenant administrator can never reach the platform bucket.
        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("GovernanceAdmin"), repo, null, null, null, null, null, "_platform")).ShouldBe(403);
        await repo.DidNotReceiveWithAnyArgs().QueryAuditLogPageAsync(default!, default);

        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("ClusterAdmin"), repo, null, null, null, null, null, "_platform")).ShouldBe(200);
        seen!.Tenant.ShouldBe(TenantId.Platform);

        // every other reserved value stays rejected, also for ClusterAdmin
        repo.ClearReceivedCalls();
        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("ClusterAdmin"), repo, null, null, null, null, null, "_other")).ShouldBe(403);
        StatusOf(await GovernanceEndpoints.ListAuditPageAsync(Ctx("ClusterAdmin"), repo, null, null, null, null, null, "_PLATFORM")).ShouldBe(403);
        await repo.DidNotReceiveWithAnyArgs().QueryAuditLogPageAsync(default!, default);
    }

    [Fact]
    public async Task SEC_SC_16_GrantsPage_IsTenantBound_AndMarksClampedGrants()
    {
        var legacy = C(1, validFrom: Now.AddDays(-10), validTo: DateTimeOffset.MaxValue);
        var repo = new ConsentPages([legacy, C(2, tenant: "t2")]);
        var clock = Substitute.For<TimeProvider>();
        clock.GetUtcNow().Returns(Now);

        var result = await GovernanceEndpoints.ListGrantsPageAsync(Ctx("GovernanceAdmin"), repo, clock, null, null, null, null, null, null, true);

        var body = await Body(result);
        body.ShouldContain("\"clamped\":true");
        body.ShouldContain("treated as expiring at");
        body.ShouldNotContain("S-1-5-21-2");
    }

    [Fact]
    public async Task SEC_SC_16_GrantsPage_RejectsBadFilters_AndUnauthorizedCallers()
    {
        var repo = new ConsentPages([]);
        StatusOf(await GovernanceEndpoints.ListGrantsPageAsync(Ctx("Consumer"), repo, null, null, null, null, null, null, null, null)).ShouldBe(403);
        StatusOf(await GovernanceEndpoints.ListGrantsPageAsync(Ctx("GovernanceAdmin"), repo, null, "bad", null, null, null, null, null, null)).ShouldBe(400);
        StatusOf(await GovernanceEndpoints.ListGrantsPageAsync(Ctx("GovernanceAdmin"), repo, null, null, null, null, "Wizard", null, null, null)).ShouldBe(400);
        StatusOf(await GovernanceEndpoints.ListGrantsPageAsync(Ctx("GovernanceAdmin"), repo, null, null, null, "nope", null, null, null, null)).ShouldBe(400);
        repo.PageCalls.ShouldBe(0);
    }

    private static int StatusOf(IResult result) => result switch
    {
        IStatusCodeHttpResult s => s.StatusCode ?? 200,
        _ => 200
    };

    private static async Task<string> Body(IResult result)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return await new StreamReader(ctx.Response.Body).ReadToEndAsync();
    }
}
