namespace Autheris.Tests.Unit.Api;

using System;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Application.Sql;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

/// <summary>The OpenAPI description served at GET /api/v1/sql/openapi.json.</summary>
public sealed class WebSqlOpenApiSpecTests
{
    private static GatewayOptions Gateway(WebSqlOptions? webSql = null) =>
        new() { WebSql = webSql ?? new WebSqlOptions { Enabled = true } };

    internal static JsonNode Spec(GatewayOptions? options = null) =>
        JsonSerializer.SerializeToNode(WebSqlEndpoints.BuildOpenApiSpec(options ?? Gateway()))!;

    internal static JsonNode Schema(JsonNode spec, string name) => spec["components"]!["schemas"]![name]!;

    [Fact]
    public void Spec_RequestSchema_DescribesParametersListsPageAndNext()
    {
        var request = Schema(Spec(), "WebSqlRequest")["properties"]!;

        request["parameters"]!["description"]!.GetValue<string>().ShouldContain("list parameter");
        request["parameters"]!["additionalProperties"]!["nullable"]!.GetValue<bool>().ShouldBeTrue();
        request["parameters"]!["additionalProperties"]!["oneOf"]!.AsArray().Any(n => n!["type"]!.GetValue<string>() == "array").ShouldBeTrue();
        request["page"]!["$ref"]!.GetValue<string>().ShouldEndWith("WebSqlPageRequest");
        request["next"]!["$ref"]!.GetValue<string>().ShouldEndWith("WebSqlNext");
    }

