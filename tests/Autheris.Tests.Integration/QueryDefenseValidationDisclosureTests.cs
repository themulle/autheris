using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>
/// Validation-error disclosure over /graphql (#1188). Reuses the catalog hosts of the stress fixture but is not a
/// Stress class, so the Security cases run on every pull request.
/// </summary>
public class QueryDefenseValidationDisclosureTests : IClassFixture<QueryDefenseStressTests.HostFixture>
{
    private readonly QueryDefenseStressTests.HostFixture _fixture;

    public QueryDefenseValidationDisclosureTests(QueryDefenseStressTests.HostFixture fixture) => _fixture = fixture;

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnknownColumn_OnFullyVisibleTable_ReturnsUnknownFieldWithStatus400()
    {
        using var client = CreateClientFor(_fixture.FullyVisibleHost);

        var response = await client.PostAsJsonAsync("/graphql", new { query = "{ finance_dbo_finance_table_1(first: 5) { id unknown_column } }" });

        var content = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(System.Net.HttpStatusCode.BadRequest, content);
        using var doc = JsonDocument.Parse(content);
        var error = doc.RootElement.GetProperty("errors")[0];
        var extensions = error.GetProperty("extensions");
        extensions.GetProperty("code").GetString().ShouldBe("UNKNOWN_FIELD");
        extensions.GetProperty("field").GetString().ShouldBe("unknown_column");
        error.GetProperty("locations").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnknownColumn_OnPartlyDeniedTable_DeniedTable_AndUnknownRoot_AreByteIdentical()
    {
        using var client = CreateClientFor(_fixture.CatalogAccessHost);
        var bodies = new List<(System.Net.HttpStatusCode Status, string Body)>();
        foreach (var query in new[]
        {
            "{ finance_dbo_finance_table_1(first: 5) { id unknown_column } }",
            "{ sales_dbo_sales_table_1(first: 5) { id unknown_column } }",
            "{ no_such_table_1188(first: 5) { id unknown_column } }",
        })
        {
            var response = await client.PostAsJsonAsync("/graphql", new { query });
            bodies.Add((response.StatusCode, await response.Content.ReadAsStringAsync()));
        }

        bodies[0].Body.ShouldContain("GRAPHQL_VALIDATION_FAILED");
        bodies[1].ShouldBe(bodies[0]);
        bodies[2].ShouldBe(bodies[0]);
    }

    private static HttpClient CreateClientFor(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("GraphQL-Preflight", "1");
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", "S-1-5-21-test-analyst");
        return client;
    }
}
