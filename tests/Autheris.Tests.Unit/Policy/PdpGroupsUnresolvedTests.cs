namespace Autheris.Tests.Unit.Policy;

using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>SEC-AUTH-04a: an unknown group set denies data transports and the PDP; self-info endpoints stay reachable.</summary>
[Trait("Category", "Security")]
public sealed class PdpGroupsUnresolvedTests
{
    private static ClaimsPrincipal Principal(GroupResolutionStatus status) => new(new ClaimsIdentity(
    [
        new Claim("sub", "S-1-5-21-1-2-3-1001"),
        new Claim("tenant_id", "tenant-a"),
        GroupResolutionClaims.CreateStatus(status),
        GroupResolutionClaims.CreateMarker()
    ], "Bearer"));

    private static async Task<(HttpContext Context, bool NextCalled)> RunMiddlewareAsync(GroupResolutionStatus status, bool dataTransport)
    {
        var monitor = new Autheris.Tests.Unit.Security.FakeOptionsMonitor<GatewayOptions>(new GatewayOptions());
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>(monitor);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), User = Principal(status) };
        context.Response.Body = new MemoryStream();
        var metadata = dataTransport
            ? new EndpointMetadataCollection(new IngressTransportMetadata(DataSourceTransportFlags.Rest))
            : EndpointMetadataCollection.Empty;
        context.SetEndpoint(new Endpoint(null, metadata, "test"));

        var nextCalled = false;
        await new SecurityContextResolutionMiddleware(_ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }).InvokeAsync(context);
        return (context, nextCalled);
    }

    [Fact]
    public async Task Degraded_denies_tables_views_procedures_catalog_with_identity_groups_unresolved()
    {
        // Every data transport carries the ingress transport metadata; the gate answers before the endpoint runs.
        var (denied, nextCalled) = await RunMiddlewareAsync(GroupResolutionStatus.Degraded, dataTransport: true);

        nextCalled.ShouldBeFalse();
        denied.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        denied.Response.Body.Position = 0;
        new StreamReader(denied.Response.Body).ReadToEnd().ShouldContain("identity.groups_unresolved");

        // The table policy denies on the same status (views are tables).
        var policy = new TableAccessPolicy(
            Autheris.Tests.Unit.Security.NullFake.Create<IConsentRepository>(),
            Autheris.Tests.Unit.Security.NullFake.Create<IConsentResolutionService>(),
            Autheris.Tests.Unit.Security.NullFake.Create<IConsentCacheService>(),
            Autheris.Tests.Unit.Security.NullFake.Create<IPolicyEnforcementService>(),
            Autheris.Tests.Unit.Security.NullFake.Create<IRebacEvaluator>(),
            null,
            new GatewayOptions(),
            new Autheris.Application.VirtualFilters.NullMandatoryRowFilterResolver());
        var table = new TableIdentifier("d", "main", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Table = new Table { SourceName = "ds", SchemaName = "main", TableName = "orders", SourceType = "SQLite", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "INTEGER" }]
        };
        var decision = await policy.DecideAsync(
            TableAccessQuery.ForPrincipal(Principal(GroupResolutionStatus.Degraded), new Sid("S-1-5-21-1-2-3-1001"), new TenantId("tenant-a"), meta), CancellationToken.None);

        decision.IsAllowed.ShouldBeFalse();
        decision.DeniedReasons.ShouldContain(r => r.Contains("identity.groups_unresolved"));
    }

    [Fact]
    public async Task Degraded_keeps_self_info_endpoint_reachable()
    {
        var (context, nextCalled) = await RunMiddlewareAsync(GroupResolutionStatus.Degraded, dataTransport: false);

        nextCalled.ShouldBeTrue();
        var securityContext = (SecurityPrincipalContext)context.Items[SecurityPrincipalContext.ItemKey]!;
        securityContext.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);
        securityContext.GroupSids.ShouldBeEmpty();
    }

    [Fact]
    public async Task Complete_status_reaches_data_transport()
    {
        var (_, nextCalled) = await RunMiddlewareAsync(GroupResolutionStatus.Complete, dataTransport: true);

        nextCalled.ShouldBeTrue();
    }

    [Fact]
    public void Unmarked_security_context_defaults_to_degraded()
    {
        var unmarked = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-1-2-3-1001"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Test"
        };

        unmarked.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);
        SecurityPrincipalContext.Anonymous().GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Complete);
        (unmarked with { GroupResolutionStatus = GroupResolutionStatus.Complete }).GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Complete);
    }
}
