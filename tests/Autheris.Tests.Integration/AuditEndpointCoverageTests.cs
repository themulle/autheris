using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class AuditEndpointCoverageTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    private static readonly HashSet<string> ApprovedExemptionPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/health",
        "/metrics",
        "/",
        "/getting-started",
        "/docs",
        "/ui/bcp",
        "/ui/swagger",
        "/odata/v4/$swagger",
        "/swagger"
    };

    public AuditEndpointCoverageTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-auditcov-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting("Gateway:GraphQL:EnableBananaCakePop", "true");
            builder.UseSetting("Gateway:WebSql:Enabled", "true");
            builder.UseSetting("Gateway:Backstage:Enabled", "true");
            builder.UseSetting("Gateway:DevPortal:Enabled", "true");
        });
    }

    private List<RouteEndpoint> GetRouteEndpoints()
    {
        using var client = _factory.CreateClient();
        var dataSource = _factory.Services.GetRequiredService<EndpointDataSource>();
        return dataSource.Endpoints.OfType<RouteEndpoint>().ToList();
    }

    [Fact]
    public void All_RouteEndpoints_MustHave_AuditPolicy_Or_AuditExemption()
    {
        var endpoints = GetRouteEndpoints();
        var unclassified = new List<string>();

        foreach (var ep in endpoints)
        {
            var policy = ep.Metadata.GetMetadata<AuditPolicy>();
            var exemption = ep.Metadata.GetMetadata<AuditExemption>();
            var pattern = ep.RoutePattern.RawText ?? "(empty)";

            if (policy == null && exemption == null)
            {
                unclassified.Add(pattern);
            }
        }

        unclassified.ShouldBeEmpty(
            $"L-9 Violation: Found unclassified endpoints without AuditPolicy or AuditExemption:\n{string.Join("\n", unclassified.Distinct())}");
    }

    [Fact]
    public void AuditExemptions_MustOnlyBe_ApprovedOperationalRoutes()
    {
        var endpoints = GetRouteEndpoints();
        var disallowedExemptions = new List<string>();

        foreach (var ep in endpoints)
        {
            var exemption = ep.Metadata.GetMetadata<AuditExemption>();
            if (exemption != null)
            {
                var pattern = ep.RoutePattern.RawText ?? "/";
                bool isApproved = ApprovedExemptionPrefixes.Any(p =>
                    p == "/" ? pattern == "/" : pattern.StartsWith(p, StringComparison.OrdinalIgnoreCase));

                if (!isApproved)
                {
                    disallowedExemptions.Add($"{pattern} (Justification: {exemption.Justification})");
                }
            }
        }

        disallowedExemptions.ShouldBeEmpty(
            $"Found unapproved AuditExemptions outside the allowlist:\n{string.Join("\n", disallowedExemptions)}");
    }
}
