#pragma warning disable CA2012 // NSubstitute .Returns(...) configures a ValueTask member; the instance is not consumed here.
namespace Autheris.Tests.Unit.Governance.Requests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.ConsentRequests;
using Autheris.Tests.Unit.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class UnmaskGrantListingAndDuplicateTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TableIdentifier OrdersTable = new("pg", "public", "orders");
    private static readonly DateTimeOffset FixedNow = ConsentApiTestHarness.FixedNow;

    private static ClaimsPrincipal UserPrincipal(string sid = "S-1-5-21-USER-1", string role = "Developer") =>
        ConsentApiTestHarness.BuildUserPrincipal(TenantA.Value, sid, role);

    private static Consent NewGrant(
        PermissionGrantKind kind,
        string granteeSid = "S-1-5-21-USER-1",
        GranteeType granteeType = GranteeType.User,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null,
        bool isRevoked = false,
        IReadOnlyList<string>? targetColumns = null,
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = OrdersTable,
            Effect = ConsentEffect.Allow,
            GranteeType = granteeType,
            GranteeSid = new Sid(granteeSid),
            ValidFrom = validFrom ?? FixedNow.AddHours(-1),
            ValidTo = validTo ?? FixedNow.AddDays(14),
            IsRevoked = isRevoked,
            TenantId = TenantA,
            GrantKind = kind,
            TargetColumns = targetColumns
        };

    [Fact]
    public async Task ConsentGrantsEndpoint_ReturnsUnmaskGrantsWithGrantKindAndTargetColumns_AndFiltersByKind()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var dataGrant = NewGrant(PermissionGrantKind.Data);
        var unmaskGrant = NewGrant(PermissionGrantKind.Unmask, targetColumns: ["email", "phone"]);
        harness.Store.Consents.Add(dataGrant);
        harness.Store.Consents.Add(unmaskGrant);

        var admin = ConsentApiTestHarness.BuildUserPrincipal(TenantA.Value, "S-1-5-21-ADMIN", "GovernanceAdmin");

        // Unfiltered listing returns both Data and Unmask grants with grantKind and targetColumns
        var allResp = await harness.ListGrantsAsync(admin);
        allResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var items = allResp.ReadJson().GetProperty("items");
        items.GetArrayLength().ShouldBe(2);

        var byId = items.EnumerateArray().ToDictionary(e => e.GetProperty("id").GetGuid());
        byId[dataGrant.Id].GetProperty("grantKind").GetString().ShouldBe("Data");
        byId[dataGrant.Id].GetProperty("targetColumns").ValueKind.ShouldBe(JsonValueKind.Null);

        byId[unmaskGrant.Id].GetProperty("grantKind").GetString().ShouldBe("Unmask");
        byId[unmaskGrant.Id].GetProperty("targetColumns").EnumerateArray().Select(x => x.GetString()).ToArray()
            .ShouldBe(["email", "phone"]);

        // ?kind=unmask returns only the unmask grant
        var unmaskOnly = await harness.ListGrantsAsync(admin, kind: "unmask");
        unmaskOnly.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var unmaskItems = unmaskOnly.ReadJson().GetProperty("items");
        unmaskItems.GetArrayLength().ShouldBe(1);
        unmaskItems[0].GetProperty("id").GetGuid().ShouldBe(unmaskGrant.Id);

        // ?kind=data returns only the data grant
        var dataOnly = await harness.ListGrantsAsync(admin, kind: "data");
        dataOnly.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var dataItems = dataOnly.ReadJson().GetProperty("items");
        dataItems.GetArrayLength().ShouldBe(1);
        dataItems[0].GetProperty("id").GetGuid().ShouldBe(dataGrant.Id);

        // Invalid kind is rejected with 400
        var invalidKind = await harness.ListGrantsAsync(admin, kind: "bogus");
        invalidKind.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Single grant read includes grantKind and targetColumns
        var single = await harness.GetGrantByIdAsync(admin, unmaskGrant.Id);
        single.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var singleJson = single.ReadJson();
        singleJson.GetProperty("grantKind").GetString().ShouldBe("Unmask");
        singleJson.GetProperty("targetColumns").EnumerateArray().Select(x => x.GetString()).ToArray()
            .ShouldBe(["email", "phone"]);
    }

    [Fact]
    public async Task GovernanceGrantsEndpoint_ReturnsUnmaskGrantsWithGrantKindAndTargetColumns_AndSupportsKindFilter()
    {
        var dataGrant = NewGrant(PermissionGrantKind.Data);
        var unmaskGrant = NewGrant(PermissionGrantKind.Unmask, targetColumns: ["email"]);

        var repo = Substitute.For<IConsentRepository>();
        repo.GetConsentPageAsync(Arg.Any<ConsentPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var q = ci.Arg<ConsentPageQuery>();
                var all = new[] { dataGrant, unmaskGrant };
                var filtered = q.GrantKind is null ? all : all.Where(g => g.GrantKind == q.GrantKind).ToArray();
                return Task.FromResult(new ConsentPage(filtered, null));
            });

        var clock = new FakeTimeProvider(FixedNow);
        var services = new ServiceCollection();
        services.AddLogging();
        await using var sp = services.BuildServiceProvider();

        DefaultHttpContext BuildContext()
        {
            var ctx = new DefaultHttpContext
            {
                RequestServices = sp,
                User = ConsentApiTestHarness.BuildUserPrincipal(TenantA.Value, "S-1-5-21-ADMIN", "GovernanceAdmin")
            };
            ctx.Response.Body = new MemoryStream();
            return ctx;
        }

        // Unfiltered returns both Data and Unmask
        var ctxAll = BuildContext();
        var resAll = await GovernanceEndpoints.ListGrantsPageAsync(
            ctxAll, repo, clock, cursor: null, pageSize: 50, table: null, granteeType: null, grantee: null, includeInactive: false, clamped: false, kind: null);
        await resAll.ExecuteAsync(ctxAll);
        ctxAll.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ctxAll.Response.Body.Position = 0;
        var jsonAll = JsonDocument.Parse(await new StreamReader(ctxAll.Response.Body).ReadToEndAsync()).RootElement;
        var items = jsonAll.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetGuid());
        items.Count.ShouldBe(2);
        items[dataGrant.Id].GetProperty("grantKind").GetString().ShouldBe("Data");
        items[unmaskGrant.Id].GetProperty("grantKind").GetString().ShouldBe("Unmask");
        items[unmaskGrant.Id].GetProperty("targetColumns").EnumerateArray().Select(x => x.GetString()).ToArray().ShouldBe(["email"]);

        // Filtered ?kind=unmask returns only unmask grant
        var ctxUnmask = BuildContext();
        var resUnmask = await GovernanceEndpoints.ListGrantsPageAsync(
            ctxUnmask, repo, clock, cursor: null, pageSize: 50, table: null, granteeType: null, grantee: null, includeInactive: false, clamped: false, kind: "unmask");
        await resUnmask.ExecuteAsync(ctxUnmask);
        ctxUnmask.Response.StatusCode.ShouldBe(StatusCodes.Status200OK);
        ctxUnmask.Response.Body.Position = 0;
        var jsonUnmask = JsonDocument.Parse(await new StreamReader(ctxUnmask.Response.Body).ReadToEndAsync()).RootElement;
        jsonUnmask.GetProperty("items").GetArrayLength().ShouldBe(1);
        jsonUnmask.GetProperty("items")[0].GetProperty("id").GetGuid().ShouldBe(unmaskGrant.Id);

        // Invalid kind returns 400
        var ctxInvalid = BuildContext();
        var resInvalid = await GovernanceEndpoints.ListGrantsPageAsync(
            ctxInvalid, repo, clock, cursor: null, pageSize: 50, table: null, granteeType: null, grantee: null, includeInactive: false, clamped: false, kind: "unknown");
        await resInvalid.ExecuteAsync(ctxInvalid);
        ctxInvalid.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task AccessExplanationService_ListGrants_IncludesUnmaskGrantsWithUnmaskOperationAndTargetColumns()
    {
        var pdp = Substitute.For<IAccessDecisionPoint>();
        var tables = Substitute.For<ITableMetadataRepository>();
        var visibility = Substitute.For<ICatalogVisibilityService>();
        var owners = Substitute.For<IContainerOwnership>();
        var consents = Substitute.For<IConsentRepository>();
        var resolver = Substitute.For<IExplainSubjectResolver>();
        var audit = Substitute.For<IAuditWriter>();

        var meta = new TableMetadata
        {
            Table = new Table { SourceName = "pg", SchemaName = "public", TableName = "orders", IsActive = true },
            Identifier = OrdersTable,
            OwnerTenantId = TenantA.Value
        };
        tables.GetTableMetadataAsync(OrdersTable, Arg.Any<CancellationToken>()).Returns(meta);
        visibility.FindVisibleAsync(Arg.Any<CatalogCaller>(), OrdersTable, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<CatalogVisibleRow?>(new CatalogVisibleRow(meta, CatalogOutputShape.Full)));

        var dataGrant = NewGrant(PermissionGrantKind.Data);
        var unmaskGrant = NewGrant(PermissionGrantKind.Unmask, targetColumns: ["email"]);
        ConsentPageQuery? capturedQuery = null;
        consents.GetConsentPageAsync(Arg.Any<ConsentPageQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedQuery = ci.Arg<ConsentPageQuery>();
                return Task.FromResult(new ConsentPage([dataGrant, unmaskGrant], null));
            });

        var svc = new AccessExplanationService(pdp, tables, visibility, owners, consents, resolver, audit, new FakeTimeProvider(FixedNow));
        var caller = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN"),
            TenantId = TenantA,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(["GovernanceAdmin"]),
            AuthenticationScheme = "test"
        };

        var result = await svc.ListGrantsAsync(caller, "pg.public.orders");

        result.Outcome.ShouldBe(ExplainOutcome.Ok);
        capturedQuery.ShouldNotBeNull();
        capturedQuery!.GrantKind.ShouldBeNull();

        result.Grants.ShouldNotBeNull();
        result.Grants!.Count.ShouldBe(2);

        var dataView = result.Grants.Single(g => g.GrantId == dataGrant.Id);
        dataView.GrantKind.ShouldBe("Data");
        dataView.Operations.ShouldBe(["Read"]);
        dataView.TargetColumns.ShouldBeNull();

        var unmaskView = result.Grants.Single(g => g.GrantId == unmaskGrant.Id);
        unmaskView.GrantKind.ShouldBe("Unmask");
        unmaskView.Operations.ShouldBe(["Unmask"]);
        unmaskView.TargetColumns.ShouldBe(["email"]);
    }

    [Fact]
    public async Task SubmitUnmaskRequest_WhenAlreadyCoveredByActiveUnmaskGrant_Returns409AlreadyCoveredWithGrantId()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var activeUnmask = NewGrant(
            PermissionGrantKind.Unmask,
            granteeSid: "S-1-5-21-USER-1",
            validFrom: FixedNow.AddHours(-2),
            validTo: FixedNow.AddDays(14),
            targetColumns: ["email", "phone"]);
        harness.Store.Consents.Add(activeUnmask);

        var user = UserPrincipal("S-1-5-21-USER-1");

        // Requesting a subset of columns within the existing validity window is refused with 409 ALREADY_COVERED
        var covered = await harness.CreateRequestAsync(
            user,
            new CreateConsentRequestBody(
                Domain: "pg",
                Schema: "public",
                TableName: "orders",
                Justification: "Need clear email for support case",
                DurationDays: 7,
                Kind: "unmask",
                Target: new CreateConsentRequestTarget(Domain: "pg", Schema: "public", TableName: "orders", Columns: ["EMAIL"])));

        covered.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        var problem = covered.ReadJson();
        problem.GetProperty("code").GetString().ShouldBe("ALREADY_COVERED");
        problem.GetProperty("grantId").GetString().ShouldBe(activeUnmask.Id.ToString("D"));
        harness.Store.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task SubmitUnmaskRequest_AllowsWhenExtensionOrUncoveredColumnOrRevokedOrDifferentGranteeType()
    {
        await using var harness = ConsentApiTestHarness.Create();
        harness.Store.Consents.Add(NewGrant(
            PermissionGrantKind.Unmask,
            granteeSid: "S-1-5-21-USER-1",
            validFrom: FixedNow.AddHours(-2),
            validTo: FixedNow.AddDays(7),
            targetColumns: ["email"]));
        // Revoked grant covering phone must not block requests
        harness.Store.Consents.Add(NewGrant(
            PermissionGrantKind.Unmask,
            granteeSid: "S-1-5-21-USER-1",
            validFrom: FixedNow.AddHours(-2),
            validTo: FixedNow.AddDays(30),
            isRevoked: true,
            targetColumns: ["phone"]));
        // Active Data grant must not block unmask requests
        harness.Store.Consents.Add(NewGrant(
            PermissionGrantKind.Data,
            granteeSid: "S-1-5-21-USER-1",
            validFrom: FixedNow.AddHours(-2),
            validTo: FixedNow.AddDays(30)));
        // Active Group unmask grant must not block a User unmask request for the same string
        harness.Store.Consents.Add(NewGrant(
            PermissionGrantKind.Unmask,
            granteeSid: "S-1-5-21-USER-1",
            granteeType: GranteeType.Group,
            validFrom: FixedNow.AddHours(-2),
            validTo: FixedNow.AddDays(30),
            targetColumns: ["phone"]));

        var user = UserPrincipal("S-1-5-21-USER-1");

        // 1. Extending beyond the active grant's validTo (14d > 7d) is allowed
        var extension = await harness.CreateRequestAsync(
            user,
            new CreateConsentRequestBody(
                Domain: "pg",
                Schema: "public",
                TableName: "orders",
                Justification: "Extend clear email access for two weeks",
                DurationDays: 14,
                Kind: "unmask",
                Target: new CreateConsentRequestTarget(Domain: "pg", Schema: "public", TableName: "orders", Columns: ["email"])));
        extension.StatusCode.ShouldBe(StatusCodes.Status201Created);

        // 2. Requesting an additional uncovered column ("phone") is allowed
        var additionalCol = await harness.CreateRequestAsync(
            user,
            new CreateConsentRequestBody(
                Domain: "pg",
                Schema: "public",
                TableName: "orders",
                Justification: "Need phone column in clear form too",
                DurationDays: 5,
                Kind: "unmask",
                Target: new CreateConsentRequestTarget(Domain: "pg", Schema: "public", TableName: "orders", Columns: ["email", "phone"])));
        additionalCol.StatusCode.ShouldBe(StatusCodes.Status201Created);
    }

    [Fact]
    public async Task SubmitUnmaskRequest_WhenUnionOfActiveUnmaskGrantsCoversAllColumns_ThrowsAlreadyCovered()
    {
        await using var harness = ConsentApiTestHarness.Create();
        var g1 = NewGrant(
            PermissionGrantKind.Unmask,
            granteeSid: "S-1-5-21-USER-1",
            validFrom: FixedNow.AddHours(-2),
            validTo: FixedNow.AddDays(10),
            targetColumns: ["email"]);
        var g2 = NewGrant(
            PermissionGrantKind.Unmask,
            granteeSid: "S-1-5-21-USER-1",
            validFrom: FixedNow.AddHours(-2),
            validTo: FixedNow.AddDays(12),
            targetColumns: ["phone"]);
        harness.Store.Consents.Add(g1);
        harness.Store.Consents.Add(g2);

        var user = UserPrincipal("S-1-5-21-USER-1");

        var covered = await harness.CreateRequestAsync(
            user,
            new CreateConsentRequestBody(
                Domain: "pg",
                Schema: "public",
                TableName: "orders",
                Justification: "Requesting both email and phone already covered across two grants",
                DurationDays: 7,
                Kind: "unmask",
                Target: new CreateConsentRequestTarget(Domain: "pg", Schema: "public", TableName: "orders", Columns: ["email", "phone"])));

        covered.StatusCode.ShouldBe(StatusCodes.Status409Conflict);
        var problem = covered.ReadJson();
        problem.GetProperty("code").GetString().ShouldBe("ALREADY_COVERED");
        problem.GetProperty("grantId").GetString().ShouldBeOneOf(g1.Id.ToString("D"), g2.Id.ToString("D"));
    }
}
