namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.External;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>One rule for the person view of the external catalog: a column the caller cannot see is never listed, described or counted.</summary>
[Trait("Category", "Security")]
public sealed class ExternalCatalogDeniedColumnTests
{
    private const string UserSid = "S-1-5-21-DENIED-COLUMN";
    private const string DeniedName = "diagnosis_code";
    private const string DeniedDescription = "zebracornmarker internal classification";

    private static TableMetadata AddTable(ExternalCatalogTestHarness harness, string name, bool hidden = false)
    {
        var table = harness.AddTable(
            "pg",
            name,
            ownerTenantId: "tenant-a",
            hidden: hidden,
            columns:
            [
                ("id", Sensitive: false, Hidden: false),
                ("email", Sensitive: true, Hidden: false),
                (DeniedName, Sensitive: false, Hidden: false),
                ("internal_flag", Sensitive: false, Hidden: true)
            ]);
        var index = harness.Tables.Tables.IndexOf(table);
        table = table with
        {
            Columns = table.Columns.Select(c => new TableColumn { ColumnName = c.ColumnName, DataType = c.DataType, DiscoveryHidden = c.DiscoveryHidden, Description = c.ColumnName == DeniedName ? DeniedDescription : null }).ToList()
        };
        harness.Tables.Tables[index] = table;
        return table;
    }

    private static void GrantExceptDenied(ExternalCatalogTestHarness harness, params TableIdentifier[] granted)
    {
        var set = granted.ToHashSet();
        harness.Pdp.Decide = r => set.Contains(r.Object.Identifier)
            ? TableAccessDecision.Allowed(
                r.Object.Identifier,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["email"] = ColumnAccessLevel.Mask,
                    [DeniedName] = ColumnAccessLevel.Deny
                },
                hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(r.Object.Identifier, "Denied");
    }

    private static ClaimsPrincipal Person(string sid = UserSid) =>
        ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", sid, scopes: ExternalApiPermissions.CatalogRead);

    private static JsonElement Columns(JsonElement obj) => obj.GetProperty("columns");

    [Fact]
    public async Task GetObject_Person_DeniedColumn_HasNoNameNorDescription()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var table = AddTable(harness, "patients");
        GrantExceptDenied(harness, table.Identifier);

