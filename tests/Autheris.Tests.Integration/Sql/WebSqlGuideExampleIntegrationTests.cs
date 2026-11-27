namespace Autheris.Tests.Integration.Sql;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common.Hosting;
using Autheris.Tests.Integration.Governance;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// Replays the WebSQL example of the agent query guide, the OpenAPI document and the feature documentation unchanged on the
/// demo table DDL and a demo-like seed, and checks who may fetch the OpenAPI document.
/// </summary>
public sealed class WebSqlGuideExampleIntegrationTests : IClassFixture<WebSqlGuideExampleIntegrationTests.HostFixture>
{
    private const string OpenApiPath = "/api/v1/sql/openapi.json";
    private const string UnknownPath = "/api/v1/sql/openapi-unknown.json";

    // The demo table of deploy/sql/01-init-schema.sql and a seed with the expressions of deploy/sql/02-seed-data.sh.
    private const string DemoScript = """
        CREATE TABLE public.invoices (
            id SERIAL PRIMARY KEY,
            status VARCHAR(20),
            amount NUMERIC(12,2),
            created_at TIMESTAMPTZ
        );
        INSERT INTO public.invoices (status, amount, created_at)
        SELECT (ARRAY['APPROVED','PENDING','PAID','REJECTED','PROCESSING'])[1 + (g % 5)],
               round((10 + (g * 137.42) % 49990)::numeric, 2),
               now() - (g || ' minutes')::interval
        FROM generate_series(1, 300) AS g;
        """;

    private readonly HostFixture _fixture;

    public WebSqlGuideExampleIntegrationTests(HostFixture fixture) => _fixture = fixture;

    public sealed class HostFixture : IntegrationHost<HostFixture>
    {
        public HostFixture() => ConnectionString = PostgresDataSource.Create("g1201", DemoScript, "public");

        public string ConnectionString { get; }

