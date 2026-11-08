namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autheris.Api.Endpoints;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;
using Autheris.Tests.Integration.Governance;

[Trait("Category", "Security")]
public sealed class IngressExemptionTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public IngressExemptionTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("IngressExemptionTests"));
        });
    }

    [Fact]
    public void Exemption_list_contains_only_existing_routes()
    {
        var exemptions = ReadRouteExemptions();
        using var client = _factory.CreateClient();
        var endpoints = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.ShouldNotBeEmpty();

        var staleExemptions = exemptions
            .Where(x => !endpoints.Any(e => e.Metadata.GetMetadata<IngressTransportMetadata>() is null && Matches(x, e.RoutePattern.RawText ?? string.Empty)))
            .ToList();

        staleExemptions.ShouldBeEmpty("Exemptions must reference only existing unstamped routes:\n" + string.Join("\n", staleExemptions));
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