        var resp = await harness.GetObjectByIdAsync(Person(), table.Table.Id.ToString());

        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        resp.Body.ShouldNotContain(DeniedName);
        resp.Body.ShouldNotContain(DeniedDescription);
        Columns(resp.ReadJson()).EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["id", "email"]);
    }

    [Fact]
    public async Task ListObjects_Person_UngrantedObject_HasNoColumns()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var granted = AddTable(harness, "patients");
        var ungranted = AddTable(harness, "visits");
        GrantExceptDenied(harness, granted.Identifier);

        var resp = await harness.ListObjectsAsync(Person());

        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var items = resp.ReadJson().GetProperty("items").EnumerateArray().ToList();
        var ungrantedItem = items.Single(i => i.GetProperty("objectId").GetString() == ungranted.Identifier.ToString());
        ungrantedItem.GetProperty("callerState").GetProperty("access").GetString().ShouldNotBe("granted");
        Columns(ungrantedItem).GetArrayLength().ShouldBe(0);
        resp.Body.ShouldNotContain(DeniedName);
        resp.Body.ShouldNotContain(DeniedDescription);
        resp.Body.ShouldNotContain("internal_flag");
    }

    [Fact]
    public async Task GetObject_PersonOwnerOfHiddenTable_HiddenColumnAppendix_NotEmitted()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var table = AddTable(harness, "owned_hidden", hidden: true);
        harness.Owners.AssignOwner(table.Identifier, UserSid, "Owner");
        GrantExceptDenied(harness, table.Identifier);

        var resp = await harness.GetObjectByIdAsync(Person(), table.Table.Id.ToString());

        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        resp.Body.ShouldNotContain("internal_flag");
        resp.Body.ShouldNotContain(DeniedName);
        Columns(resp.ReadJson()).EnumerateArray().Select(c => c.GetProperty("name").GetString()).ShouldBe(["id", "email"]);
    }

    [Fact]
    public async Task GetObject_Person_AccessStateIsClearOrMaskedOnly()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var table = AddTable(harness, "patients");
        GrantExceptDenied(harness, table.Identifier);

        var resp = await harness.GetObjectByIdAsync(Person(), table.Table.Id.ToString());

        Columns(resp.ReadJson()).EnumerateArray().Select(c => c.GetProperty("accessState").GetString())
            .ShouldBe(["clear", "masked"]);
    }

    [Fact]
    public async Task GetObject_Person_NeverEmitsNoAccessAccessState()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var granted = AddTable(harness, "patients");
        var ungranted = AddTable(harness, "visits");
        GrantExceptDenied(harness, granted.Identifier);

        var list = await harness.ListObjectsAsync(Person());
        var byId = await harness.GetObjectByIdAsync(Person(), granted.Table.Id.ToString());

        list.Body.ToLowerInvariant().ShouldNotContain("noaccess");
        byId.Body.ToLowerInvariant().ShouldNotContain("noaccess");
        ungranted.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetObject_Person_PartitionStatistics_DoNotNameDeniedColumn_GrantedAndUngranted()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var granted = AddTable(harness, "patients");
        var ungranted = AddTable(harness, "visits");
        foreach (var t in new[] { granted, ungranted })
        {
            harness.Artifacts.Artifacts[t.Table.Id] = new DiscoveryArtifacts(
                t.Table.Id,
                "tenant-a",
                SamplesJson: null,
                StatisticsJson: $"{{\"table\":{{\"approxRowCount\":42,\"partitionColumns\":[\"id\",\"{DeniedName}\"]}}}}",
                ContentSha256: "abc",
                UploadedBy: "S-ADMIN",
                UploadedAt: DateTimeOffset.UtcNow);
        }

        GrantExceptDenied(harness, granted.Identifier);

        var grantedResp = await harness.GetObjectByIdAsync(Person(), granted.Table.Id.ToString());
        var ungrantedResp = await harness.GetObjectByIdAsync(Person(), ungranted.Table.Id.ToString());

        grantedResp.Body.ShouldNotContain(DeniedName);
        ungrantedResp.Body.ShouldNotContain(DeniedName);
        grantedResp.ReadJson().GetProperty("statistics").GetProperty("partitionColumns").EnumerateArray()
            .Select(p => p.GetString()).ShouldBe(["id"]);
        ungrantedResp.ReadJson().GetProperty("statistics").GetProperty("partitionColumns").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task GetObject_ServicePrincipal_ColumnsUnchanged()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "catalog-sync-app", enabled: true);
        var table = AddTable(harness, "patients");
        GrantExceptDenied(harness);

        var app = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "catalog-sync-app", ExternalApiPermissions.CatalogRead);
        var resp = await harness.GetObjectByIdAsync(app, table.Table.Id.ToString());

        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        Columns(resp.ReadJson()).EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .ShouldBe(["id", "email", DeniedName]);
    }

    private static ClaimsPrincipal Token(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Test"));

    [Fact]
    public void IsAppOnlyPrincipal_PersonWithAzpNoScopeNoSid_IsNotAppView()
    {
        // A person token carries a person identifier (upn) even when the sid is not resolvable.
        var person = Token(new Claim("azp", "web-client"), new Claim("upn", "alice@example.com"));

        ExternalCatalogReader.IsAppOnlyPrincipal(person).ShouldBeFalse();
        ExternalApiPermissions.IsServicePrincipal(person).ShouldBeFalse();
    }

    [Fact]
    public void IsAppOnlyPrincipal_PersonWithSid_IsNeverAppView()
    {
        var person = Token(new Claim("azp", "web-client"), new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-PERSON"));

        ExternalCatalogReader.IsAppOnlyPrincipal(person).ShouldBeFalse();
        ExternalApiPermissions.IsServicePrincipal(person).ShouldBeFalse();
    }

    [Fact]
    public void IsAppOnlyPrincipal_PersonWithSidAndServiceAccountName_IsNotAppView()
    {
        var person = Token(
            new Claim("azp", "web-client"),
            new Claim("preferred_username", "service-account-x"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-PERSON"));

        ExternalCatalogReader.IsAppOnlyPrincipal(person).ShouldBeFalse();
        ExternalApiPermissions.IsServicePrincipal(person).ShouldBeFalse();
    }

    [Fact]
    public void IsAppOnlyPrincipal_IdtypApp_IsAppView() =>
        ExternalCatalogReader.IsAppOnlyPrincipal(Token(new Claim("idtyp", "app"))).ShouldBeTrue();

    [Fact]
    public void IsAppOnlyPrincipal_ClientCredentialsGty_IsAppView()
    {
        ExternalCatalogReader.IsAppOnlyPrincipal(Token(new Claim("gty", "client-credentials"))).ShouldBeTrue();
        ExternalCatalogReader.IsAppOnlyPrincipal(Token(new Claim("gty", "client_credentials"))).ShouldBeTrue();
    }

    public static TheoryData<string, Claim[]> ClaimSets => new()
    {
        { "idtyp app", [new Claim("idtyp", "app")] },
        { "gty cc", [new Claim("gty", "client-credentials")] },
        { "azp only", [new Claim("azp", "svc")] },
        { "azp + upn", [new Claim("azp", "svc"), new Claim("upn", "a@b.c")] },
        { "azp + sid", [new Claim("azp", "svc"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")] },
        { "azp + sub", [new Claim("azp", "svc"), new Claim("sub", "abc")] },
        { "azp + scope", [new Claim("azp", "svc"), new Claim("scp", "Autheris.Catalog.Read")] },
        { "service account name", [new Claim("preferred_username", "service-account-x")] },
        { "plain person", [new Claim("email", "a@b.c")] }
    };

    [Theory]
    [MemberData(nameof(ClaimSets))]
    public void AppOnlyPredicate_AgreesWith_ExternalApiPermissions(string name, Claim[] claims)
    {
        var principal = Token(claims);

        ExternalCatalogReader.IsAppOnlyPrincipal(principal).ShouldBe(ExternalApiPermissions.IsServicePrincipal(principal), name);
    }

    [Fact]
    public async Task RestCatalogObjects_PersonTokenWithAzpNoScope_ReturnsPersonView_Unit()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        harness.RegisterExternalSystem("tenant-a", "web-client", enabled: true);
        var table = AddTable(harness, "patients");
        GrantExceptDenied(harness, table.Identifier);

        // azp, no scope claim, a resolvable user subject: the person view applies, never the unfiltered service-principal view.
        var person = Token(
            new Claim("azp", "web-client"),
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.PrimarySid, UserSid),
            new Claim("roles", ExternalApiPermissions.CatalogRead));
        var resp = await harness.GetObjectByIdAsync(person, table.Table.Id.ToString());

        resp.Body.ShouldNotContain(DeniedName);
    }
}
