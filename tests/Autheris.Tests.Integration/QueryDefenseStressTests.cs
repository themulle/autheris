using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

namespace Autheris.Tests.Integration;

public class QueryDefenseStressTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public QueryDefenseStressTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("QueryDefenseStressTests"));
            builder.UseSetting("Gateway:GraphQL:MaxAllowedExecutionDepth", "6");
            builder.UseSetting("Gateway:GraphQL:MaxAllowedComplexity", "250");
            // The depth test walks a schema-introspection path; introspection is not a limit and stays unrelated to the defenses.
            builder.UseSetting("Gateway:GraphQL:EnableIntrospection", "true");
        });
    }

    private HttpClient CreateClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-test-analyst");
        return client;
    }

    [Fact]
    public async Task QueryCostAnalyzer_SmallLimit_PassesComplexityBudget()
    {
        var client = CreateClient();

        // Query requesting first: 5 should have cost ~ 50 + child costs <= 250
        var payload = new
        {
            query = @"
                query GetInvoices {
                    table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"", first: 5) { jsonRows }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldNotContain("QUERY_TOO_COMPLEX");
        content.ShouldNotContain("GRAPHQL_VALIDATION_FAILED"); // RDF-S4: the rewritten query resolves against the schema
    }

    [Fact]
    public async Task QueryCostAnalyzer_ExcessiveLimit_FailsWithQueryTooComplex()
    {
        var client = CreateClient();

        // Query requesting first: 5000 should exceed maximum allowed cost (250)
        var payload = new
        {
            query = @"
                query GetMassiveInvoices {
                    table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"", first: 5000) { jsonRows }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        // Hot Chocolate validation errors return HTTP 400 Bad Request
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task QueryCostAnalyzer_OmittedLimit_DefaultsToWorstCaseBudgetAndFails()
    {
        var client = CreateClient();

        // Each aliased selection without first/last is priced at the default page (50 rows); none of them exceeds the budget (250) alone,
        // only their sum does.
        var payload = new
        {
            query = @"
                query GetAllInvoicesWithoutLimit {
                    t1: table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"") { jsonRows }
                    t2: table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"") { jsonRows }
                    t3: table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"") { jsonRows }
                    t4: table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"") { jsonRows }
                    t5: table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"") { jsonRows }
                    t6: table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"") { jsonRows }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task MaxExecutionDepth_ExceedingDepthLimit6_FailsOnAstValidation()
    {
        var client = CreateClient();

        // Query exceeding depth 6: a real schema-introspection path of depth 7 (the static schema has no deeper object nesting).
        var payload = new
        {
            query = @"
                query DeepQuery {
                    __type(name: ""Query"") {
                        fields {
                            type {
                                ofType {
                                    ofType {
                                        ofType {
                                            name
                                        }
                                    }
                                }
                            }
                        }
                    }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("depth");
    }

    [Fact]
    public async Task QueryCostAnalyzer_FragmentSpreadBypassAttempt_IsDetectedAndBlocked()
    {
        var client = CreateClient();

        // An attacker wraps excessive list query in a named fragment attempting to bypass cost analysis
        var payload = new
        {
            query = @"
                query MassiveQueryViaFragment {
                    ...HeavyFragment
                }
                fragment HeavyFragment on Query {
                    table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"", first: 5000) { jsonRows }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task QueryCostAnalyzer_InlineFragmentBypassAttempt_IsDetectedAndBlocked()
    {
        var client = CreateClient();

        // An attacker wraps excessive list query in an inline fragment attempting to bypass cost analysis
        var payload = new
        {
            query = @"
                query MassiveQueryViaInlineFragment {
                    ... on Query {
                        table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"", first: 5000) { jsonRows }
                    }
                }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("QUERY_TOO_COMPLEX");
    }

    [Fact]
    public async Task CrossTenantSpoofing_WhenHeaderDiffersFromClaim_Returns403Forbidden()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-alpha");
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-bravo");

        var payload = new
        {
            query = "{ __typename }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var content = await response.Content.ReadAsStringAsync();
        content.ShouldContain("CROSS_TENANT_ACCESS_FORBIDDEN");
    }

    [Fact]
    public async Task CrossTenantSpoofing_WhenHeaderMatchesClaim_Succeeds()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Tenant", "tenant-alpha");
        client.DefaultRequestHeaders.Add("X-Tenant-ID", "tenant-alpha");

        var payload = new
        {
            query = "{ __typename }"
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task QueryCostAnalyzer_VariableDefaultBypassAttempt_WithExcessiveRuntimeVariable_IsBlocked()
    {
        var client = CreateClient();

        // SG-10: An attacker declares default $n = 1 (static cost ~10 passes document validation),
        // but runtime variables provide n = 5000 (exceeds complexity budget 250).
        var payload = new
        {
            query = @"
                query GetInvoicesWithDefault($n: Int = 1) {
                    table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"", first: $n) { jsonRows }
                }",
            variables = new { n = 5000 }
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        content.ShouldContain("QUERY_COST_QUOTA_EXCEEDED");
    }

    [Fact]
    public async Task QueryCostAnalyzer_VariableDefault_WithinBudgetRuntimeVariable_Succeeds()
    {
        var client = CreateClient();

        // Normal query within budget using variables
        var payload = new
        {
            query = @"
                query GetInvoicesWithinBudget($n: Int = 1) {
                    table(domain: ""finance"", schema: ""dbo"", name: ""finance_table_1"", first: $n) { jsonRows }
                }",
            variables = new { n = 3 }
        };

        var response = await client.PostAsJsonAsync("/graphql", payload);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.ShouldNotContain("QUERY_COST_QUOTA_EXCEEDED");
        content.ShouldNotContain("QUERY_TOO_COMPLEX");
        content.ShouldNotContain("GRAPHQL_VALIDATION_FAILED"); // RDF-S4: the rewritten query resolves against the schema
    }

    [Fact]
    public async Task CatalogRootField_OverCostLimit_ReadableTableAndColumns_ReturnsQueryTooComplexWithoutNumbers()
    {
        using var host = CreateCatalogAccessHost();
        using var client = CreateClientFor(host);

        var response = await client.PostAsJsonAsync("/graphql", new
        {
            query = "{ finance_dbo_finance_table_1(first: 200) { id name } }"
        });

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var errors = doc.RootElement.GetProperty("errors");
        errors.GetArrayLength().ShouldBe(1, content);

        var error = errors[0];
        error.GetProperty("message").GetString().ShouldBe(ErrorSanitizingFilter.ComplexMessage);
        error.GetProperty("extensions").GetProperty("code").GetString().ShouldBe("QUERY_TOO_COMPLEX");
        error.GetProperty("extensions").EnumerateObject().Select(p => p.Name).ShouldBe(["code"]);
        content.ShouldNotContain("200");
        content.ShouldNotContain("250");
    }

    [Theory]
    [InlineData("{ finance_dbo_finance_table_1(first: 200) { id unknown_column } }")]
    [InlineData("{ sales_dbo_sales_table_1(first: 200) { id name } }")]
    [InlineData("{ finance_dbo_finance_table_1(first: 200) { id email } }")]
    public async Task CatalogRootField_OverCostLimit_UnknownOrUnauthorizedField_ReturnsGraphQlValidationFailed(string query)
    {
        using var host = CreateCatalogAccessHost();
        using var client = CreateClientFor(host);

        var response = await client.PostAsJsonAsync("/graphql", new { query });

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var errors = doc.RootElement.GetProperty("errors");
        errors.GetArrayLength().ShouldBe(1, content);

        var error = errors[0];
        error.GetProperty("message").GetString().ShouldBe(ErrorSanitizingFilter.ValidationMessage);
        error.GetProperty("extensions").GetProperty("code").GetString().ShouldBe(ErrorSanitizingFilter.ValidationCode);
        content.ShouldNotContain("QUERY_TOO_COMPLEX");
    }

    private WebApplicationFactory<Program> CreateCatalogAccessHost() =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                var access = Substitute.For<ITableAccessResolver>();
                access.ResolveTableAccessAsync(
                        Arg.Any<ClaimsPrincipal?>(),
                        Arg.Any<TableIdentifier>(),
                        Arg.Any<IReadOnlyList<string>?>(),
                        Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
                        Arg.Any<CancellationToken>())
                    .Returns(call =>
                    {
                        var principal = call.ArgAt<ClaimsPrincipal?>(0) ?? new ClaimsPrincipal();
                        var id = call.ArgAt<TableIdentifier>(1);
                        var meta = new TableMetadata
                        {
                            Identifier = id,
                            Table = new Table { IsActive = true, SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, SourceType = "SqlServer" },
                            Columns =
                            [
                                new TableColumn { ColumnName = "id", DataType = "int" },
                                new TableColumn { ColumnName = "name", DataType = "varchar" },
                                new TableColumn { ColumnName = "amount", DataType = "decimal" },
                                new TableColumn { ColumnName = "email", DataType = "varchar" },
                                new TableColumn { ColumnName = "created_at", DataType = "datetime" },
                            ]
                        };
                        if (string.Equals(id.Domain, "finance", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(id.Schema, "dbo", StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(id.TableName, "finance_table_1", StringComparison.OrdinalIgnoreCase))
                        {
                            return new ResolvedTableAccess(
                                meta,
                                TableAccessDecision.Allowed(
                                    id,
                                    new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                                    {
                                        ["id"] = ColumnAccessLevel.Clear,
                                        ["name"] = ColumnAccessLevel.Clear,
                                        ["amount"] = ColumnAccessLevel.Clear,
                                        ["email"] = ColumnAccessLevel.Deny,
                                        ["created_at"] = ColumnAccessLevel.Clear,
                                    },
                                    null,
                                    hasUnconstrainedColumnAllow: false),
                                new TenantId("T1"),
                                new Sid("S-1-5-21-test-analyst"),
                                principal);
                        }

                        return new ResolvedTableAccess(
                            meta,
                            TableAccessDecision.Denied(id, ["Access denied."]),
                            new TenantId("T1"),
                            new Sid("S-1-5-21-test-analyst"),
                            principal);
                    });

                services.RemoveAll<ITableAccessResolver>();
                services.AddSingleton(access);
            });
        });

    private static HttpClient CreateClientFor(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-test-analyst");
        return client;
    }
}
