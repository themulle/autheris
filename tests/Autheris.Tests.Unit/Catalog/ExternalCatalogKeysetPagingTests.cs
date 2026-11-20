namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

public sealed class ExternalCatalogKeysetPagingTests
{
    private const string SigningKey = "01234567890123456789012345678901-keyset-paging";
    private const string UserSid = "S-1-5-21-PAGING-USER";

    private static ClaimsPrincipal Person() =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", UserSid, scopes: ExternalApiPermissions.CatalogRead);

    [Fact]
    public async Task Paging_Slice2EqualsFullOrderSlice2()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        var names = new[] { "zebra", "Beta", "beta", "alpha", "gamma", "delta" };
        foreach (var n in names)
        {
            harness.AddTable("pg", n);
        }

        var user = Person();
        var full = (await harness.ListObjectsAsync(user, pageSize: 100)).ReadJson()
            .GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()!).ToList();

        var p1 = (await harness.ListObjectsAsync(user, pageSize: 2)).ReadJson();
        var p1Ids = p1.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()!).ToList();
        var c1 = p1.GetProperty("nextCursor").GetString()!;

        var p2 = (await harness.ListObjectsAsync(user, pageSize: 2, cursor: c1)).ReadJson();
        var p2Ids = p2.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()!).ToList();
        var c2 = p2.GetProperty("nextCursor").GetString()!;

        var p3 = (await harness.ListObjectsAsync(user, pageSize: 2, cursor: c2)).ReadJson();
        var p3Ids = p3.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()!).ToList();

        p1Ids.ShouldBe(full.Skip(0).Take(2).ToList());
        p2Ids.ShouldBe(full.Skip(2).Take(2).ToList());
        p3Ids.ShouldBe(full.Skip(4).Take(2).ToList());
        (!p3.TryGetProperty("nextCursor", out var next) || next.ValueKind == JsonValueKind.Null).ShouldBeTrue();
    }

    [Fact]
    public async Task PiiTrue_CountsAndPagesVisibleColumnsOnly()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        var visiblePii = harness.AddTable("pg", "t1_visible_pii", columns: [("id", false, false), ("email", true, false)]);
        var deniedPii = harness.AddTable("pg", "t2_denied_pii", columns: [("id", false, false), ("ssn", true, false)]);
        var hiddenColPii = harness.AddTable("pg", "t3_hidden_pii", columns: [("id", false, false), ("secret_email", true, true)]);
        var visiblePii2 = harness.AddTable("pg", "t4_visible_pii", columns: [("id", false, false), ("phone", true, false)]);

        harness.Pdp.Decide = r =>
        {
            if (r.Object.Identifier == deniedPii.Identifier)
            {
                return TableAccessDecision.Allowed(
                    r.Object.Identifier,
                    new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["ssn"] = ColumnAccessLevel.Deny },
                    hasUnconstrainedColumnAllow: false);
            }

            return TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        };

        var user = Person();
        var p1 = (await harness.ListObjectsAsync(user, pii: true, pageSize: 1)).ReadJson();
        p1.GetProperty("totalCount").GetInt32().ShouldBe(2);
        p1.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()).ShouldBe([visiblePii.Identifier.ToString()]);

        var p2 = (await harness.ListObjectsAsync(user, pii: true, pageSize: 1, cursor: p1.GetProperty("nextCursor").GetString())).ReadJson();
        p2.GetProperty("totalCount").GetInt32().ShouldBe(2);
        p2.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()).ShouldBe([visiblePii2.Identifier.ToString()]);
        hiddenColPii.ShouldNotBeNull();
    }

    [Fact]
    public async Task OwnerFilter_PagesAcrossWindows()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        for (var i = 0; i < 8; i++)
        {
            var tbl = harness.AddTable("pg", $"tbl_{i:00}");
            if (i is 1 or 5 or 7)
            {
                harness.Owners.AssignOwner(tbl.Identifier, "S-ALICE", "Alice", "alice@example.test");
            }
            else
            {
                harness.Owners.AssignOwner(tbl.Identifier, "S-BOB", "Bob", "bob@example.test");
            }
        }

        var user = Person();
        var p1 = (await harness.ListObjectsAsync(user, owner: "alice@example.test", pageSize: 2)).ReadJson();
        p1.GetProperty("totalCount").GetInt32().ShouldBe(3);
        p1.GetProperty("items").EnumerateArray().Select(static x => x.GetProperty("objectId").GetString())
            .ShouldBe(["pg.dbo.tbl_01", "pg.dbo.tbl_05"]);

        var p2 = (await harness.ListObjectsAsync(user, owner: "alice@example.test", pageSize: 2, cursor: p1.GetProperty("nextCursor").GetString())).ReadJson();
        p2.GetProperty("totalCount").GetInt32().ShouldBe(3);
        p2.GetProperty("items").EnumerateArray().Select(static x => x.GetProperty("objectId").GetString())
            .ShouldBe(["pg.dbo.tbl_07"]);
    }

    [Fact]
    public async Task Page_ProjectsAtMostOneWindow()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        for (var i = 0; i < 120; i++)
        {
            harness.AddTable("pg", $"t_{i:000}", kind: i % 2 == 0 ? DiscoveryObjectKind.Table : DiscoveryObjectKind.View);
        }

        var discoverCalls = 0;
        var baseDecide = harness.Pdp.Decide;
        harness.Pdp.Decide = req =>
        {
            if (req.Operation == AccessOperation.Discover)
            {
                discoverCalls++;
            }

            return baseDecide(req);
        };

        const int pageSize = 10;
        var resp = await harness.ListObjectsAsync(Person(), kind: "view", pageSize: pageSize);
        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var json = resp.ReadJson();
        json.GetProperty("totalCount").GetInt32().ShouldBe(60);
        json.GetProperty("items").GetArrayLength().ShouldBe(pageSize);

        // Pre-projection filters project at most pageSize + 1 objects (one window), never the whole 120-object catalog.
        discoverCalls.ShouldBeLessThanOrEqualTo(pageSize + 1);
    }
}
