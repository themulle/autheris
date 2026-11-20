namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Catalog.Discovery;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

public sealed class ExternalCatalogFacetsTests
{
    private const string UserSid = "S-1-5-21-FACETS-USER";

    private static ClaimsPrincipal Person() =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", UserSid, scopes: ExternalApiPermissions.CatalogRead);

    [Fact]
    public async Task Facets_PersonAndAppOnly_ReturnSortedDomainAndSchemaCounts()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "sync-app", enabled: true);

        harness.AddTable("zeta", "z1", schema: "s2", ownerTenantId: "tenant-a");
        harness.AddTable("alpha", "a1", schema: "b_schema", ownerTenantId: "tenant-a");
        harness.AddTable("alpha", "a2", schema: "a_schema", ownerTenantId: "tenant-a", kind: DiscoveryObjectKind.View);
        harness.AddTable("alpha", "a3", schema: "a_schema", ownerTenantId: "tenant-a", hidden: true);
        harness.AddTable("foreign", "f1", schema: "dbo", ownerTenantId: "tenant-b");

        var personResp = await harness.GetFacetsAsync(Person());
        personResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var json = personResp.ReadJson();
        json.GetProperty("totalCount").GetInt32().ShouldBe(3);

        var domains = json.GetProperty("domains").EnumerateArray().ToList();
        domains.Select(static d => d.GetProperty("domain").GetString()).ShouldBe(["alpha", "zeta"]);
        domains[0].GetProperty("count").GetInt32().ShouldBe(2);
        domains[0].GetProperty("schemas").EnumerateArray().Select(static s => s.GetProperty("schema").GetString())
            .ShouldBe(["a_schema", "b_schema"]);

        var app = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "sync-app", ExternalApiPermissions.CatalogRead);
        var appResp = await harness.GetFacetsAsync(app);
        appResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        appResp.ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(3);

        // CatalogReadAdmin sees hidden objects in its own tenant (a3), never foreign tenant (f1).
        var adminApp = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "sync-app", ExternalApiPermissions.CatalogReadAdmin);
        var adminResp = await harness.GetFacetsAsync(adminApp);
        adminResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        adminResp.ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(4);
    }

    [Fact]
    public async Task Facets_EndpointValidation_AccessStatusAndETagAndRateLimit()
    {
        var discoveryOpts = new CatalogDiscoveryOptions { Discovery = new CatalogDiscoverySettings { RequestsPerMinute = 3 } };
        await using var harness = ExternalCatalogTestHarness.Create(
            discoveryOptions: discoveryOpts,
            rateLimiter: new FakeDiscoveryRateLimiter());
        var granted = harness.AddTable("pg", "orders");
        harness.AddTable("pg", "invoices");
        harness.Consents.Consents.Add(new Consent
        {
            TableIdentifier = granted.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            TenantId = new TenantId("tenant-a"),
            ValidFrom = ExternalCatalogTestHarness.FixedNow.AddDays(-1),
            ValidTo = ExternalCatalogTestHarness.FixedNow.AddDays(30)
        });
        harness.Pdp.Decide = r => r.Object.Identifier == granted.Identifier
            ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(r.Object.Identifier, "Denied");

        var user = Person();
        var filtered = await harness.GetFacetsAsync(user, accessStatus: "granted");
        filtered.StatusCode.ShouldBe(StatusCodes.Status200OK);
        filtered.ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(1);

        var etag = filtered.Headers.ETag.ToString();
        var cached = await harness.GetFacetsAsync(user, accessStatus: "granted", ifNoneMatch: etag);
        cached.StatusCode.ShouldBe(StatusCodes.Status304NotModified);

        (await harness.GetFacetsAsync(user, accessStatus: "")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        var badStatus = await harness.GetFacetsAsync(user, accessStatus: "invalid<payload>");
        badStatus.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        badStatus.Body.ShouldNotContain("invalid<payload>");

        harness.RegisterExternalSystem("tenant-a", "sync-app", enabled: true);
        var app = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "sync-app", ExternalApiPermissions.CatalogRead);
        (await harness.GetFacetsAsync(app, accessStatus: "granted")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // 3rd person call is still within RPM=3.
        (await harness.GetFacetsAsync(user)).StatusCode.ShouldBe(StatusCodes.Status200OK);

        // 4th person call exceeds RPM=3 and maps DiscoveryStatusException to 429 with Retry-After.
        var limited = await harness.GetFacetsAsync(user);
        limited.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        limited.Headers.ContainsKey("Retry-After").ShouldBeTrue();
    }

    [Fact]
    public async Task Facets_ExceedingResponseLimit_Returns413FailClosed()
    {
        await using var harness = ExternalCatalogTestHarness.Create(maxResponseBytes: 64);
        harness.AddTable("very_long_domain_name_exceeding_cap", "t1", schema: "very_long_schema_name_exceeding_cap");

        var resp = await harness.GetFacetsAsync(Person());
        resp.StatusCode.ShouldBe(StatusCodes.Status413PayloadTooLarge);
    }
}
