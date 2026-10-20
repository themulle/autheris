using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Audit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
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

    /// <summary>
    /// FR-03: every route with AuditLevel.Delegated skips the central success audit and relies on its own domain audit.
    /// Each such route must be reviewed and listed here with a justification; a new Delegated route fails CI until then.
    /// Key format: "METHOD pattern" (HTTP methods sorted, joined by ',', or "ANY").
    /// </summary>
    private static readonly Dictionary<string, string> ApprovedDelegatedRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["POST /api/v1/jobs/query"] =
            "AsyncQueryJobManager.SubmitJobAsync writes the ASYNC_JOB_SUBMITTED ALLOW record fail-closed before enqueueing; " +
            "denials, failures and exceptions are finalized by AccessAuditMiddleware.",
        ["GET /api/v1/jobs/{jobId}/result"] =
            "AsyncQueryJobManager.GetJobResultStreamAsync writes ASYNC_JOB_DOWNLOADED fail-closed before returning a stream; " +
            "IDOR attempts record ASYNC_JOB_ACCESS_DENIED; other failures are finalized by AccessAuditMiddleware.",
        ["DELETE /api/v1/jobs/{jobId}"] =
            "AsyncQueryJobManager.CancelJobAsync writes ASYNC_JOB_CANCELLED; IDOR attempts record ASYNC_JOB_ACCESS_DENIED; " +
            "other failures are finalized by AccessAuditMiddleware.",
        ["POST /api/admin/tokens/revoke"] =
            "TokenRevocationEndpoints writes the TOKEN_REVOKED record itself after revocation; " +
            "403/401/errors are finalized by AccessAuditMiddleware.",
        ["POST /api/admin/tenant-registry/{tenantId}/resolve-collision"] =
            "TenantRegistryAdminEndpoints writes the TENANT_COLLISION_RESOLVED record itself after the resolution; " +
            "403/401/errors are finalized by AccessAuditMiddleware.",
        ["ANY /graphql/{**slug}"] =
            "GraphQL data access is audited by GatewayExecutionService, which records a per-table ALLOW/DENY decision for every table a query touches; " +
            "HTTP-level denials (401/403/429/5xx) and exceptions are finalized by AccessAuditMiddleware. " +
            "Residual gap: parse, validation, depth and cost rejections and introspection return HTTP 200 with GraphQL errors, " +
            "are not audited, and read no data.",
    };

    public AuditEndpointCoverageTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
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

    private static string RouteKey(RouteEndpoint ep)
    {
        var methods = ep.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        var method = methods is { Count: > 0 } ? string.Join(',', methods.OrderBy(m => m, StringComparer.Ordinal)) : "ANY";
        return $"{method} {ep.RoutePattern.RawText ?? "/"}";
    }

    [Fact]
    public void DelegatedAuditRoutes_MustBeExplicitlyReviewedAndJustified()
    {
        var delegated = GetRouteEndpoints()
            .Where(ep => ep.Metadata.GetMetadata<AuditPolicy>()?.Level == AuditLevel.Delegated)
            .Select(RouteKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var unreviewed = delegated
            .Where(k => !ApprovedDelegatedRoutes.TryGetValue(k, out var why) || string.IsNullOrWhiteSpace(why))
            .ToList();

        unreviewed.ShouldBeEmpty(
            $"FR-03 Violation: Delegated audit routes without an approved justification (add to {nameof(ApprovedDelegatedRoutes)} after review):\n{string.Join("\n", unreviewed)}");
    }
}