        protected override void ConfigureHostConfiguration(IConfigurationBuilder config)
        {
            config.AddInMemoryCollection(Settings(ConnectionString, webSqlEnabled: true, anonymous: true));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                NpgsqlConnection.ClearAllPools();
            }
        }
    }

    private static Dictionary<string, string?> Settings(string connectionString, bool webSqlEnabled, bool anonymous)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Authentication:EnableTestAuthHandler"] = "true",
            ["Gateway:GovernanceDb:Provider"] = "PostgreSql",
            ["Gateway:GovernanceDb:ConnectionString"] = GovernanceTestDatabase.Create("WebSqlGuideExampleIntegrationTests"),
            ["Gateway:DataSources:Connections:finance:Provider"] = "PostgreSql",
            ["Gateway:DataSources:Connections:finance:DefaultSchema"] = "public",
            ["Gateway:DataSources:Connections:finance:ConnectionString"] = connectionString,
            ["Gateway:WebSql:Enabled"] = webSqlEnabled ? "true" : "false",
            ["Gateway:WebSql:DefaultDataSourceName"] = "finance",
        };

        if (anonymous)
        {
            settings["Gateway:Insecure:danger_allow_anonymous_access"] = "true";
            settings["Gateway:Insecure:danger_bypass_consent_checks"] = "true";
        }

        return settings;
    }

    private static HttpClient Authenticated(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-GUIDE1201");
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant_alpha");
        return client;
    }

    private async Task RegisterInvoicesAsync()
    {
        using var scope = _fixture.Services.CreateScope();
        var tableRepo = scope.ServiceProvider.GetRequiredService<ITableMetadataRepository>();
        await tableRepo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), DisplayName = "invoices", TableName = "invoices", SchemaName = "public", SourceName = WebSqlGuideExample.DataSource, SourceType = "PostgreSql" },
            Identifier = new TableIdentifier(WebSqlGuideExample.DataSource, "public", "invoices"),
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "integer" },
                new TableColumn { ColumnName = "status", DataType = "varchar" },
                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                new TableColumn { ColumnName = "created_at", DataType = "timestamp with time zone" }
            ],
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(),
            ColumnMaskingRules = new Dictionary<string, MaskingRule>()
        });
    }

    private static async Task<(HttpStatusCode Status, JsonDocument Body)> PostAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync("/api/v1/sql", content);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, JsonDocument.Parse(text));
    }

    private static string WithNext(string next) =>
        "{\n" +
        "  \"sql\": \"" + WebSqlGuideExample.Sql + "\",\n" +
        "  \"parameters\": " + WebSqlGuideExample.ParametersJson + ",\n" +
        "  \"dataSource\": \"" + WebSqlGuideExample.DataSource + "\",\n" +
        "  \"next\": \"" + next + "\"\n" +
        "}";

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue1201_Example_GivenGuideConstants_WhenPostedUnchangedAndNextFollowed_ThenRowsPagedAndNoMaskedColumn()
    {
        await RegisterInvoicesAsync();
        var client = Authenticated(_fixture);

        var (status, first) = await PostAsync(client, WebSqlGuideExample.RequestJson);

        status.ShouldBe(HttpStatusCode.OK, first.RootElement.ToString());
        var root = first.RootElement;
        root.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString())
            .ShouldBe(["id", "status", "amount", "created_at"]);
        root.GetProperty("columns").EnumerateArray().ShouldAllBe(c => !c.GetProperty("masked").GetBoolean());
        var rows = root.GetProperty("rows").EnumerateArray().ToList();
        rows.Count.ShouldBe(WebSqlGuideExample.PageSize);
        rows.ShouldAllBe(r => r.GetProperty("status").GetString() == "PAID" || r.GetProperty("status").GetString() == "PENDING");
        rows.ShouldAllBe(r => r.GetProperty("amount").GetDecimal() >= 100m);
        rows.ShouldAllBe(r => r.GetProperty("created_at").GetString()!.EndsWith('Z'));
        root.GetProperty("rowCount").GetInt32().ShouldBe(WebSqlGuideExample.PageSize);
        root.GetProperty("truncated").GetBoolean().ShouldBeTrue();
        var page = root.GetProperty("page");
        (page.GetProperty("number").GetInt32(), page.GetProperty("offset").GetInt32(), page.GetProperty("size").GetInt32())
            .ShouldBe((1, 0, WebSqlGuideExample.PageSize));
        var next = root.GetProperty("next").GetString();
        next.ShouldNotBeNullOrWhiteSpace();
        var lastId = rows[^1].GetProperty("id").GetInt32();

        var (status2, second) = await PostAsync(client, WithNext(next!));

        status2.ShouldBe(HttpStatusCode.OK, second.RootElement.ToString());
        var page2 = second.RootElement.GetProperty("page");
        (page2.GetProperty("number").GetInt32(), page2.GetProperty("offset").GetInt32(), page2.GetProperty("size").GetInt32())
            .ShouldBe((2, WebSqlGuideExample.PageSize, WebSqlGuideExample.PageSize));
        second.RootElement.GetProperty("rows").EnumerateArray().First().GetProperty("id").GetInt32().ShouldBeGreaterThan(lastId);

        var (limitStatus, limitBody) = await PostAsync(client, """
            { "sql": "SELECT id FROM finance.public.invoices ORDER BY id LIMIT @n", "parameters": { "n": 5 }, "dataSource": "finance" }
            """);

        limitStatus.ShouldBe(HttpStatusCode.BadRequest, limitBody.RootElement.ToString());
        limitBody.RootElement.GetProperty("code").GetString().ShouldBe("SQL_SYNTAX_ERROR");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue1201_OpenApi_GivenWebSqlEnabled_WhenAuthenticatedGet_ThenNoSentinelDataSourceAndDocumentedLimitsMatchOptions()
    {
        var client = Authenticated(_fixture);

        var response = await client.GetAsync(OpenApiPath);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        doc.RootElement.GetProperty("openapi").GetString().ShouldBe("3.0.3");
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.GetProperty("WebSqlPageRequest").GetProperty("properties").GetProperty("size").GetProperty("maximum").GetInt64()
            .ShouldBe(new Autheris.Domain.Options.WebSqlOptions().MaxAllowedRows);
        schemas.GetProperty("Problem").GetProperty("properties").TryGetProperty("code", out _).ShouldBeTrue();
        text.ShouldNotContain("DefaultDataSourceName");
        text.ShouldNotContain("AllowedDataSources");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue1201_OpenApi_GivenWebSqlEnabled_WhenAnonymousGet_ThenUnauthorized()
    {
        using var host = new IntegrationHost(Settings(_fixture.ConnectionString, webSqlEnabled: true, anonymous: false));

        var response = await host.CreateClient().GetAsync(OpenApiPath);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue1201_OpenApi_GivenWebSqlDisabled_WhenAuthenticatedGet_ThenResponseEqualsUnknownRoute()
    {
        using var host = new IntegrationHost(Settings(_fixture.ConnectionString, webSqlEnabled: false, anonymous: false));
        var client = Authenticated(host);

        var spec = await Snapshot(client, OpenApiPath);
        var unknown = await Snapshot(client, UnknownPath);

        spec.Status.ShouldBe(HttpStatusCode.NotFound);
        spec.ShouldBe(unknown);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Issue1201_OpenApi_GivenWebSqlDisabled_WhenAnonymousGet_ThenResponseEqualsUnknownProtectedRoute()
    {
        // The "protected route" is the enabled one: it sits behind authentication, so an anonymous caller must not be able to tell whether WebSQL is enabled.
        ResponseSnapshot disabled;
        using (var disabledHost = new IntegrationHost(Settings(_fixture.ConnectionString, webSqlEnabled: false, anonymous: false)))
        {
            disabled = await Snapshot(disabledHost.CreateClient(), OpenApiPath);
        }

        ResponseSnapshot enabled;
        using (var enabledHost = new IntegrationHost(Settings(_fixture.ConnectionString, webSqlEnabled: true, anonymous: false)))
        {
            enabled = await Snapshot(enabledHost.CreateClient(), OpenApiPath);
        }

        disabled.Status.ShouldBe(HttpStatusCode.Unauthorized);
        disabled.ShouldBe(enabled);
    }

    private sealed record ResponseSnapshot(HttpStatusCode Status, string Body, string Headers);

    // Status, headers (except dates and per-request ids) and body.
    private static async Task<ResponseSnapshot> Snapshot(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        var headers = response.Headers.Concat(response.Content.Headers)
            .Where(h => !h.Key.Contains("date", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Contains("trace", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Contains("id", StringComparison.OrdinalIgnoreCase)
                && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .OrderBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
            .Select(h => h.Key + "=" + string.Join(",", h.Value));
        return new ResponseSnapshot(response.StatusCode, body, string.Join("\n", headers));
    }
}
