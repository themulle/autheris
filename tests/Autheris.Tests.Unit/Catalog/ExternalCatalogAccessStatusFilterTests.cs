namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>Issue #798: <c>GET /api/v1/catalog/objects?accessStatus=</c> filters before totalCount and the cursor.</summary>
public sealed class ExternalCatalogAccessStatusFilterTests
{
    private const string UserSid = "S-1-5-21-FILTER-USER";

    private static ExternalCatalogTestHarness Create(int tables, params int[] granted)
    {
        var harness = ExternalCatalogTestHarness.Create();
        var all = Enumerable.Range(0, tables).Select(i => harness.AddTable("pg", $"t{i:00}", ownerTenantId: "tenant-a")).ToList();
        var grantedIds = granted.Select(i => all[i].Identifier).ToHashSet();
        foreach (var id in grantedIds)
        {
            harness.Consents.Consents.Add(new Consent
            {
                TableIdentifier = id,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = new Sid(UserSid),
                TenantId = new TenantId("tenant-a"),
                ValidFrom = DateTimeOffset.UtcNow.AddYears(-1),
                ValidTo = DateTimeOffset.UtcNow.AddYears(1)
            });
        }

        harness.Pdp.Decide = r => grantedIds.Contains(r.Object.Identifier)
            ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(r.Object.Identifier, "Denied");
        return harness;
    }

    private static System.Security.Claims.ClaimsPrincipal User() =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", UserSid, scopes: ExternalApiPermissions.CatalogRead);

    [Fact]
    public async Task Granted_TotalCountAndCursorReferToTheFilteredSet()
    {
        await using var harness = Create(10, 2, 5, 9);

        var first = await harness.ListObjectsAsync(User(), pageSize: 2, accessStatus: "granted");

        first.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var page = first.ReadJson();
        page.GetProperty("totalCount").GetInt32().ShouldBe(3);
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("objectId").GetString())
            .ShouldBe(["pg.dbo.t02", "pg.dbo.t05"]);
        page.GetProperty("items").EnumerateArray().ShouldAllBe(i => i.GetProperty("callerState").GetProperty("access").GetString() == "granted");
        var cursor = page.GetProperty("nextCursor").GetString();
        cursor.ShouldNotBeNull();

        var second = (await harness.ListObjectsAsync(User(), pageSize: 2, cursor: cursor, accessStatus: "GRANTED")).ReadJson();
        second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("objectId").GetString()).ShouldBe(["pg.dbo.t09"]);
        (!second.TryGetProperty("nextCursor", out var next) || next.ValueKind == System.Text.Json.JsonValueKind.Null).ShouldBeTrue();
    }

    [Fact]
    public async Task Granted_OnALargeCatalog_DecidesEachTableOnceAcrossTheInternalPages()
    {
        // 450 granted tables: the reader pulls three 200-row discovery pages; the caller view is built once, not once per page.
        await using var harness = Create(450, Enumerable.Range(0, 450).ToArray());
        var decide = harness.Pdp.Decide;
        var discoverDecisions = 0;
        harness.Pdp.Decide = r =>
        {
            if (r.Operation == Autheris.Application.Policy.Pdp.AccessOperation.Discover)
            {
                discoverDecisions++;
            }

            return decide(r);
        };

        var page = (await harness.ListObjectsAsync(User(), pageSize: 200, accessStatus: "granted")).ReadJson();

        page.GetProperty("totalCount").GetInt32().ShouldBe(450);
        discoverDecisions.ShouldBeLessThanOrEqualTo(450 + 450);
    }

    [Fact]
    public async Task None_ReturnsTheRequestableObjects()
    {
        await using var harness = Create(4, 0);

        var page = (await harness.ListObjectsAsync(User(), accessStatus: "none")).ReadJson();

        page.GetProperty("totalCount").GetInt32().ShouldBe(3);
        page.GetProperty("items").EnumerateArray().ShouldAllBe(i => i.GetProperty("callerState").GetProperty("access").GetString() == "none");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task InvalidValue_IsA400_WithoutEchoingTheValue()
    {
        await using var harness = Create(2, 0);

        var response = await harness.ListObjectsAsync(User(), accessStatus: "everything<script>");

        response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        response.Body.ShouldContain("granted, requested, expired, none");
        response.Body.ShouldNotContain("everything");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task EmptyValue_IsA400_NotNoFilter()
    {
        await using var harness = Create(2, 0);

        var response = await harness.ListObjectsAsync(User(), accessStatus: "");

        response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AppOnlyCaller_IsRejected()
    {
        await using var harness = Create(2, 0);
        harness.RegisterExternalSystem("tenant-a", "catalog-sync-app", enabled: true);
        var app = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "catalog-sync-app", ExternalApiPermissions.CatalogRead);

        var response = await harness.ListObjectsAsync(app, accessStatus: "granted");

        response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }
}
