namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Endpoints;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Shouldly;
using Xunit;

/// <summary>F-AUTH-DX route registration verification on minimal WebApplication host.</summary>
public sealed class DevExperienceEndpointIntegrationTests
{
    private static GatewayOptions CreateOptions() => new()
    {
        GovernanceDb = new GovernanceDbOptions { ConnectionString = "Data Source=secret-conn-string.db" },
        Authentication = new AuthenticationOptions
        {
            RequireKerberosOnly = false,
            BasicAuth = new BasicAuthOptions
            {
                Enabled = true,
                Session = new BasicAuthSessionOptions { Enabled = true, AllowedEnvironments = ["Development"] },
                Users =
                [
                    new BasicAuthUserConfig { Username = "owner", Password = "s3cret-pw", Sid = "S-1-5-21-1000-1000-1000-1001", Roles = ["GovernanceAdmin"] },
                    new BasicAuthUserConfig { Username = "analyst-b", Password = "$pbkdf2$600000$x", TenantId = "tenant-b", Roles = ["Consumer"] }
                ]
            }
        }
    };

    private static IReadOnlyList<string> MappedDevRoutes(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        using var app = builder.Build();
        app.MapDevEndpoints(CreateOptions());
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(e => e.RoutePattern.RawText ?? string.Empty)
            .Where(p => p.StartsWith("/api/dev", StringComparison.Ordinal))
            .ToList();
    }

    [Fact]
    public void DevRoutes_AreMappedInDevelopment()
    {
        MappedDevRoutes("Development").ShouldBe(["/api/dev/personas", "/api/dev/info", "/api/dev/login/{persona}"], ignoreOrder: true);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void DevRoutes_AreNotMappedOutsideDevelopment(string environment)
    {
        MappedDevRoutes(environment).ShouldBeEmpty();
    }
}
