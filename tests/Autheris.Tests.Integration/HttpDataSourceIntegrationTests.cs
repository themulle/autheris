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

public sealed class HttpDataSourceIntegrationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

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

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
        }

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

    public HttpDataSourceIntegrationTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", "Data Source=:memory:;Mode=Memory;Cache=Shared");
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

    private HttpClient CreateClient(Sid? userSid = null, string? roles = null)
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

    [Fact]
    public async Task DeclarativeHttp_EnforcesCentralRlsAndColumnMasking()
    {
        var userSid = new Sid("S-1-5-21-HTTP-DECL-USER");
        var client = CreateClient(userSid);

        var tableId = new TableIdentifier("crm", "public", "customers");

        using (var scope = _factory.Services.CreateScope())
        {
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
                    Method = "GET"
                }
            };

            var columns = new[]
            {
                new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "int" },
                new TableColumn { TableId = table.Id, ColumnName = "name", DataType = "varchar" },
                new TableColumn { TableId = table.Id, ColumnName = "region", DataType = "varchar" },
                new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "varchar", IsSensitive = true }
            };

            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "[REDACTED-EMAIL]" }
            };

            var meta = new TableMetadata
            {
                Identifier = tableId,
                Table = table,
                Columns = columns,
                ColumnMaskingRules = maskingRules
            };

            var savedMeta = await repo.UpsertTableMetadataAsync(meta);

            // Grant consent: ALLOW with row filter `region = 'EU'` and masking on `email`
            await repo.CreateConsentAsync(new Consent
            {
                TableId = savedMeta.Table.Id,
                TableIdentifier = tableId,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                RowFilters =
                [
                    new ConsentRowFilter
                    {
                        ColumnName = "region",
                        Operator = "EQ",
                        ValueType = "string",
                        ValueJson = "\"EU\"",
                        ValueSource = "LITERAL"
                    }
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

        // Query the declarative HTTP table via GraphQL
        var gqlQuery = new
        {
            query = """
            query {
                table(domain: "crm", name: "customers", schema: "public") {
                    tableName
                    totalCount
                    jsonRows
                }
            }
            """
        };

        var response = await client.PostAsJsonAsync("/graphql", gqlQuery);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("FORBIDDEN");
        body.ShouldContain("crm.public.customers");

        using var doc = JsonDocument.Parse(body);
        var tableData = doc.RootElement.GetProperty("data").GetProperty("table");
        var totalCount = tableData.GetProperty("totalCount").GetInt32();

        // 1. RLS Post-Filtering: 'Beta LLC' with region 'US' MUST be filtered out! Only 2 EU rows remain.
        totalCount.ShouldBe(2);

        var jsonRows = tableData.GetProperty("jsonRows").EnumerateArray().Select(e => e.GetString()!).ToList();
        jsonRows.Count.ShouldBe(2);

        // 2. Central Masking: email MUST be masked to [REDACTED-EMAIL]
        foreach (var r in jsonRows)
        {
            r.ShouldContain("[REDACTED-EMAIL]");
            r.ShouldNotContain("alpha@example.com");
            r.ShouldNotContain("gamma@example.com");
            r.ShouldContain("EU");
            r.ShouldNotContain("US");
        }
    }

    [Fact]
    public async Task PluginHttp_EnforcesCentralColumnMaskingAndZeroTrustStripping()
    {
        var userSid = new Sid("S-1-5-21-HTTP-PLUGIN-USER");
        var client = CreateClient(userSid);

        var tableId = new TableIdentifier("billing", "public", "invoices");

        using (var scope = _factory.Services.CreateScope())
        {
            // Register plugin in IPluginManager
            var pluginManager = scope.ServiceProvider.GetRequiredService<IPluginManager>();
            pluginManager.RegisterPlugin(new IntegrationTestBillingPlugin());

            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();

            var table = new Table
            {
                SourceName = "billing_service",
                SchemaName = "public",
                TableName = "invoices",
                DataSourceType = DataSourceType.HttpPlugin,
                PluginName = "IntegrationTestBillingPlugin"
            };

            var columns = new[]
            {
                new TableColumn { TableId = table.Id, ColumnName = "invoice_id", DataType = "varchar" },
                new TableColumn { TableId = table.Id, ColumnName = "customer_id", DataType = "varchar" },
                new TableColumn { TableId = table.Id, ColumnName = "tax_number", DataType = "varchar", IsSensitive = true },
                new TableColumn { TableId = table.Id, ColumnName = "total", DataType = "decimal" },
                new TableColumn { TableId = table.Id, ColumnName = "secret_notes", DataType = "varchar", IsSensitive = true }
            };

            var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["tax_number"] = new MaskingRule { RuleType = "REDACT", Replacement = "[TAX-REDACTED]" }
            };

            var meta = new TableMetadata
            {
                Identifier = tableId,
                Table = table,
                Columns = columns,
                ColumnMaskingRules = maskingRules
            };

            var savedMeta = await repo.UpsertTableMetadataAsync(meta);

            // Grant consent:
            // ALLOW invoice_id (Clear), customer_id (Clear), tax_number (Mask), total (Clear)
            // secret_notes is DENIED (not in column rules -> Zero-Trust Deny)
            await repo.CreateConsentAsync(new Consent
            {
                TableId = savedMeta.Table.Id,
                TableIdentifier = tableId,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = userSid,
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
                ValidTo = DateTimeOffset.UtcNow.AddDays(30),
                ColumnRules =
                [
                    new ConsentColumnRule { ColumnName = "invoice_id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "customer_id", AccessLevel = ColumnAccessLevel.Clear },
                    new ConsentColumnRule { ColumnName = "tax_number", AccessLevel = ColumnAccessLevel.Mask },
                    new ConsentColumnRule { ColumnName = "total", AccessLevel = ColumnAccessLevel.Clear }
                    // Notice: secret_notes omitted -> Zero-Trust Deny!
                ]
            });
        }

        var gqlQuery = new
        {
            query = """
            query {
                table(domain: "billing", name: "invoices", schema: "public") {
                    tableName
                    totalCount
                    jsonRows
                }
            }
            """
        };

        var response = await client.PostAsJsonAsync("/graphql", gqlQuery);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain("FORBIDDEN");
        body.ShouldContain("billing.public.invoices");

        using var doc = JsonDocument.Parse(body);
        var tableData = doc.RootElement.GetProperty("data").GetProperty("table");
        var jsonRows = tableData.GetProperty("jsonRows").EnumerateArray().Select(e => e.GetString()!).ToList();
        jsonRows.Count.ShouldBe(2);

        foreach (var r in jsonRows)
        {
            // Central masking applied:
            r.ShouldContain("[TAX-REDACTED]");
            r.ShouldNotContain("DE123456789");
            r.ShouldNotContain("FR987654321");

            // Zero-Trust stripping: secret_notes must NOT appear in output!
            r.ShouldNotContain("secret_notes");
            r.ShouldNotContain("CONFIDENTIAL INTERNAL NOTE");
        }
    }

    [Fact]
    public async Task DeclarativeHttp_FailsClosed_WhenUserHasNoActiveConsent()
    {
        var unauthorizedUserSid = new Sid("S-1-5-21-NO-CONSENT-USER");
        var client = CreateClient(unauthorizedUserSid);

        var tableId = new TableIdentifier("crm", "public", "customers");

        using (var scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            var meta = new TableMetadata
            {
                Identifier = tableId,
                Table = new Table
                {
                    SourceName = "crm_service",
                    SchemaName = "public",
                    TableName = "customers",
                    DataSourceType = DataSourceType.HttpDeclarative,
                    HttpEndpoint = new HttpEndpointDescriptor
                    {
                        BaseUrl = "https://mock.crm.internal",
                        PathTemplate = "/api/v1/customers"
                    }
                },
                Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
            };
            await repo.UpsertTableMetadataAsync(meta);
        }

        var gqlQuery = new
        {
            query = """
            query {
                table(domain: "crm", name: "customers", schema: "public") {
                    tableName
                    totalCount
                }
            }
            """
        };

        var response = await client.PostAsJsonAsync("/graphql", gqlQuery);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldContain("FORBIDDEN");
        body.ShouldContain("Zugriff auf Tabelle");
    }
}
