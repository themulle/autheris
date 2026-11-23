namespace Autheris.Tests.Unit.Catalog;

using Autheris.Domain.Model;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1050: <c>GET /api/v1/catalog/objects/{id}</c> resolves the one object from the catalog snapshot instead of reading the whole
/// catalog per request. The responses (status and body) must be those of the full-read path for every visibility case, and an unknown id
/// must not read the catalog at all.
/// </summary>
[Trait("Category", "Security")]
public sealed class ExternalCatalogSingleObjectTests
{
    private const string Person = "S-1-5-21-1050-1050-1050-1001";
    private const string Owner = "S-1-5-21-1050-1050-1050-1002";

    public static TheoryData<string> Cases() => new()
    {
        "visible-by-identifier",
        "visible-by-table-id",
        "hidden-not-owner",
        "hidden-owner",
        "foreign-tenant",
        "inactive",
        "unknown",
        "malformed"
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task SnapshotPath_AnswersLikeTheFullReadPath(string scenario)
    {
        await using var harness = Seed();
        var (principal, id) = Request(harness, scenario);

        var full = await harness.GetObjectByIdAsync(principal, id, reader: harness.CreateFullReadReader());
        var before = harness.Tables.AllTablesReads;
        var fromSnapshot = await harness.GetObjectByIdAsync(principal, id);

        fromSnapshot.StatusCode.ShouldBe(full.StatusCode, scenario);
        WithoutRequestIds(fromSnapshot.Body).ShouldBe(WithoutRequestIds(full.Body), scenario);
        (harness.Tables.AllTablesReads - before).ShouldBe(0, "the snapshot path never reads the whole catalog");
    }

    [Fact]
    public async Task ExpectedStatuses_AreFailClosed()
    {
        await using var harness = Seed();
        foreach (var ok in new[] { "visible-by-identifier", "visible-by-table-id", "hidden-owner" })
        {
            var (principal, id) = Request(harness, ok);
            (await harness.GetObjectByIdAsync(principal, id)).StatusCode.ShouldBe(StatusCodes.Status200OK, ok);
        }

        foreach (var notFound in new[] { "hidden-not-owner", "foreign-tenant", "inactive", "unknown", "malformed" })
        {
            var (principal, id) = Request(harness, notFound);
            (await harness.GetObjectByIdAsync(principal, id)).StatusCode.ShouldBe(StatusCodes.Status404NotFound, notFound);
        }
    }

    // The correlation and trace ids differ per request by design.
    private static string WithoutRequestIds(string body) =>
        System.Text.RegularExpressions.Regex.Replace(body, "\"(correlationId|traceId)\":\"[^\"]*\"", "\"$1\":\"\"");

    private static ExternalCatalogTestHarness Seed()
    {
        var harness = ExternalCatalogTestHarness.Create(catalogSnapshot: true);
        harness.AddTable("pg", "customers", ownerTenantId: "tenant-a");
        var hidden = harness.AddTable("pg", "secret_audit", ownerTenantId: "tenant-a", hidden: true);
        harness.Owners.AssignOwner(hidden.Identifier, Owner, "Owner");
        harness.AddTable("pg", "other_tenant", ownerTenantId: "tenant-b");
        var inactive = harness.AddTable("pg", "retired", ownerTenantId: "tenant-a");
        harness.Tables.Tables[harness.Tables.Tables.IndexOf(inactive)] = inactive with { Table = inactive.Table with { IsActive = false } };
        return harness;
    }

    private static (System.Security.Claims.ClaimsPrincipal Principal, string Id) Request(ExternalCatalogTestHarness harness, string scenario)
    {
        TableMetadata Table(string name) => harness.Tables.Tables.Single(t => t.Identifier.TableName == name);
        var (caller, id) = scenario switch
        {
            "visible-by-identifier" => (Person, Table("customers").Identifier.ToString()),
            "visible-by-table-id" => (Person, Table("customers").Table.Id.ToString()),
            "hidden-not-owner" => (Person, Table("secret_audit").Identifier.ToString()),
            "hidden-owner" => (Owner, Table("secret_audit").Identifier.ToString()),
            "foreign-tenant" => (Person, Table("other_tenant").Identifier.ToString()),
            "inactive" => (Person, Table("retired").Identifier.ToString()),
            "unknown" => (Person, "pg.dbo.does_not_exist"),
            _ => (Person, "not-an-id")
        };

        return (ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", caller, scopes: "Apps.Read"), id);
    }
}
