using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

namespace Autheris.Tests.Integration;

/// <summary>
/// Issue #447 (AC-ATR-11, SEC-ATR-12, SEC-ATR-13): every mapped route either declares its ingress transport
/// (<see cref="IngressTransportMetadata"/>) or is listed as a reviewed exemption (route lines of
/// tests/Autheris.Tests.Architecture/Baselines/ingress-exemptions.txt). A new route without either fails here, so a new
/// data-reaching endpoint cannot ship unstamped (the gate would deny it with TransportUnset).
/// </summary>
[Trait("Category", "Security")]
public sealed class IngressTransportCoverageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IngressTransportCoverageTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("IngressTransportCoverageTests"));
        });
    }

    [Fact]
    public void SEC_ATR_12_SEC_ATR_13_AllEndpoints_HaveIngressMetadataOrReviewedExemption()
    {
        var exemptions = ReadRouteExemptions();

        // Creating a client starts the host so that all endpoint data sources are registered.
        using var client = _factory.CreateClient();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.ShouldNotBeEmpty();

        var unstamped = endpoints
            .Where(e => e.Metadata.GetMetadata<IngressTransportMetadata>() is null)
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(route => !exemptions.Any(x => Matches(x, route)))
            .Order(StringComparer.Ordinal)
            .ToList();

        unstamped.ShouldBeEmpty(
            "Routes without WithIngressTransport(...) must be added to Baselines/ingress-exemptions.txt with a reviewed reason "
            + "(security approval required, SEC-ATR-13), or stamped:\n" + string.Join("\n", unstamped));

        // No dead exemption: every exemption pattern still matches a mapped unstamped route (keeps the list honest).
        var staleExemptions = exemptions
            .Where(x => !endpoints.Any(e => e.Metadata.GetMetadata<IngressTransportMetadata>() is null && Matches(x, e.RoutePattern.RawText ?? string.Empty)))
            .ToList();
        staleExemptions.ShouldBeEmpty("Remove exemptions that no longer match an unstamped route:\n" + string.Join("\n", staleExemptions));
    }

    private static bool Matches(string pattern, string route) =>
        pattern.EndsWith('*')
            ? route.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase)
            : string.Equals(pattern, route, StringComparison.OrdinalIgnoreCase);

    private static List<string> ReadRouteExemptions()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
        {
            dir = dir.Parent;
        }

        dir.ShouldNotBeNull("Repository root (Autheris.sln) not found.");
        var path = Path.Combine(dir.FullName, "tests", "Autheris.Tests.Architecture", "Baselines", "ingress-exemptions.txt");
        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("route ", StringComparison.Ordinal))
            .Select(l => l["route ".Length..].Split('|')[0].Trim())
            .ToList();
    }
}
