namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>Issue #857: <c>GET /api/v1/catalog/objects?q=</c> filters before totalCount and the cursor, per caller.</summary>
[Trait("Category", "Security")]
public sealed class ExternalCatalogObjectsQueryTests
{
    private const string UserSid = "S-1-5-21-QUERY-USER";

    private static ClaimsPrincipal User() =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", UserSid, scopes: "Apps.Read");

    private static void Grant(ExternalCatalogTestHarness harness, TableMetadata table) =>
        harness.Consents.Consents.Add(new Consent
        {
            TableIdentifier = table.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            TenantId = new TenantId("tenant-a"),
            ValidFrom = ExternalCatalogTestHarness.FixedNow.AddYears(-1),
            ValidTo = ExternalCatalogTestHarness.FixedNow.AddYears(1)
        });

    private static List<string?> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("objectId").GetString()).ToList();

    [Fact]
    public async Task CatalogObjects_Query_MatchesVisibleColumnName()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.AddTable("pg", "orders", columns: [("order_total", false, false)]);
        harness.AddTable("pg", "customers", columns: [("email_address", false, false)]);

        var page = (await harness.ListObjectsAsync(User(), q: "order_total")).ReadJson();

        Ids(page).ShouldBe(["pg.dbo.orders"]);
        page.GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task CatalogObjects_Query_NeverMatchesHiddenText()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.AddTable("pg", "patients", columns: [("id", false, false), ("zebra_secret_col", true, true)]);
        harness.AddTable("pg", "secret_table_zebra", hidden: true);

        var page = (await harness.ListObjectsAsync(User(), q: "zebra")).ReadJson();

        Ids(page).ShouldBeEmpty();
        page.GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task CatalogObjects_Query_NeverMatchesNoAccessColumnName()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var table = harness.AddTable("pg", "accounts", columns: [("id", false, false), ("iban_value", false, false)]);
        Grant(harness, table);
        harness.Pdp.Decide = r => TableAccessDecision.Allowed(
            r.Object.Identifier,
            new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["iban_value"] = ColumnAccessLevel.Deny },
            hasUnconstrainedColumnAllow: false);

        (await harness.ListObjectsAsync(User(), q: "iban_value")).ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(0);
        Ids((await harness.ListObjectsAsync(User(), q: "accounts")).ReadJson()).ShouldBe(["pg.dbo.accounts"]);
    }

    [Fact]
    public async Task CatalogObjects_Query_CombinesWithAccessStatus()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var granted = harness.AddTable("pg", "sales_a");
        harness.AddTable("pg", "sales_b");
        var otherGranted = harness.AddTable("pg", "hr_a");
        Grant(harness, granted);
        Grant(harness, otherGranted);
        var ids = new[] { granted.Identifier, otherGranted.Identifier }.ToHashSet();
        harness.Pdp.Decide = r => ids.Contains(r.Object.Identifier)
            ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(r.Object.Identifier, "Denied");

        var page = (await harness.ListObjectsAsync(User(), q: "sales", accessStatus: "granted")).ReadJson();

        Ids(page).ShouldBe(["pg.dbo.sales_a"]);
        page.GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task CatalogObjects_Query_CountsAfterFilter()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        for (var i = 0; i < 5; i++)
        {
            harness.AddTable("pg", $"match_{i}");
        }

        harness.AddTable("pg", "other");

        var page = (await harness.ListObjectsAsync(User(), q: "match_", pageSize: 2)).ReadJson();

        page.GetProperty("totalCount").GetInt32().ShouldBe(5);
        Ids(page).Count.ShouldBe(2);
        page.GetProperty("nextCursor").GetString().ShouldNotBeNull();
    }

    [Fact]
    public async Task CatalogObjects_Query_CursorBoundToQuery()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        for (var i = 0; i < 4; i++)
        {
            harness.AddTable("pg", $"match_{i}");
        }

        var first = (await harness.ListObjectsAsync(User(), q: "match_", pageSize: 2)).ReadJson();
        var cursor = first.GetProperty("nextCursor").GetString();

        var same = await harness.ListObjectsAsync(User(), q: " match_ ", pageSize: 2, cursor: cursor);
        same.StatusCode.ShouldBe(StatusCodes.Status200OK);
        Ids(same.ReadJson()).ShouldBe(["pg.dbo.match_2", "pg.dbo.match_3"]);

        (await harness.ListObjectsAsync(User(), q: "other", pageSize: 2, cursor: cursor)).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await harness.ListObjectsAsync(User(), pageSize: 2, cursor: cursor)).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await harness.ListObjectsAsync(User(), q: "match_", accessStatus: "granted", pageSize: 2, cursor: cursor)).StatusCode
            .ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task CatalogObjects_Query_TooLong_Is400_AndAppOnlyCallerIsRejected()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "app", enabled: true);

        (await harness.ListObjectsAsync(User(), q: new string('a', 257))).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        var app = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "app", ExternalApiPermissions.CatalogRead);
        (await harness.ListObjectsAsync(app, q: "x")).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }
}
