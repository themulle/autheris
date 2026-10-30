using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Plugins;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public abstract class RestDatasetHttpTableTestBase : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    protected WebApplicationFactory<Program> Factory => _factory;

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    private sealed class IntegrationTestBillingPlugin : IHttpDataSourcePlugin
    {
        public string Name => "IntegrationTestBillingPlugin";

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
            PluginExecutionContext context,
            CancellationToken ct = default)
        {
            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows =
            [
                new Dictionary<string, object?>
                {
                    ["invoice_id"] = "INV-2026-001",
                    ["customer_id"] = "CUST-10",
                    ["tax_number"] = "DE123456789",
                    ["total"] = 1500.00m,
                    ["secret_notes"] = "CONFIDENTIAL INTERNAL NOTE"
                },
                new Dictionary<string, object?>
                {
                    ["invoice_id"] = "INV-2026-002",
                    ["customer_id"] = "CUST-20",
                    ["tax_number"] = "FR987654321",
                    ["total"] = 2800.50m,
                    ["secret_notes"] = "ANOTHER CONFIDENTIAL NOTE"
                }
            ];

            return Task.FromResult(rows);
        }
    }

    protected RestDatasetHttpTableTestBase(WebApplicationFactory<Program> factory, bool crossSourceEnabled, bool restTransportAllowed = true)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:Rebac:Enabled", "false");
            // The seeded table is owned by the shared test default tenant; claim-less test principals are assigned it.
            builder.UseSetting("Gateway:Tenancy:DefaultTenantId", Autheris.Tests.Common.TestTenants.Default.Value);
            builder.UseSetting("Gateway:WebSql:CrossSource:Enabled", crossSourceEnabled ? "true" : "false");
            if (crossSourceEnabled && restTransportAllowed)
            {
                // REST federation is an explicit opt-in: "Rest" is not part of the default AllowedTransports.
                builder.UseSetting("Gateway:WebSql:CrossSource:AllowedTransports:0", "Rest");
            }
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-{GetType().Name}-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.ConfigureServices(services =>
            {
                // Register a mock HttpClientFactory that serves declarative REST responses
                var mockHandler = new DelegatingMockHandler(req =>
                {
                    if (req.RequestUri != null && req.RequestUri.AbsolutePath.Contains("/api/v1/customers"))
                    {
                        var json = """
                        [
                            { "id": 101, "name": "Alpha Corp", "region": "EU", "email": "alpha@example.com" },
                            { "id": 102, "name": "Beta LLC", "region": "US", "email": "beta@example.com" },
                            { "id": 103, "name": "Gamma GmbH", "region": "EU", "email": "gamma@example.com" }
                        ]
                        """;
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new StringContent(json, Encoding.UTF8, "application/json")
                        };
                    }

                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                });

                var httpClient = new HttpClient(mockHandler);
                var clientFactory = Substitute.For<IHttpClientFactory>();
                clientFactory.CreateClient(Arg.Any<string>()).Returns(httpClient);
                services.AddSingleton(clientFactory);
            });
        });
    }

    protected HttpClient CreateClient(Sid? userSid = null, string? roles = null)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        if (userSid != null)
        {
            client.DefaultRequestHeaders.Add("X-Test-User-Sid", userSid.Value.Value);
        }
        if (!string.IsNullOrWhiteSpace(roles))
        {
            client.DefaultRequestHeaders.Add("X-Test-Roles", roles);
        }
        return client;
    }

    protected static TableIdentifier TableId => new("crm", "public", "customers");

    protected async Task<TableMetadata> SeedTableAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        var table = new Table
        {
            SourceName = "crm_service",
            SchemaName = "public",
            TableName = "customers",
            DataSourceType = DataSourceType.HttpDeclarative,
            HttpEndpoint = new HttpEndpointDescriptor
            {
                BaseUrl = "https://mock.crm.internal",
                PathTemplate = "/api/v1/customers",
                Method = "GET",
                CompleteResponse = true
            }
        };
        var meta = new TableMetadata
        {
            Identifier = TableId,
            Table = table,
            Columns =
            [
                new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = table.Id, ColumnName = "name", DataType = "varchar" },
                new TableColumn { TableId = table.Id, ColumnName = "region", DataType = "varchar" },
                new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "varchar", IsSensitive = true }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "[REDACTED-EMAIL]" }
            }
        };
        return await repo.UpsertTableMetadataAsync(meta);
    }

    /// <summary>Seeds the table and a minimal ALLOW consent (id column only) for <paramref name="userSid"/>.</summary>
    protected async Task SeedTableWithConsentAsync(Sid userSid)
    {
        var saved = await SeedTableAsync();
        using var scope = Factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
        await repo.CreateConsentAsync(new Consent
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableId = saved.Table.Id,
            TableIdentifier = TableId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
        });
    }
}

public sealed class RestDatasetHttpTableIntegrationTests : RestDatasetHttpTableTestBase
{
    public RestDatasetHttpTableIntegrationTests(WebApplicationFactory<Program> factory)
        : base(factory, crossSourceEnabled: true)
    {
    }

    [Fact]
    public async Task RestDataset_OnProtectedHttpTable_WithoutConsent_Returns403_Not500()
    {
        var client = CreateClient(new Sid("S-1-5-21-REST-NO-CONSENT"));
        await SeedTableAsync();

        var response = await client.GetAsync("/api/v1/data/crm/public/customers?limit=2&offset=0");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
    }

