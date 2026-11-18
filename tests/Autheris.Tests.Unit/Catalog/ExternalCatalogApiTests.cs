namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Domain.Tenancy;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

[Trait("Category", "Security")]
public sealed class ExternalCatalogApiTests
{
    [Fact]
    public async Task CatalogObjects_PersonTokenWithoutRole_Returns200_WithDiscoveryScopedItems()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var visible = harness.AddTable("pg", "customers", ownerTenantId: "tenant-a", hidden: false);
        harness.AddTable("pg", "secret_audit", ownerTenantId: "tenant-a", hidden: true);

        var person = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-PERSON", scopes: "Apps.Read");

        var list = await harness.ListObjectsAsync(person);
        list.StatusCode.Should().Be(StatusCodes.Status200OK);
        var items = list.ReadJson().GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("objectId").GetString().Should().Be(visible.Identifier.ToString());

        var byId = await harness.GetObjectByIdAsync(person, visible.Table.Id.ToString());
        byId.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task CatalogObjects_AppOnlyTokenWithoutScopeOrRole_StaysForbidden()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "some-app", enabled: true);
        harness.AddTable("pg", "customers", ownerTenantId: "tenant-a", hidden: false);

        var app = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "some-app");

        (await harness.ListObjectsAsync(app)).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CatalogObjects_PersonTokenWithOtherExternalScope_StaysForbidden()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var person = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-PERSON", scopes: "Autheris.Consent.Read");

        (await harness.ListObjectsAsync(person)).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task CatalogObjects_PolicyFailsForAnonymous()
    {
        await using var harness = ExternalCatalogTestHarness.Create();

        var anonymous = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity());
        (await harness.AuthorizeAsync(anonymous, GatewayPolicies.CatalogRead)).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task CatalogObjects_PlainPerson_SeesSameAsCatalogReadScoped()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.AddTable("pg", "customers", ownerTenantId: "tenant-a", hidden: false);
        harness.AddTable("pg", "secret_audit", ownerTenantId: "tenant-a", hidden: true);

        var plain = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-PERSON", scopes: "Apps.Read");
        var scoped = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-PERSON", scopes: ExternalApiPermissions.CatalogRead);

        static List<string?> Ids(ExecutedCatalogResponse r) =>
            r.ReadJson().GetProperty("items").EnumerateArray().Select(i => i.GetProperty("objectId").GetString()).Order().ToList();

        var plainIds = Ids(await harness.ListObjectsAsync(plain));
        plainIds.Should().BeEquivalentTo(Ids(await harness.ListObjectsAsync(scoped)));
        plainIds.Should().ContainSingle().Which.Should().Be("pg.dbo.customers");
        plainIds.Should().NotContain(id => id!.Contains("secret_audit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CatalogObjects_TenantlessPersonToken_Is403TenantUnresolved()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.AddTable("pg", "customers", ownerTenantId: "tenant-a", hidden: false);

        var tenantless = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(null, "S-1-5-21-PERSON", scopes: "Apps.Read");

        var response = await harness.ListObjectsAsync(tenantless);
        response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        response.Body.Should().Contain("TENANT_UNRESOLVED");
    }

    [Fact]
    public async Task CatalogObjectById_HiddenTable_NonAdminPerson_Is404()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var hidden = harness.AddTable("pg", "secret_audit", ownerTenantId: "tenant-a", hidden: true);

        var person = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-PERSON", scopes: "Apps.Read");

        (await harness.GetObjectByIdAsync(person, hidden.Table.Id.ToString())).StatusCode.Should().Be(StatusCodes.Status404NotFound);
        (await harness.GetObjectByIdAsync(person, hidden.Identifier.ToString())).StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task CatalogChanges_PersonToken_HiddenObjectReportedAsRemoved()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.AddTable("pg", "secret_audit", ownerTenantId: "tenant-a", hidden: true);
        await harness.CreateIndexer().IndexOnceAsync();

        var person = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", "S-1-5-21-PERSON", scopes: "Apps.Read");

        var feed = await harness.GetChangesAsync(person, since: 0);
        feed.StatusCode.Should().Be(StatusCodes.Status200OK);
        var changes = feed.ReadJson().GetProperty("changes").EnumerateArray().ToList();
        changes.Should().ContainSingle();
        changes[0].GetProperty("kind").GetString().Should().Be("removed");
    }

    [Fact]
    public async Task HiddenObjectsAndColumns_Excluded_ForCatalogRead()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "catalog-sync-app", enabled: true);

        var visible = harness.AddTable(
            "pg",
            "customers",
            ownerTenantId: "tenant-a",
            hidden: false,
            columns:
            [
                ("id", Sensitive: false, Hidden: false),
                ("ssn", Sensitive: true, Hidden: true)
            ]);

        var hiddenTable = harness.AddTable(
            "pg",
            "secret_audit",
            ownerTenantId: "tenant-a",
            hidden: true,
            columns:
            [
                ("id", Sensitive: false, Hidden: false),
                ("token", Sensitive: true, Hidden: true)
            ]);

        var appCaller = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "catalog-sync-app",
            ExternalApiPermissions.CatalogRead);

        var listResp = await harness.ListObjectsAsync(appCaller);
        listResp.StatusCode.Should().Be(StatusCodes.Status200OK);
        var items = listResp.ReadJson().GetProperty("items");
        items.GetArrayLength().Should().Be(1);
        items[0].GetProperty("objectId").GetString().Should().Be(visible.Identifier.ToString());
        items[0].GetProperty("hidden").GetBoolean().Should().BeFalse();

        var cols = items[0].GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["id"], cols);

        var hiddenById = await harness.GetObjectByIdAsync(appCaller, hiddenTable.Table.Id.ToString());
        hiddenById.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task Hidden_Included_WithFlags_ForAppOnlyExternalSystemWithCatalogReadAdmin()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "servicenow-sync", enabled: true);

        var visible = harness.AddTable(
            "pg",
            "orders",
            ownerTenantId: "tenant-a",
            hidden: false,
            columns:
            [
                ("id", Sensitive: false, Hidden: false),
                ("internal_note", Sensitive: true, Hidden: true)
            ]);
        var hiddenTable = harness.AddTable(
            "pg",
            "hr_comp",
            ownerTenantId: "tenant-a",
            hidden: true,
            columns:
            [
                ("employee_id", Sensitive: false, Hidden: false),
                ("salary", Sensitive: true, Hidden: true)
            ]);

        var adminApp = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "servicenow-sync",
            ExternalApiPermissions.CatalogReadAdmin);

        var listResp = await harness.ListObjectsAsync(adminApp);
        listResp.StatusCode.Should().Be(StatusCodes.Status200OK);
        var items = listResp.ReadJson().GetProperty("items").EnumerateArray().ToList();
        items.Should().HaveCount(2);

        var visibleDto = items.Single(x => x.GetProperty("objectId").GetString() == visible.Identifier.ToString());
        visibleDto.GetProperty("hidden").GetBoolean().Should().BeFalse();
        visibleDto.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "internal_note")
            .GetProperty("hidden").GetBoolean().Should().BeTrue();

        var hiddenDto = items.Single(x => x.GetProperty("objectId").GetString() == hiddenTable.Identifier.ToString());
        hiddenDto.GetProperty("hidden").GetBoolean().Should().BeTrue();
        hiddenDto.GetProperty("callerState").ValueKind.Should().Be(JsonValueKind.Null);

        // Unregistered or disabled external system with CatalogReadAdmin must NOT see hidden objects or columns.
        harness.RegisterExternalSystem("tenant-a", "disabled-sync", enabled: false);
        var disabledApp = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "disabled-sync",
            ExternalApiPermissions.CatalogReadAdmin);
        var disabledList = await harness.ListObjectsAsync(disabledApp);
        disabledList.StatusCode.Should().Be(StatusCodes.Status200OK);
        disabledList.ReadJson().GetProperty("items").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task DelegatedReadAdminScope_DoesNotRevealHidden()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.AddTable("pg", "public_sales", ownerTenantId: "tenant-a", hidden: false);
        var hidden = harness.AddTable("pg", "private_board", ownerTenantId: "tenant-a", hidden: true);

        var delegatedAdminScopeUser = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(
            "tenant-a",
            "S-1-5-21-DELEGATED-USER",
            scopes: $"{ExternalApiPermissions.CatalogReadAdmin} {ExternalApiPermissions.CatalogRead}");

        var listResp = await harness.ListObjectsAsync(delegatedAdminScopeUser);
        listResp.StatusCode.Should().Be(StatusCodes.Status200OK);
        var ids = listResp.ReadJson().GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("objectId").GetString())
            .ToList();
        ids.Should().HaveCount(1);
        ids.Should().NotContain(hidden.Identifier.ToString());

        var byIdResp = await harness.GetObjectByIdAsync(delegatedAdminScopeUser, hidden.Table.Id.ToString());
        byIdResp.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task DelegatedToken_DataOwner_SeesOwnHiddenObjectOnly()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var ownedHidden = harness.AddTable(
            "pg",
            "owned_hidden",
            ownerTenantId: "tenant-a",
            hidden: true,
            columns:
            [
                ("id", Sensitive: false, Hidden: false),
                ("secret_col", Sensitive: true, Hidden: true)
            ]);
        var otherHidden = harness.AddTable(
            "pg",
            "other_hidden",
            ownerTenantId: "tenant-a",
            hidden: true);

        const string ownerSid = "S-1-5-21-OWNER-A";
        const string otherSid = "S-1-5-21-OWNER-B";
        harness.Owners.AssignOwner(ownedHidden.Identifier, ownerSid, "Owner A");
        harness.Owners.AssignOwner(otherHidden.Identifier, otherSid, "Owner B");

        var ownerCaller = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(
            "tenant-a",
            ownerSid,
            scopes: ExternalApiPermissions.CatalogRead);

        var listResp = await harness.ListObjectsAsync(ownerCaller);
        listResp.StatusCode.Should().Be(StatusCodes.Status200OK);
        var items = listResp.ReadJson().GetProperty("items").EnumerateArray().ToList();
        items.Should().ContainSingle();
        items[0].GetProperty("objectId").GetString().Should().Be(ownedHidden.Identifier.ToString());
        items[0].GetProperty("hidden").GetBoolean().Should().BeTrue();
        items[0].GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .Should().Contain("secret_col");

        var otherResp = await harness.GetObjectByIdAsync(ownerCaller, otherHidden.Table.Id.ToString());
        otherResp.StatusCode.Should().Be(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task IfNoneMatch_Returns304()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "catalog-sync", enabled: true);
        var table = harness.AddTable("pg", "invoices", ownerTenantId: "tenant-a", hidden: false);
        var indexer = harness.CreateIndexer();
        await indexer.IndexOnceAsync();

        var appCaller = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "catalog-sync",
            ExternalApiPermissions.CatalogRead);

        // 1. List endpoint ETag -> 304 on match, 200 on mismatch
        var listFirst = await harness.ListObjectsAsync(appCaller);
        listFirst.StatusCode.Should().Be(StatusCodes.Status200OK);
        var listEtag = listFirst.Headers.ETag.ToString();
        Assert.StartsWith("\"", listEtag);
        Assert.EndsWith("\"", listEtag);

        var listNotModified = await harness.ListObjectsAsync(appCaller, ifNoneMatch: listEtag);
        listNotModified.StatusCode.Should().Be(StatusCodes.Status304NotModified);

        var listStaleTag = await harness.ListObjectsAsync(appCaller, ifNoneMatch: "\"stale-etag\"");
        listStaleTag.StatusCode.Should().Be(StatusCodes.Status200OK);

        // 2. Get by id ETag -> 304 on match; caller-specific ETag differs per user
        var byIdFirst = await harness.GetObjectByIdAsync(appCaller, table.Table.Id.ToString());
        byIdFirst.StatusCode.Should().Be(StatusCodes.Status200OK);
        var byIdEtag = byIdFirst.Headers.ETag.ToString();

        var byIdNotModified = await harness.GetObjectByIdAsync(appCaller, table.Table.Id.ToString(), ifNoneMatch: byIdEtag);
        byIdNotModified.StatusCode.Should().Be(StatusCodes.Status304NotModified);

        var userCaller = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(
            "tenant-a",
            "S-1-5-21-USER-ETAG",
            scopes: ExternalApiPermissions.CatalogRead);
        var userById = await harness.GetObjectByIdAsync(userCaller, table.Table.Id.ToString(), ifNoneMatch: byIdEtag);
        userById.StatusCode.Should().Be(StatusCodes.Status200OK);
        userById.Headers.ETag.ToString().Should().NotBe(byIdEtag);

        // 3. Changes endpoint ETag -> 304 on match
        var changesFirst = await harness.GetChangesAsync(appCaller, since: 0);
        changesFirst.StatusCode.Should().Be(StatusCodes.Status200OK);
        var changesEtag = changesFirst.Headers.ETag.ToString();

        var changesNotModified = await harness.GetChangesAsync(appCaller, since: 0, ifNoneMatch: changesEtag);
        changesNotModified.StatusCode.Should().Be(StatusCodes.Status304NotModified);
    }

    [Fact]
    public async Task ChangesFeed_HidingObject_EmitsRemoved()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "catalog-sync", enabled: true);
        var table = harness.AddTable("pg", "ledger", ownerTenantId: "tenant-a", hidden: false);
        var indexer = harness.CreateIndexer();

        await indexer.IndexOnceAsync();

        var appCaller = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "catalog-sync",
            ExternalApiPermissions.CatalogRead);

        var initialFeed = await harness.GetChangesAsync(appCaller, since: 0);
        initialFeed.StatusCode.Should().Be(StatusCodes.Status200OK);
        var initialJson = initialFeed.ReadJson();
        var initialItems = initialJson.GetProperty("changes").EnumerateArray().ToList();
        initialItems.Should().ContainSingle();
        initialItems[0].GetProperty("kind").GetString().Should().Be("upsert");
        var seqAfterCreate = initialJson.GetProperty("nextSince").GetInt64();
        seqAfterCreate.Should().Be(1);

        // Flip DiscoveryHidden = true without changing physical table -> must emit "removed"
        harness.Tables.Tables.Clear();
        harness.Tables.Tables.Add(new TableMetadata
        {
            Identifier = table.Identifier,
            OwnerTenantId = table.OwnerTenantId,
            CatalogTags = table.CatalogTags,
            Columns = table.Columns,
            Table = new Table
            {
                Id = table.Table.Id,
                SourceName = table.Table.SourceName,
                SchemaName = table.Table.SchemaName,
                TableName = table.Table.TableName,
                DisplayName = table.Table.DisplayName,
                Description = table.Table.Description,
                DataSourceType = table.Table.DataSourceType,
                IsActive = true,
                DiscoveryHidden = true,
                ObjectKind = table.Table.ObjectKind,
                Sensitivity = table.Table.Sensitivity
            }
        });
        harness.Clock.Advance(TimeSpan.FromMinutes(1));
        await indexer.IndexOnceAsync();

        var deltaFeed = await harness.GetChangesAsync(appCaller, since: seqAfterCreate);
        deltaFeed.StatusCode.Should().Be(StatusCodes.Status200OK);
        var deltaJson = deltaFeed.ReadJson();
        var deltaItems = deltaJson.GetProperty("changes").EnumerateArray().ToList();
        deltaItems.Should().ContainSingle();
        deltaItems[0].GetProperty("kind").GetString().Should().Be("removed");
        deltaItems[0].GetProperty("objectId").GetString().Should().Be(table.Identifier.ToString());
        deltaJson.GetProperty("nextSince").GetInt64().Should().BeGreaterThan(seqAfterCreate);

        // No-op re-index should emit nothing new
        await indexer.IndexOnceAsync();
        var afterNoOp = await harness.GetChangesAsync(appCaller, since: deltaJson.GetProperty("nextSince").GetInt64());
        afterNoOp.ReadJson().GetProperty("changes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task CrossTenant_ObjectId_Returns404()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "sync-a", enabled: true);

        var tenantATable = harness.AddTable("pg", "alpha_orders", ownerTenantId: "tenant-a", hidden: false);
        var tenantBTable = harness.AddTable("pg", "beta_secrets", ownerTenantId: "tenant-b", hidden: false);

        var tenantACaller = ExternalCatalogTestHarness.BuildAppPrincipal(
            "tenant-a",
            "sync-a",
            ExternalApiPermissions.CatalogReadAdmin);

        var byIdB = await harness.GetObjectByIdAsync(tenantACaller, tenantBTable.Table.Id.ToString());
        byIdB.StatusCode.Should().Be(StatusCodes.Status404NotFound);

        var listA = await harness.ListObjectsAsync(tenantACaller);
        listA.StatusCode.Should().Be(StatusCodes.Status200OK);
        var ids = listA.ReadJson().GetProperty("items").EnumerateArray()
            .Select(x => x.GetProperty("objectId").GetString())
            .ToList();
        ids.Should().Contain(tenantATable.Identifier.ToString());
        ids.Should().NotContain(tenantBTable.Identifier.ToString());

        // Caller missing tenant_id must fail closed with 403
        var missingTenantCaller = ExternalCatalogTestHarness.BuildAppPrincipal(
            null,
            "sync-a",
            ExternalApiPermissions.CatalogRead);
        var missingTenantResp = await harness.ListObjectsAsync(missingTenantCaller);
        missingTenantResp.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task ListObjects_EnforcesFilters_Pagination_AndCallerMaskingState()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var tableOne = harness.AddTable(
            "finance",
            "payroll_entries",
            ownerTenantId: "tenant-a",
            sensitivity: "HIGH",
            columns:
            [
                ("id", Sensitive: false, Hidden: false),
                ("iban", Sensitive: true, Hidden: false)
            ]);
        var tableTwo = harness.AddTable(
            "finance",
            "cost_centers",
            ownerTenantId: "tenant-a",
            sensitivity: "LOW",
            columns: [("id", Sensitive: false, Hidden: false)]);

        harness.Pdp.Decide = req =>
        {
            if (req.Object.Table.Id == tableOne.Table.Id)
            {
                return TableAccessDecision.Allowed(
                    req.Object.Identifier,
                    new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["iban"] = ColumnAccessLevel.Mask
                    });
            }

            return TableAccessDecision.Allowed(req.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        };

        var userCaller = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(
            "tenant-a",
            "S-1-5-21-USER-1",
            scopes: ExternalApiPermissions.CatalogRead);
        var page1 = await harness.ListObjectsAsync(userCaller, pageSize: 1);
        page1.StatusCode.Should().Be(StatusCodes.Status200OK);
        var p1Json = page1.ReadJson();
        p1Json.GetProperty("items").GetArrayLength().Should().Be(1);
        var cursor = p1Json.GetProperty("nextCursor").GetString();
        cursor.Should().NotBeNullOrWhiteSpace();

        var page2 = await harness.ListObjectsAsync(userCaller, cursor: cursor, pageSize: 1);
        page2.StatusCode.Should().Be(StatusCodes.Status200OK);
        page2.ReadJson().GetProperty("items").GetArrayLength().Should().Be(1);

        var piiFiltered = await harness.ListObjectsAsync(userCaller, pii: true, sensitivity: "HIGH");
        piiFiltered.StatusCode.Should().Be(StatusCodes.Status200OK);
        var piiItem = piiFiltered.ReadJson().GetProperty("items").EnumerateArray().Single();
        piiItem.GetProperty("objectId").GetString().Should().Be(tableOne.Identifier.ToString());
        piiItem.GetProperty("columns").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "iban")
            .GetProperty("accessState").GetString().Should().Be("masked");

        var badCursor = await harness.ListObjectsAsync(userCaller, cursor: "not-valid-base64!");
        badCursor.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }
}
