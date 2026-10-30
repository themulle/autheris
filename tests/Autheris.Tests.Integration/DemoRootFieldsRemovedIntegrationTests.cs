namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

/// <summary>
/// RDF-S5: the demo root fields <c>finance</c> and <c>hr</c> were removed. Selecting them answers a GraphQL validation error
/// (field does not exist), never an internal error, never rows, never exception text. No SQL Server or Docker needed.
/// </summary>
[Trait("Category", "Security")]
public sealed class DemoRootFieldsRemovedIntegrationTests : IDisposable
{
    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), $"autheris-rdf-{Guid.NewGuid():N}.db");
    private readonly string _dataFile = Path.Combine(Path.GetTempPath(), $"autheris-rdf-data-{Guid.NewGuid():N}.db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _dbFile, _dataFile }.Where(File.Exists))
        {
            try { File.Delete(f); } catch (IOException) { }
        }
    }

    [Theory]
    [InlineData("{ finance { invoices { jsonRows } } }")]
    [InlineData("{ hr { employees { jsonRows } } }")]
    [InlineData("{ finance { invoicesWithItems(first: 2) { id items { productName } } } }")]
    public async Task FinanceRootSelection_AsReader_ReturnsValidationErrorNotInternalError(string query)
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-SIM");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_sim");
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        using var response = await Post(client, query);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError, body);
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("errors", out var errors).ShouldBeTrue(body);
        errors.GetArrayLength().ShouldBeGreaterThan(0);
        errors.EnumerateArray().Any(e => e.TryGetProperty("extensions", out var vext) && vext.TryGetProperty("code", out var vc) && vc.GetString() == "GRAPHQL_VALIDATION_FAILED").ShouldBeTrue(body);
        errors.EnumerateArray().Any(e => e.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("code", out var c) && c.GetString() == "INTERNAL_SERVER_ERROR").ShouldBeFalse(body);
        if (doc.RootElement.TryGetProperty("data", out var data))
        {
            data.ValueKind.ShouldBe(JsonValueKind.Null, body);
        }

        body.ShouldNotContain("jsonRows\":[");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain("   at ");
        body.ShouldNotContain("type Query");
    }

    [Theory]
    [InlineData("{ finance { invoices { jsonRows } } }")]
    [InlineData("{ hr { employees { jsonRows } } }")]
    public async Task FinanceRootSelection_Unauthenticated_NoRowsNoException(string query)
    {
        using var factory = Create();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        using var response = await Post(client, query);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError, body);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, body);
        body.ShouldNotContain("jsonRows\":[");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task Table_Unauthenticated_IsRejected()
    {
        // RDF-S6: parity for the deleted FinanceAndHrQuery_Unauthenticated_ThrowsUnauthorized on the generic table field.
        using var factory = Create();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        using var response = await Post(client, "{ table(domain: \"finance\", schema: \"dbo\", name: \"finance_table_1\", first: 5) { jsonRows } }");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, body);
        body.ShouldNotContain("jsonRows\":[");
    }

    [Theory]
    [InlineData("{ finance { invoices { jsonRows } } }")]
    [InlineData("{ hr { employees { jsonRows } } }")]
    public async Task FinanceRootSelection_InTrustedDocumentsOnlyMode_IsRejectedWithoutRows(string query)
    {
        using var factory = Create(trustedDocumentsOnly: true);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-SIM");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Reader");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_sim");
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        using var response = await Post(client, query);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldNotBe(HttpStatusCode.InternalServerError, body);
        body.ShouldNotContain("jsonRows\":[");
        body.ShouldNotContain("Exception");
        body.ShouldNotContain("   at ");
        body.ShouldContain("errors");
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string query)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");
        return await client.PostAsync(new Uri("/graphql", UriKind.Relative), content);
    }

    private WebApplicationFactory<Program> Create(bool trustedDocumentsOnly = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            b.UseSetting("Gateway:RateLimiting:PreAuthIpRateLimit:PermitLimit", "100000");
            b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokenBucketCapacity", "100000");
            b.UseSetting("Gateway:RateLimiting:PostAuthSidRateLimit:TokensPerSecond", "10000");
            b.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            b.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source={_dbFile}");
            b.UseSetting("Gateway:Insecure:danger_bypass_consent_checks", "true");
            b.UseSetting("Gateway:Rebac:Enabled", "false");
            if (trustedDocumentsOnly)
            {
                b.UseSetting("Gateway:GraphQL:PersistedQueriesOnly", "true");
                b.UseSetting("Gateway:GraphQL:AppScopedTrustedDocuments:Enabled", "true");
            }

            b.UseSetting("Gateway:DataSources:Connections:default:Provider", "Sqlite");
            b.UseSetting("Gateway:DataSources:Connections:default:ConnectionString", $"Data Source={_dataFile}");
        });
}