    [Fact]
    public void Spec_ResultSchema_DescribesTruncatedPageNextPagingUnavailableAndIndexHints()
    {
        var result = Schema(Spec(), "WebSqlResult");
        var properties = result["properties"]!;

        foreach (var member in new[] { "columns", "rows", "rowCount", "truncated", "page", "next", "pagingUnavailable", "resultCache", "indexHints" })
        {
            properties[member].ShouldNotBeNull();
        }

        var column = properties["columns"]!["items"]!["properties"]!;
        foreach (var member in new[] { "name", "type", "nullable", "masked" })
        {
            column[member].ShouldNotBeNull();
        }

        properties["indexHints"]!["items"]!["properties"]!["reason"]!["example"]!.GetValue<string>().ShouldBe("NO_USABLE_INDEX");
        result["required"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldBe(["columns", "rows", "rowCount", "truncated"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Spec_ErrorResponses_AreProblemJsonWithCodeExtension()
    {
        var responses = Spec()["paths"]!["/api/v1/sql"]!["post"]!["responses"]!.AsObject();

        foreach (var (status, response) in responses)
        {
            if (status == "200" || status == "401" || status == "406")
            {
                continue;
            }

            var content = response!["content"]!.AsObject();
            content.Select(c => c.Key).ShouldBe(["application/problem+json"]);
            content["application/problem+json"]!["schema"]!["$ref"]!.GetValue<string>().ShouldEndWith("Problem");
        }

        var problem = Schema(Spec(), "Problem");
        problem["properties"]!["code"].ShouldNotBeNull();
        problem["properties"]!["error"].ShouldBeNull();
        problem["properties"]!["message"].ShouldBeNull();
        responses["429"]!["headers"]!["Retry-After"].ShouldNotBeNull();
    }

    [Fact]
    public void Spec_PagingUnavailableEnum_MatchesExecutorValues()
    {
        var values = Schema(Spec(), "WebSqlPagingUnavailable")["enum"]!.AsArray().Select(n => n!.GetValue<string>());

        values.OrderBy(v => v, StringComparer.Ordinal).ShouldBe(new[] { "caller_not_bindable", "federated", "offset_limit", "order_by_required", "statement_has_limit" });
    }

    [Fact]
    public void Spec_Limits_FollowConfiguredOptions()
    {
        var spec = Spec(Gateway(new WebSqlOptions
        {
            Enabled = true,
            MaxAllowedRows = 4242,
            MaxListParameterValues = 77,
            Paging = new WebSqlPagingOptions { TokenLifetimeMinutes = 8 }
        }));

        Schema(spec, "WebSqlPageRequest")["properties"]!["size"]!["maximum"]!.GetValue<int>().ShouldBe(4242);
        Schema(spec, "WebSqlRequest")["properties"]!["parameters"]!["additionalProperties"]!["oneOf"]!.AsArray()
            .First(n => n!["type"]!.GetValue<string>() == "array")!["maxItems"]!.GetValue<int>().ShouldBe(77);
        Schema(spec, "WebSqlNext")["description"]!.GetValue<string>().ShouldContain("8 minutes");
    }

    [Fact]
    public void Spec_Example_IsTheGuideExample()
    {
        var request = Schema(Spec(), "WebSqlRequest")["properties"]!;

        request["sql"]!["example"]!.GetValue<string>().ShouldBe(WebSqlGuideExample.Sql);
        request["parameters"]!["example"]!.ToJsonString().ShouldBe(JsonNode.Parse(WebSqlGuideExample.ParametersJson)!.ToJsonString());
    }

    [Fact]
    public void Spec_SerializesAsOpenApi303Json()
    {
        var json = JsonSerializer.Serialize(WebSqlEndpoints.BuildOpenApiSpec(Gateway()));
        var spec = JsonNode.Parse(json)!;

        spec["openapi"]!.GetValue<string>().ShouldBe("3.0.3");
        spec["paths"]!["/api/v1/sql"]!["post"]!["operationId"]!.GetValue<string>().ShouldBe("executeGovernedWebSql");
        spec["components"]!["schemas"]!.AsObject().Select(s => s.Key)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ShouldBe(new[] { "Problem", "WebSqlNext", "WebSqlPage", "WebSqlPageRequest", "WebSqlPagingUnavailable", "WebSqlRequest", "WebSqlResult", "WebSqlView", "WebSqlViewCondition" });
    }

    [Fact]
    [Trait("Category", "Security")]
    public void GetOpenApiSpec_WhenWebSqlDisabledOrNull_Returns404()
    {
        WebSqlEndpoints.GetOpenApiSpec(null).ShouldBeOfType<NotFound>();
        WebSqlEndpoints.GetOpenApiSpec(new GatewayOptions { WebSql = new WebSqlOptions { Enabled = false } }).ShouldBeOfType<NotFound>();
        WebSqlEndpoints.GetOpenApiSpec(Gateway()).ShouldNotBeOfType<NotFound>();
    }
}

/// <summary>Disclosure and auth properties of the WebSQL OpenAPI document.</summary>
public sealed class WebSqlOpenApiSecurityTests
{
    private static readonly string[] ReservedPrefixes = ["__gql_", "__param_", "__rls_ks", "p_rls_", "p_pd_", "gw_tf_"];

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_OpenApi_GivenSentinelDefaultDataSourceName_WhenBuildSpec_ThenSentinelAbsent()
    {
        var options = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "sentinel-default-source", AllowedDataSources = ["sentinel-allowed-source"] } };

        var json = WebSqlOpenApiSpecTests.Spec(options).ToJsonString();

        json.ShouldNotContain("sentinel-default-source");
        json.ShouldNotContain("sentinel-allowed-source");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_OpenApi_WhenBuildSpec_ThenNoReservedPrefixNames()
    {
        var json = WebSqlOpenApiSpecTests.Spec().ToJsonString();

        foreach (var prefix in ReservedPrefixes)
        {
            json.ShouldNotContain(prefix);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_OpenApi_WhenBuildSpec_ThenProblemJsonSchemaOnlyAllowListedMembers()
    {
        var members = WebSqlOpenApiSpecTests.Schema(WebSqlOpenApiSpecTests.Spec(), "Problem")["properties"]!.AsObject().Select(p => p.Key);
        var problemFields = new[] { "type", "title", "status", "detail", "instance", "code", "correlationId", "traceId" };

        foreach (var member in members.Except(problemFields))
        {
            ApiProblems.AllowedExtensionKeys.ShouldContain(member);
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_OpenApi_GivenEndpointMetadata_WhenInspected_ThenRequiresAuthorizationAndNoAllowAnonymous()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "src", "Autheris.Api", "Endpoints", "WebSqlEndpoints.cs"));
        var start = source.IndexOf("\"/api/v1/sql/openapi.json\"", StringComparison.Ordinal);
        start.ShouldBeGreaterThan(0);
        var statement = source[start..source.IndexOf(';', start)];

        statement.ShouldContain(".RequireAuthorization()");
        statement.ShouldNotContain("AllowAnonymous");
    }

    private static string RepoRoot()
    {
        var current = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(current.FullName, "Autheris.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Autheris.sln not found above " + AppContext.BaseDirectory);
    }
}

/// <summary>The shared WebSQL example constants.</summary>
public sealed class WebSqlGuideExampleSecurityTests
{
    [Fact]
    [Trait("Category", "Security")]
    public void Issue1201_Example_WhenInspected_ThenOnlyConstsNoLiteralsNoMaskedColumnNames()
    {
        var type = typeof(WebSqlGuideExample);
        type.GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance)
            .ShouldAllBe(f => f.IsLiteral);
        type.GetProperties().ShouldBeEmpty();

        WebSqlGuideExample.Sql.ShouldNotContain("'");
        WebSqlGuideExample.Sql.ShouldNotContain("\"");
        foreach (var name in new[] { "email", "iban", "salary" })
        {
            WebSqlGuideExample.Sql.ToLowerInvariant().ShouldNotContain(name);
        }
    }
}
