namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.External;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DiscoveryCountSecurityTests
{
    private const string SigningKey = "01234567890123456789012345678901-count-security";
    private const string UserSid = "S-1-5-21-COUNT-USER";

    private static ClaimsPrincipal Person() =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", UserSid, scopes: ExternalApiPermissions.CatalogRead);

    [Fact]
    public async Task TotalCount_ExcludesHiddenAndForeignTenantObjects()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        harness.AddTable("pg", "visible_1", ownerTenantId: "tenant-a", hidden: false);
        harness.AddTable("pg", "hidden_1", ownerTenantId: "tenant-a", hidden: true);
        harness.AddTable("pg", "foreign_1", ownerTenantId: "tenant-b", hidden: false);

        var resp = await harness.ListObjectsAsync(Person());
        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        resp.ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task TotalCount_Q_ExcludesObjectsMatchingOnlyHiddenText()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        harness.AddTable("pg", "visible_orders", columns: [("id", false, false), ("order_amount", false, false)]);
        var deniedTable = harness.AddTable("pg", "patients", columns: [("id", false, false), ("secret_order_token", false, false)]);
        harness.AddTable("pg", "hidden_col_tbl", columns: [("id", false, false), ("hidden_order_col", false, true)]);
        harness.AddTable("pg", "hidden_order_tbl", hidden: true);

        harness.Pdp.Decide = r => r.Object.Identifier == deniedTable.Identifier
            ? TableAccessDecision.Allowed(
                r.Object.Identifier,
                new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["secret_order_token"] = ColumnAccessLevel.Deny },
                hasUnconstrainedColumnAllow: false)
            : TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var resp = await harness.ListObjectsAsync(Person(), q: "order");
        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var json = resp.ReadJson();
        json.GetProperty("totalCount").GetInt32().ShouldBe(1);
        json.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()).ShouldBe(["pg.dbo.visible_orders"]);
    }

    [Fact]
    public async Task TotalCount_Pii_IgnoresNoAccessColumns()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        var clearPii = harness.AddTable("pg", "t_clear_pii", columns: [("id", false, false), ("email", true, false)]);
        var deniedPii = harness.AddTable("pg", "t_denied_pii", columns: [("id", false, false), ("ssn", true, false)]);
        harness.Pdp.Decide = r => r.Object.Identifier == deniedPii.Identifier
            ? TableAccessDecision.Allowed(
                r.Object.Identifier,
                new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["ssn"] = ColumnAccessLevel.Deny },
                hasUnconstrainedColumnAllow: false)
            : TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);

        var resp = await harness.ListObjectsAsync(Person(), pii: true);
        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var json = resp.ReadJson();
        json.GetProperty("totalCount").GetInt32().ShouldBe(1);
        json.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()).ShouldBe([clearPii.Identifier.ToString()]);
    }

    [Fact]
    public async Task TotalCount_Owner_IgnoresHiddenOwnership()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        var hiddenOwnedByOther = harness.AddTable("pg", "secret_hr", ownerTenantId: "tenant-a", hidden: true);
        harness.Owners.AssignOwner(hiddenOwnedByOther.Identifier, "S-OTHER-OWNER", "TargetOwner", "target@example.test");

        var visibleWithInactiveOwner = harness.AddTable("pg", "public_hr", ownerTenantId: "tenant-a", hidden: false);
        harness.Owners.AssignOwner(visibleWithInactiveOwner.Identifier, "S-OTHER-OWNER", "TargetOwner", "target@example.test", isActive: false);

        var resp = await harness.ListObjectsAsync(Person(), owner: "TargetOwner");
        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        resp.ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(0);
        resp.ReadJson().GetProperty("items").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task Facets_SumEqualsListTotalForSameAccessStatus()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        var granted = harness.AddTable("sales", "orders");
        harness.AddTable("sales", "invoices");
        harness.AddTable("hr", "employees");
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
        foreach (var status in new string?[] { null, "granted", "none" })
        {
            var facets = (await harness.GetFacetsAsync(user, accessStatus: status)).ReadJson();
            var list = (await harness.ListObjectsAsync(user, accessStatus: status)).ReadJson();

            var domainSum = facets.GetProperty("domains").EnumerateArray().Sum(static d => d.GetProperty("count").GetInt32());
            var schemaSum = facets.GetProperty("domains").EnumerateArray()
                .SelectMany(static d => d.GetProperty("schemas").EnumerateArray())
                .Sum(static s => s.GetProperty("count").GetInt32());

            facets.GetProperty("totalCount").GetInt32().ShouldBe(list.GetProperty("totalCount").GetInt32());
            domainSum.ShouldBe(facets.GetProperty("totalCount").GetInt32());
            schemaSum.ShouldBe(facets.GetProperty("totalCount").GetInt32());
        }
    }

    [Fact]
    public async Task Facets_NeverNameNoAccessColumnsOrHiddenObjects()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        harness.AddTable("secret_hidden_domain", "hidden_tbl", schema: "secret_hidden_schema", hidden: true);
        var visible = harness.AddTable("visible_domain", "t1", schema: "visible_schema", columns: [("id", false, false), ("denied_secret_col", true, false)]);
        harness.Pdp.Decide = r => TableAccessDecision.Allowed(
            r.Object.Identifier,
            new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["denied_secret_col"] = ColumnAccessLevel.Deny },
            hasUnconstrainedColumnAllow: false);

        var resp = await harness.GetFacetsAsync(Person());
        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        resp.Body.ShouldNotContain("secret_hidden_domain");
        resp.Body.ShouldNotContain("secret_hidden_schema");
        resp.Body.ShouldNotContain("denied_secret_col");
        resp.ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(1);
        visible.ShouldNotBeNull();
    }

    [Fact]
    public async Task CountPassAndPageFill_ShareOnePredicate()
    {
        var repo = new FakeDiscoveryTableRepository();
        var t1 = DiscoveryTestBuilder.MakeTable("pg", "t1", columns: [("id", false, false), ("email", true, false)]);
        var t2 = DiscoveryTestBuilder.MakeTable("pg", "t2", columns: [("id", false, false), ("ssn", true, false)]);
        var t3 = DiscoveryTestBuilder.MakeTable("pg", "t3", columns: [("id", false, false), ("phone", true, false)]);
        repo.Tables.AddRange([t1, t2, t3]);

        var pdp = new FakeAccessDecisionPoint(r => r.Object.Identifier == t2.Identifier
            ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["ssn"] = ColumnAccessLevel.Deny }, hasUnconstrainedColumnAllow: false)
            : TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var service = DiscoveryTestBuilder.CreateService(tables: repo, pdp: pdp);
        var caller = DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(UserSid, "tenant-a"));

        var calls = 0;
        bool Predicate(DiscoveryObject obj)
        {
            calls++;
            return DiscoveryColumnVisibility.VisibleColumns(obj).Any(static c => c.Pii);
        }

        // Page 1 counts and collects page items in one pass without evaluating the predicate twice per candidate.
        var p1 = await service.ListKeysetAsync(new DiscoveryKeysetQuery(First: 1, ProjectedPredicate: Predicate, CountTotal: true), caller);
        p1.TotalCount.ShouldBe(2);
        p1.Items.Select(static i => i.Object.Name).ShouldBe(["t1"]);
        calls.ShouldBe(3);
    }

    [Fact]
    public async Task StaleCursor_HiddenKey_ContinuesWithoutDistinguishing()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: SigningKey);
        var a = harness.AddTable("pg", "a_tbl");
        var b = harness.AddTable("pg", "b_tbl");
        var c = harness.AddTable("pg", "c_tbl");
        var d = harness.AddTable("pg", "d_tbl");

        var user = Person();
        var p1 = (await harness.ListObjectsAsync(user, pageSize: 2)).ReadJson();
        p1.GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()).ShouldBe([a.Identifier.ToString(), b.Identifier.ToString()]);
        var cursorAfterB = p1.GetProperty("nextCursor").GetString()!;

        // Now hide b_tbl and delete b_tbl in a second check; neither errors nor distinguishes hidden vs deleted.
        var bIdx = harness.Tables.Tables.IndexOf(b);
        harness.Tables.Tables[bIdx] = b with
        {
            Table = new Table
            {
                Id = b.Table.Id,
                SourceName = b.Table.SourceName,
                SchemaName = b.Table.SchemaName,
                TableName = b.Table.TableName,
                IsActive = true,
                DiscoveryHidden = true
            }
        };

        var hiddenResp = await harness.ListObjectsAsync(user, pageSize: 2, cursor: cursorAfterB);
        hiddenResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var hiddenIds = hiddenResp.ReadJson().GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()!).ToList();
        hiddenIds.ShouldBe([c.Identifier.ToString(), d.Identifier.ToString()]);

        harness.Tables.Tables.RemoveAt(bIdx);
        var deletedResp = await harness.ListObjectsAsync(user, pageSize: 2, cursor: cursorAfterB);
        deletedResp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var deletedIds = deletedResp.ReadJson().GetProperty("items").EnumerateArray().Select(static i => i.GetProperty("objectId").GetString()!).ToList();
        deletedIds.ShouldBe(hiddenIds);
    }
}
