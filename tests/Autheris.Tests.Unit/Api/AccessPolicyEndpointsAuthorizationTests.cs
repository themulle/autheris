using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Security;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Api;

[Trait("Category", "Security")]
public sealed class AccessPolicyEndpointsAuthorizationTests
{
    private static readonly TenantId TenantA = new("tenant-a");

    private static HttpContext HttpFor(ClaimsPrincipal principal, string? ifMatch = null)
    {
        var context = new DefaultHttpContext { User = principal };
        var sec = SecurityPrincipalContext.FromPrincipal(principal, overrideTenant: TenantA);
        context.Items[SecurityPrincipalContext.ItemKey] = sec;
        context.Items[SecurityPrincipalContext.TenantIdItemKey] = TenantA;
        if (ifMatch is not null)
        {
            context.Request.Headers.IfMatch = ifMatch;
        }

        return context;
    }

    private static ClaimsPrincipal MakePrincipal(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, "S-1-5-21-TEST"),
            new("tenant_id", TenantA.Value)
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private sealed class StubPolicyProvider : IAccessPolicySettingsProvider
    {
        public int UpdateCalls { get; private set; }
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenant, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenant, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default)
        {
            UpdateCalls++;
            return Task.FromResult(new AccessPolicyUpdateResult(AccessPolicyUpdateStatus.Updated));
        }
    }

    private sealed class StubPolicyRepo : IAccessPolicySettingsRepository
    {
        public Task<StoredTenantAccessPolicy?> GetAsync(TenantId tenant, CancellationToken ct = default) =>
            Task.FromResult<StoredTenantAccessPolicy?>(null);
        public Task<AccessPolicyWriteResult> UpsertAsync(TenantId tenant, TenantAccessPolicyUpdate settings, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            Task.FromResult(new AccessPolicyWriteResult(AccessPolicyWriteStatus.Created,
                new StoredTenantAccessPolicy(tenant, settings, 1, actor.Value, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task It_refused()
    {
        var capabilities = new GovernanceCapabilities();
        var provider = new StubPolicyProvider();
        var itPrincipal = MakePrincipal("TenantAdmin");
        var clusterPrincipal = MakePrincipal("ClusterAdmin");

        var getIt = await AccessPolicyEndpoints.GetPolicyAsync(
            HttpFor(itPrincipal),
            capabilities,
            provider,
            CancellationToken.None);
        (getIt as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var putIt = await AccessPolicyEndpoints.UpdatePolicyAsync(
            HttpFor(itPrincipal, "\"0\""),
            new AccessPolicyEndpoints.TenantAccessPolicyUpdateRequest(MaxSelfGrantDays: 30),
            capabilities,
            provider,
            CancellationToken.None);
        (putIt as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        var putCluster = await AccessPolicyEndpoints.UpdatePolicyAsync(
            HttpFor(clusterPrincipal, "\"0\""),
            new AccessPolicyEndpoints.TenantAccessPolicyUpdateRequest(MaxSelfGrantDays: 30),
            capabilities,
            provider,
            CancellationToken.None);
        (putCluster as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);

        provider.UpdateCalls.ShouldBe(0);
    }

    [Fact]
    public async Task Governance_admin_can_read_and_update_with_if_match_and_etag()
    {
        var capabilities = new GovernanceCapabilities();
        var repo = new StubPolicyRepo();
        var audit = new InMemoryAuditWriter();

        var provider = new AccessPolicySettingsProvider(
            repo,
            Options.Create(new AccessPolicyOptions()),
            audit,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var govPrincipal = MakePrincipal("GovernanceAdmin");

        var getHttp = HttpFor(govPrincipal);
        var getResult = await AccessPolicyEndpoints.GetPolicyAsync(getHttp, capabilities, provider, CancellationToken.None);
        (getResult as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status200OK);
        getHttp.Response.Headers.ETag.ToString().ShouldBe("\"0\"");

        var missingIfMatch = await AccessPolicyEndpoints.UpdatePolicyAsync(
            HttpFor(govPrincipal),
            new AccessPolicyEndpoints.TenantAccessPolicyUpdateRequest(MaxSelfGrantDays: 30),
            capabilities,
            provider,
            CancellationToken.None);
        (missingIfMatch as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status428PreconditionRequired);

        var putHttp = HttpFor(govPrincipal, "\"0\"");
        var putResult = await AccessPolicyEndpoints.UpdatePolicyAsync(
            putHttp,
            new AccessPolicyEndpoints.TenantAccessPolicyUpdateRequest(MaxSelfGrantDays: 30),
            capabilities,
            provider,
            CancellationToken.None);
        (putResult as IStatusCodeHttpResult)?.StatusCode.ShouldBe(StatusCodes.Status200OK);
        putHttp.Response.Headers.ETag.ToString().ShouldBe("\"1\"");
    }

    [Fact]
    public void Route_metadata_carries_authorization_and_audit()
    {
        using var routes = new TestEndpointRouteBuilder(permissiveServices: true);
        routes.MapAccessPolicyEndpoints();

        var endpoints = routes.DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();

        endpoints.Count.ShouldBe(2);

        var get = endpoints.Single(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("GET"));
        get.Metadata.GetMetadata<IAuthorizeData>()!.Policy.ShouldBe(GatewayPolicies.GovernanceAdmin);
        get.Metadata.GetMetadata<AuditPolicy>()!.EventType.ShouldBe(AuditEventTypes.CatalogRead);

        var put = endpoints.Single(e => e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));
        put.Metadata.GetMetadata<IAuthorizeData>()!.Policy.ShouldBe(GatewayPolicies.AuthenticatedCaller);
        put.Metadata.GetMetadata<AuditPolicy>()!.EventType.ShouldBe(AuditEventTypes.PolicyChanged);
        put.Metadata.GetMetadata<AuditPolicy>()!.Level.ShouldBe(AuditLevel.Full);
    }
}