    [Fact]
    public async Task RestDataset_OnHttpTable_WithConsent_PaginatesWithRowFilterAndMasking()
    {
        var userSid = new Sid("S-1-5-21-REST-HTTP-USER");
        var client = CreateClient(userSid);
        var saved = await SeedTableAsync();

        using (var scope = Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            await repo.CreateConsentAsync(new Consent
            {
                TenantId = Autheris.Tests.Common.TestTenants.Default,
                TableId = saved.Table.Id,
                TableIdentifier = TableId,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                RowFilters =
                [
                    new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"", ValueSource = "LITERAL" }
                ],
                ColumnRules =
                [
                    new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "name", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
                ]
            });
        }

        var response = await client.GetAsync("/api/v1/data/crm/public/customers?limit=1&offset=0");
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("data").GetArrayLength().ShouldBe(1);
        doc.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        body.ShouldContain("[REDACTED-EMAIL]");
        body.ShouldNotContain("@example.com");
        body.ShouldNotContain("Beta LLC");

        var page2 = await client.GetAsync("/api/v1/data/crm/public/customers?limit=1&offset=1");
        var body2 = await page2.Content.ReadAsStringAsync();
        page2.StatusCode.ShouldBe(HttpStatusCode.OK, body2);
        using var doc2 = JsonDocument.Parse(body2);
        doc2.RootElement.GetProperty("data").GetArrayLength().ShouldBe(1);
        doc2.RootElement.GetProperty("hasMore").GetBoolean().ShouldBeFalse();
        body2.ShouldContain("Gamma GmbH");
    }

    [Theory]
    [InlineData(9000, 999, HttpStatusCode.OK)]                 // offset + limit = 9999 (window 10000, the cap)
    [InlineData(9000, 1000, HttpStatusCode.BadRequest)]        // offset + limit = 10000 (window 10001 > cap)
    public async Task RestDataset_PaginationWindowBoundary(int offset, int limit, HttpStatusCode expected)
    {
        var userSid = new Sid($"S-1-5-21-REST-WINDOW-{offset}-{limit}");
        var client = CreateClient(userSid);
        await SeedTableWithConsentAsync(userSid);

        var response = await client.GetAsync($"/api/v1/data/crm/public/customers?select=id&limit={limit}&offset={offset}");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(expected, body);
    }
}

/// <summary>
/// Cross-source federation is disabled by default (fail-closed). A consented REST read of an HTTP table must then be a
/// clean 403 policy denial, never a 500.
/// </summary>
public sealed class RestDatasetHttpTableCrossSourceDisabledIntegrationTests : RestDatasetHttpTableTestBase
{
    public RestDatasetHttpTableCrossSourceDisabledIntegrationTests(WebApplicationFactory<Program> factory)
        : base(factory, crossSourceEnabled: false)
    {
    }

    [Fact]
    public async Task RestDataset_OnHttpTable_WithConsent_WhenCrossSourceDisabled_Returns403_Not500()
    {
        var userSid = new Sid("S-1-5-21-REST-XS-DISABLED");
        var client = CreateClient(userSid);
        var saved = await SeedTableAsync();

        using (var scope = Factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            await repo.CreateConsentAsync(new Consent
            {
                TenantId = Autheris.Tests.Common.TestTenants.Default,
                TableId = saved.Table.Id,
                TableIdentifier = TableId,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules = [new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }]
            });
        }

        var response = await client.GetAsync("/api/v1/data/crm/public/customers?limit=1&offset=0");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
    }
}

/// <summary>
/// Cross-source federation is enabled but REST is not listed in CrossSource.AllowedTransports: REST federation is an explicit
/// opt-in, so the read is a generic 403 that discloses neither the federation policy nor the table.
/// </summary>
public sealed class RestDatasetHttpTableRestNotOptedInIntegrationTests : RestDatasetHttpTableTestBase
{
    public RestDatasetHttpTableRestNotOptedInIntegrationTests(WebApplicationFactory<Program> factory)
        : base(factory, crossSourceEnabled: true, restTransportAllowed: false)
    {
    }

    [Fact]
    public async Task RestDataset_OnHttpTable_WhenRestNotInAllowedTransports_Returns403WithGenericBody()
    {
        var userSid = new Sid("S-1-5-21-REST-NOT-OPTED-IN");
        var client = CreateClient(userSid);
        await SeedTableWithConsentAsync(userSid);

        var response = await client.GetAsync("/api/v1/data/crm/public/customers?limit=1&offset=0");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, body);
        // The governed contract: ProblemDetails with a governed code. The request path (instance) is the caller's own input; every
        // other member must be generic.
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("status").GetInt32().ShouldBe(403);
        Autheris.Api.Errors.ApiErrorCodes.IsGoverned(doc.RootElement.GetProperty("code").GetString()).ShouldBeTrue(body);
        var generic = System.Text.RegularExpressions.Regex.Replace(body, "\"instance\":\"[^\"]*\",?", string.Empty);
        generic.ShouldNotContain("Cross-catalog", Case.Insensitive);
        generic.ShouldNotContain("customers", Case.Insensitive);
        generic.ShouldNotContain("AllowedTransports", Case.Insensitive);
    }
}
