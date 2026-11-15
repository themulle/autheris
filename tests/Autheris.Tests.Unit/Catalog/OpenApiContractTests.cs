namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.External;
using Autheris.Application.Governance.Grants.Records;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

[Trait("Category", "Security")]
public sealed class OpenApiContractTests
{
    private const string EmbeddedSpecName = "Autheris.Api.Contracts.autheris-external-v1.openapi.json";

    [Fact]
    public async Task Document_Matches_Mapped_Routes_And_Methods()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var endpoints = await harness.GetAllExternalV1RouteEndpointsAsync();

        var actualOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var endpoint in endpoints)
        {
            var raw = endpoint.RoutePattern.RawText ?? string.Empty;
            var normalizedPath = NormalizeRouteTemplate(raw);
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            foreach (var method in methods)
            {
                actualOperations.Add($"{method.ToUpperInvariant()} {normalizedPath}");
            }
        }

        using var doc = LoadEmbeddedSpec();
        var documentedOperations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = doc.RootElement.GetProperty("paths");
        foreach (var pathProp in paths.EnumerateObject())
        {
            foreach (var methodProp in pathProp.Value.EnumerateObject())
            {
                documentedOperations.Add($"{methodProp.Name.ToUpperInvariant()} {pathProp.Name}");
            }
        }

        Assert.Equal(
            documentedOperations.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList(),
            actualOperations.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList());
    }

    [Fact]
    public void Dto_Schemas_Match_Document()
    {
        using var doc = LoadEmbeddedSpec();
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        var dtoTypes = new[]
        {
            typeof(ExternalCatalogOwnerDto),
            typeof(ExternalCatalogCallerStateDto),
            typeof(ExternalCatalogColumnDto),
            typeof(ExternalCatalogObjectDto),
            typeof(ExternalCatalogPageDto),
            typeof(ExternalCatalogChangeDto),
            typeof(ExternalCatalogChangesResponseDto),
            typeof(ConsentRequestDto),
            typeof(ConsentGrantDto),
            typeof(CreateConsentRequestBody),
            typeof(ConsentDecisionBody),
            typeof(RevokeConsentGrantBody),
            typeof(GrantRecordVerifyRequest)
        };

        foreach (var dtoType in dtoTypes)
        {
            schemas.TryGetProperty(dtoType.Name, out var schemaElement)
                .Should().BeTrue($"OpenAPI components.schemas must define {dtoType.Name}");

            var docProperties = schemaElement.GetProperty("properties").EnumerateObject()
                .Select(p => p.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            var dtoProperties = dtoType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name))
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(dtoProperties, docProperties);
        }
    }

    [Fact]
    public async Task ExternalOpenApiEndpoint_ServesEmbeddedDocument_WithETagAnd304()
    {
        await using var harness = ExternalCatalogTestHarness.Create();
        var caller = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "catalog-sync");

        var first = await harness.GetExternalOpenApiSpecAsync(caller);
        first.StatusCode.Should().Be(StatusCodes.Status200OK);
        var etag = first.Headers.ETag.ToString();
        Assert.StartsWith("\"", etag);
        Assert.EndsWith("\"", etag);

        var json = first.ReadJson();
        json.GetProperty("openapi").GetString().Should().Be("3.0.3");
        Assert.True(json.GetProperty("paths").EnumerateObject().Any());

        var cached = await harness.GetExternalOpenApiSpecAsync(caller, ifNoneMatch: etag);
        cached.StatusCode.Should().Be(StatusCodes.Status304NotModified);

        var unauth = await harness.GetExternalOpenApiSpecAsync(new System.Security.Claims.ClaimsPrincipal());
        unauth.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    private static JsonDocument LoadEmbeddedSpec()
    {
        var assembly = typeof(ExternalCatalogApiEndpoints).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedSpecName)
            ?? throw new InvalidOperationException($"Missing embedded OpenAPI resource '{EmbeddedSpecName}'.");
        using var reader = new StreamReader(stream);
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private static string NormalizeRouteTemplate(string raw)
    {
        var withLeadingSlash = raw.StartsWith('/') ? raw : "/" + raw;
        return Regex.Replace(withLeadingSlash, @"\{([^}:]+):[^}]+\}", "{$1}");
    }
}
