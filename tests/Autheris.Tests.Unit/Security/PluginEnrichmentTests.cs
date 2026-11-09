namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Identity;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Plugins.Auth;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Plugins.Auth;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>OD-31 / SEC-AZ-14: authentication plugin output is control-plane input only and never part of the data-plane group set.</summary>
[Trait("Category", "Security")]
public sealed class PluginEnrichmentTests
{
    private const string TokenGroup = "S-1-5-21-1-2-3-1100";
    private const string EnrichedGroup = "S-1-5-21-9-9-9-9999";

    private static async Task<SecurityPrincipalContext> RunAsync(PluginRoleEnrichmentResult enrichment, int maxSids = 256)
    {
        var options = new GatewayOptions
        {
            Plugins = new PluginsOptions { Auth = new AuthPluginOptions { Enabled = true, AllowRoleEnrichment = true, MaxEnrichedGroupSidsPerCaller = maxSids } }
        };
        var host = Substitute.For<IAuthPluginHost>();
        host.HasRoleProviders.Returns(true);
        host.EnrichRolesAsync(Arg.Any<PluginSecurityContext>(), Arg.Any<CancellationToken>()).Returns(PluginRoleEnrichmentExecutionResult.Success(enrichment));

        var monitor = Substitute.For<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>();
        monitor.CurrentValue.Returns(options);
        var services = new ServiceCollection();
        services.AddSingleton(monitor);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton(host);

        var identity = new ClaimsIdentity(
        [
            new Claim("sub", "S-1-5-21-1-2-3-1001"),
            new Claim("tenant_id", "tenant-a"),
            new Claim(ClaimTypes.Role, "Analyst"),
            IdentityClaimIssuers.CreateGroupSidClaim(TokenGroup),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete),
            GroupResolutionClaims.CreateMarker()
        ], "Bearer");
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = new ClaimsPrincipal(identity) };

        await new SecurityContextResolutionMiddleware(_ => Task.CompletedTask).InvokeAsync(context);

        return (SecurityPrincipalContext)context.Items[SecurityPrincipalContext.ItemKey]!;
    }

    [Fact]
    public async Task PluginRoleEnrichment_never_grants_data_access()
    {
        var context = await RunAsync(new PluginRoleEnrichmentResult(["DataOwner"], [new Sid(EnrichedGroup)]));

        context.TenantRoles.ShouldNotContain("DataOwner");
        context.HasRole("DataOwner").ShouldBeFalse();
        context.GroupSids.ShouldNotContain(new Sid(EnrichedGroup));
        context.ControlPlaneEnrichment!.Roles.ShouldContain("DataOwner");
    }

    [Fact]
    public async Task PluginEnrichedGroupSid_NeverMatchesGroupGrant()
    {
        var context = await RunAsync(new PluginRoleEnrichmentResult(null, [new Sid(EnrichedGroup)]));

        // The set that data grants, denies, row filters and masks match against holds the token groups only.
        context.GroupSids.Select(g => g.Value).ShouldBe([TokenGroup]);
        context.ControlPlaneEnrichment!.GroupSids.ShouldContain(new Sid(EnrichedGroup));
    }

    [Fact]
    public async Task PluginEnrichedGroupSid_NotInGroupSids()
    {
        var context = await RunAsync(new PluginRoleEnrichmentResult(null, [new Sid(EnrichedGroup)]));

        context.GroupSids.ShouldNotContain(new Sid(EnrichedGroup));
        context.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Complete);
    }

    [Fact]
    public async Task Enriched_non_canonical_sid_is_dropped_and_counted()
    {
        var context = await RunAsync(new PluginRoleEnrichmentResult(null, [new Sid("b3f68481-9b6d-4952-ba64-07d06eec4e1f"), new Sid(EnrichedGroup)]));

        context.ControlPlaneEnrichment!.GroupSids.Select(s => s.Value).ShouldBe([EnrichedGroup]);
    }

    [Fact]
    public async Task Enriched_sids_over_cap_are_truncated_for_control_plane_only()
    {
        var sids = Enumerable.Range(1, 5).Select(i => new Sid($"S-1-5-21-9-9-9-{i}")).ToList();

        var context = await RunAsync(new PluginRoleEnrichmentResult(null, sids), maxSids: 2);

        context.ControlPlaneEnrichment!.GroupSids.Count.ShouldBe(2);
        context.GroupSids.Select(g => g.Value).ShouldBe([TokenGroup]);
    }

    [Fact]
    public async Task Mcp_session_is_not_seeded_from_enrichment()
    {
        var context = await RunAsync(new PluginRoleEnrichmentResult(["DataOwner"], [new Sid(EnrichedGroup)]));

        // The MCP caller is built from the data-plane members of the security context.
        var session = new McpSessionContext(
            "s", "principal", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, context.UserSid.Value,
            context.TenantRoles.Concat(context.ClusterRoles).ToList(), context.GroupSids.Select(g => g.Value).ToList());
        var principal = McpProtocolHandler.BuildPrincipalFromSession(session)!;

        principal.GetGroupSids().ShouldNotContain(new Sid(EnrichedGroup));
        principal.IsInRole("DataOwner").ShouldBeFalse();
    }
}
