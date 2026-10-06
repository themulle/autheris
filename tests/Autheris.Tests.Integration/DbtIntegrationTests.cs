namespace Autheris.Tests.Integration;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

public class DbtIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    private const string SampleDbtManifest = """
    {
      "nodes": {
        "model.analytics.monthly_revenue": {
          "name": "monthly_revenue",
          "database": "corp_dw",
          "schema": "finance",
          "description": "Monthly revenue model",
          "config": { "materialized": "table" },
          "contract": { "enforced": true },
          "tags": ["finance", "reporting"],
          "meta": { "owner": "FinanceTeam" },
          "columns": {
            "account_id": {
              "name": "account_id",
              "data_type": "integer",
              "description": "Account identifier",
              "tags": [],
              "meta": {}
            },
            "contact_email": {
              "name": "contact_email",
              "data_type": "varchar",
              "description": "Finance contact email",
              "tags": ["pii"],
              "meta": { "pii": "true" }
            }
          },
          "depends_on": { "nodes": [] }
        }
      }
    }
    """;

    public DbtIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
        });
    }

    [Fact]
    public async Task DbtSyncEndpoint_RequiresAuthentication()
    {
        var client = _factory.CreateClient();
        var content = new StringContent(SampleDbtManifest, Encoding.UTF8, "application/json");

        var response = await client.PostAsync("/api/extensions/dbt/sync", content);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DbtSyncEndpoint_WithAuthenticatedUser_IngestsManifestSuccessfully()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var content = new StringContent(SampleDbtManifest, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/extensions/dbt/sync", content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"success\":true");
        body.ShouldContain("\"parsedModelsCount\":1");
        body.ShouldContain("\"generatedProposalsCount\":1");
    }

    [Fact]
    public async Task DbtExposuresEndpoint_ReturnsValidYamlExposures()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var response = await client.GetAsync("/api/extensions/dbt/exposures");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var yaml = await response.Content.ReadAsStringAsync();
        yaml.ShouldContain("version: 2");
        yaml.ShouldContain("exposures:");

    }

    [Theory]
    [InlineData("/api/extensions/dbt/sync")]
    [InlineData("/api/extensions/dbt/run-results")]
    [InlineData("/api/extensions/dbt/health/reset")]
    public async Task DbtGlobalMutationEndpoints_DataOwnerWithoutAdminRole_ReturnsForbidden(string path)
    {
        // Review E-7: a DataOwner must not change global dbt metadata or quarantine state.
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-OWNER-1");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "DataOwner");

        var response = await client.PostAsync(path, new StringContent("{}", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DbtExposuresEndpoint_NonPrivilegedUser_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-USER-1");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Viewer");

        var response = await client.GetAsync("/api/extensions/dbt/exposures");
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task DbtProposalsAndApproval_Workflow_Succeeds()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        // 1. Sync manifest to generate proposal
        var content = new StringContent(SampleDbtManifest, Encoding.UTF8, "application/json");
        var syncResponse = await client.PostAsync("/api/extensions/dbt/sync", content);
        syncResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 2. Fetch pending proposals
        var proposalsResponse = await client.GetAsync("/api/extensions/dbt/proposals");
        proposalsResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var proposalsJson = await proposalsResponse.Content.ReadAsStringAsync();
        proposalsJson.ShouldContain("contact_email");
        proposalsJson.ShouldContain("MASK_EMAIL");

        using var doc = System.Text.Json.JsonDocument.Parse(proposalsJson);
        var firstProposal = doc.RootElement.EnumerateArray().First();
        var proposalId = firstProposal.GetProperty("id").GetString();

        // 3. Approve proposal
        var approveResponse = await client.PostAsync($"/api/extensions/dbt/proposals/{proposalId}/approve", null);
        approveResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var approveBody = await approveResponse.Content.ReadAsStringAsync();
        approveBody.ShouldContain("\"status\":1"); // DbtProposalStatus.Approved
    }

    [Fact]
    public async Task DbtValidateContractEndpoint_WithValidContract_ReturnsOk()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");

        var content = new StringContent(SampleDbtManifest, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/extensions/dbt/validate-contract", content);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("\"isCompatible\":true");
        body.ShouldContain("\"validatedModelsCount\":1");
    }

    [Fact]
    public async Task DbtRunResultsAndHealthWorkflow_Succeeds()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");

        var sampleRunResults = """
        {
          "metadata": {
            "dbt_version": "1.8.0",
            "generated_at": "2026-09-29T05:00:00Z",
            "elapsed_time": 1.5
          },
          "results": [
            {
              "status": "fail",
              "execution_time": 0.5,
              "unique_id": "test.analytics.not_null_monthly_revenue_account_id.xyz",
              "failures": 5,
              "message": "Got 5 nulls"
            }
          ]
        }
        """;

        // 1. Post run_results.json
        var content = new StringContent(sampleRunResults, Encoding.UTF8, "application/json");
        var postResponse = await client.PostAsync("/api/extensions/dbt/run-results", content);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 2. Query health for monthly_revenue
        var healthResponse = await client.GetAsync("/api/extensions/dbt/health?table=monthly_revenue");
        healthResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var healthBody = await healthResponse.Content.ReadAsStringAsync();
        healthBody.ShouldContain("\"status\":2"); // Quarantined

        // 3. Reset health
        var resetResponse = await client.PostAsync("/api/extensions/dbt/health/reset?table=monthly_revenue", null);
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 4. Verify healthy again
        var recheckResponse = await client.GetAsync("/api/extensions/dbt/health?table=monthly_revenue");
        recheckResponse.StatusCode.ShouldBe(HttpStatusCode.OK);
        var recheckBody = await recheckResponse.Content.ReadAsStringAsync();
        recheckBody.ShouldContain("\"status\":0"); // Healthy
    }

    [Fact]
    public async Task GraphQLQuery_QuarantinedTable_BlocksWithTableInQuarantineError()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-ADMIN-SID");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "GovernanceAdmin");
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");

        // 1. Post failing run_results for finance_table_1
        var sampleRunResults = """
        {
          "metadata": { "dbt_version": "1.8.0", "generated_at": "2026-09-29T05:00:00Z" },
          "results": [
            {
              "status": "fail",
              "unique_id": "test.analytics.not_null_finance_table_1_id.xyz",
              "failures": 10,
              "message": "Found 10 nulls in finance_table_1"
            }
          ]
        }
        """;

        var content = new StringContent(sampleRunResults, Encoding.UTF8, "application/json");
        var postResponse = await client.PostAsync("/api/extensions/dbt/run-results", content);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 2. Query quarantined table via GraphQL
        var query = new
        {
            query = @"query { table(domain: ""finance"", name: ""finance_table_1"") { tableName totalCount } }"
        };
        var gqlResponse = await client.PostAsJsonAsync("/graphql", query);
        gqlResponse.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await gqlResponse.Content.ReadAsStringAsync();
        body.ShouldContain("TABLE_IN_QUARANTINE");
        body.ShouldContain("Quarantined");

        // 3. Reset health
        var resetResponse = await client.PostAsync("/api/extensions/dbt/health/reset?table=finance_table_1", null);
        resetResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

        // 4. Query again - should no longer be blocked by TABLE_IN_QUARANTINE
        var recheckResponse = await client.PostAsJsonAsync("/graphql", query);
        var recheckBody = await recheckResponse.Content.ReadAsStringAsync();
        recheckBody.ShouldNotContain("TABLE_IN_QUARANTINE");
    }

    [Fact]
    public async Task DbtRunResults_UnprivilegedUser_ReturnsForbidden()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-NORMAL-USER");
        client.DefaultRequestHeaders.Add("X-Test-Roles", "Viewer");

        var sampleRunResults = """
        {
          "metadata": { "dbt_version": "1.8.0", "generated_at": "2026-09-29T05:00:00Z" },
          "results": []
        }
        """;

        var content = new StringContent(sampleRunResults, Encoding.UTF8, "application/json");
        var postResponse = await client.PostAsync("/api/extensions/dbt/run-results", content);
        postResponse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

