namespace Autheris.Tests.Unit.Telemetry;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration.Validation;
using Autheris.Api.Middleware;
using Autheris.Application.Runtime.ResourceGroups;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ResourceGroupScopeTierTests
{
    [Theory]
    [InlineData("/graphql", "scp", "Agent.Read", ResourceGroupTier.AutonomousAgents)]
    [InlineData("/api/v1/sql", "scp", "Agent.Read", ResourceGroupTier.AutonomousAgents)]
    [InlineData("/graphql", "scp", "api://autheris/Agent.Read", ResourceGroupTier.AutonomousAgents)]
    [InlineData("/api/v1/sql", "scope", "Agent.Read", ResourceGroupTier.AutonomousAgents)]
    [InlineData("/graphql", "scp", "Data.Read", ResourceGroupTier.Interactive)]
    [InlineData("/api/v1/sql", "scp", "Data.Read", ResourceGroupTier.Interactive)]
    public async Task InvokeAsync_WithScopeTiers_RoutesAgentTokenToConfiguredTierAndRecordsTelemetry(
        string path,
        string scopeClaimType,
        string scopeClaimValue,
        ResourceGroupTier expectedTier)
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                ScopeTiers = new Dictionary<string, ResourceGroupTier>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Agent.Read"] = ResourceGroupTier.AutonomousAgents
                }
            }
        });

        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);
        var auditContext = new AuditContext();
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Path = path;
        httpContext.Features.Set(auditContext);
        httpContext.User = CreateUser(
            new Claim("tid", "tenant-a"),
            new Claim(scopeClaimType, scopeClaimValue));

        using var activity = new Activity("test-resource-group").Start();
        ResourceGroupTierMetrics? activeDuringRequest = null;

        var middleware = new ResourceGroupMiddleware(
            _ =>
            {
                activeDuringRequest = manager.GetMetrics().Tiers.Single(t => t.Tier == expectedTier);
                return Task.CompletedTask;
            },
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        await middleware.InvokeAsync(httpContext);

        httpContext.Response.Headers["X-Resource-Group-Tier"].ToString().ShouldBe(expectedTier.ToString());
        auditContext.AdditionalDetails["resourceGroupTier"].ShouldBe(expectedTier.ToString());
        activity.GetTagItem("autheris.resource_group.tier").ShouldBe(expectedTier.ToString());
        activeDuringRequest.ShouldNotBeNull();
        activeDuringRequest!.ActiveConcurrency.ShouldBe(1);
    }

    [Fact]
    public async Task InvokeAsync_AgentTokenHitsAutonomousAgentsLimitsWhenPoolExhausted()
    {
        var options = Options.Create(new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                Interactive = new ResourceGroupTierConfigOptions(MaxConcurrency: 10, MaxQueueDepth: 10, TimeoutSeconds: 5),
                AutonomousAgents = new ResourceGroupTierConfigOptions(MaxConcurrency: 1, MaxQueueDepth: 0, TimeoutSeconds: 1),
                ScopeTiers = new Dictionary<string, ResourceGroupTier>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Agent.Read"] = ResourceGroupTier.AutonomousAgents
                }
            }
        });

        using var manager = new ResourceGroupManager(options, NullLogger<ResourceGroupManager>.Instance);
        var firstAudit = new AuditContext();
        var secondAudit = new AuditContext();

        var firstContext = new DefaultHttpContext();
        firstContext.Request.Path = "/graphql";
        firstContext.Features.Set(firstAudit);
        firstContext.User = CreateUser(new Claim("tid", "tenant-a"), new Claim("scp", "Agent.Read"));

        var secondContext = new DefaultHttpContext();
        secondContext.Request.Path = "/api/v1/sql";
        secondContext.Features.Set(secondAudit);
        secondContext.User = CreateUser(new Claim("tid", "tenant-b"), new Claim("scp", "Agent.Read"));

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstMiddleware = new ResourceGroupMiddleware(
            async _ =>
            {
                entered.TrySetResult();
                await release.Task;
            },
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var secondMiddleware = new ResourceGroupMiddleware(
            _ => Task.CompletedTask,
            manager,
            options,
            NullLogger<ResourceGroupMiddleware>.Instance);

        var firstTask = firstMiddleware.InvokeAsync(firstContext);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await secondMiddleware.InvokeAsync(secondContext);
        secondContext.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        secondContext.Response.Headers["X-Resource-Group-Tier"].ToString().ShouldBe("AutonomousAgents");
        secondAudit.AdditionalDetails["resourceGroupTier"].ShouldBe("AutonomousAgents");

        release.SetResult();
        await firstTask;
        firstContext.Response.Headers["X-Resource-Group-Tier"].ToString().ShouldBe("AutonomousAgents");
        firstAudit.AdditionalDetails["resourceGroupTier"].ShouldBe("AutonomousAgents");
    }

    [Fact]
    public void ClassifyWorkloadTier_SelectsLeastPrivilegedAmongPathScopesAndHeader()
    {
        var options = new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                ScopeTiers = new Dictionary<string, ResourceGroupTier>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Data.Read"] = ResourceGroupTier.Interactive,
                    ["Agent.Read"] = ResourceGroupTier.AutonomousAgents,
                    ["Bulk.Export"] = ResourceGroupTier.BulkAnalytics
                }
            }
        };

        var multiScopeContext = new DefaultHttpContext();
        multiScopeContext.Request.Path = "/graphql";
        multiScopeContext.User = CreateUser(new Claim("scp", "Data.Read Agent.Read"));
        multiScopeContext.Request.Headers["X-Workload-Tier"] = "Interactive";
        ResourceGroupMiddleware.ClassifyWorkloadTier(multiScopeContext, options)
            .ShouldBe(ResourceGroupTier.AutonomousAgents);

        multiScopeContext.Request.Headers["X-Workload-Tier"] = "BulkAnalytics";
        ResourceGroupMiddleware.ClassifyWorkloadTier(multiScopeContext, options)
            .ShouldBe(ResourceGroupTier.BulkAnalytics);

        var odataAgentContext = new DefaultHttpContext();
        odataAgentContext.Request.Path = "/odata/v4/Orders";
        odataAgentContext.User = CreateUser(new Claim("scp", "Agent.Read"));
        ResourceGroupMiddleware.ClassifyWorkloadTier(odataAgentContext, options)
            .ShouldBe(ResourceGroupTier.BulkAnalytics);

        var combinedBulkScopeContext = new DefaultHttpContext();
        combinedBulkScopeContext.Request.Path = "/api/v1/sql";
        combinedBulkScopeContext.User = CreateUser(new Claim("scp", "Agent.Read Bulk.Export"));
        ResourceGroupMiddleware.ClassifyWorkloadTier(combinedBulkScopeContext, options)
            .ShouldBe(ResourceGroupTier.BulkAnalytics);

        var adminOverrideContext = new DefaultHttpContext();
        adminOverrideContext.Request.Path = "/graphql";
        adminOverrideContext.Request.Headers["X-Workload-Tier"] = "Interactive";
        adminOverrideContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("scp", "Agent.Read"),
                new Claim(ClaimTypes.Role, "GovernanceAdmin")
            ],
            "Bearer"));
        ResourceGroupMiddleware.ClassifyWorkloadTier(adminOverrideContext, options)
            .ShouldBe(ResourceGroupTier.Interactive);
    }

    [Fact]
    public void ClassifyWorkloadTier_WithoutScopeTiersMapping_PreservesPathBehavior()
    {
        var options = new GatewayOptions();
        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";
        context.User = CreateUser(new Claim("scp", "Agent.Read"));

        ResourceGroupMiddleware.ClassifyWorkloadTier(context, options)
            .ShouldBe(ResourceGroupTier.Interactive);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Agent Read", false)]
    [InlineData("/", false)]
    [InlineData("Agent.Read/", false)]
    [InlineData("api://autheris/", false)]
    [InlineData("api://autheris/Agent.Read", false)]
    [InlineData("Agent.Read", true)]
    public void Validator_ChecksScopeTierKeys(string scopeKey, bool expectedValid)
    {
        var validator = new GatewayCoreOptionsValidator(new TestHostEnvironment("Production"));
        var options = new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                ScopeTiers = new Dictionary<string, ResourceGroupTier>
                {
                    [scopeKey] = ResourceGroupTier.AutonomousAgents
                }
            }
        };

        var result = validator.Validate(null, options);
        if (expectedValid)
        {
            result.Succeeded.ShouldBeTrue();
        }
        else
        {
            result.Failed.ShouldBeTrue();
            result.Failures.ShouldContain(f => f.StartsWith("Gateway:ResourceGroups:ScopeTiers", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Validator_RejectsUndefinedResourceGroupTierValue()
    {
        var validator = new GatewayCoreOptionsValidator(new TestHostEnvironment("Production"));
        var options = new GatewayOptions
        {
            ResourceGroups = new ResourceGroupsOptions
            {
                ScopeTiers = new Dictionary<string, ResourceGroupTier>
                {
                    ["Agent.Read"] = (ResourceGroupTier)99
                }
            }
        };

        var result = validator.Validate(null, options);
        result.Failed.ShouldBeTrue();
        result.Failures.ShouldContain(f => f.StartsWith("Gateway:ResourceGroups:ScopeTiers", StringComparison.Ordinal));
    }

    private static ClaimsPrincipal CreateUser(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, "Bearer"));
}
