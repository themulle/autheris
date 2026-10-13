namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Application.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>M-A2: the audited ClusterAdmin operation that resolves a tenant id collision.</summary>
public sealed class TenantRegistryAdminEndpointTests
{
    private static DefaultHttpContext Context(params string[] roles)
    {
        var claims = new List<Claim> { new("sub", "S-1-5-21-ADMIN1"), new("tenant_id", "home") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
    }

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    [Fact]
    public async Task ClusterAdmin_Resolves_AndTheOperationIsAudited()
    {
        var registry = Substitute.For<ITenantRegistry>();
        registry.ResolveCollisionAsync(new TenantId("Acme"), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(true));
        var audit = Substitute.For<IAuditLogRepository>();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("Acme", Context("ClusterAdmin"), registry, audit, default);

        Status(result).ShouldBe(StatusCodes.Status200OK);
        await audit.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.TenantCollisionResolved && e.DetailsJson!.Contains("Acme")), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("GovernanceAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("home:ClusterAdmin")]
    public async Task NonClusterAdmin_Forbidden_NothingChanges(string role)
    {
        var registry = Substitute.For<ITenantRegistry>();
        var audit = Substitute.For<IAuditLogRepository>();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("Acme", Context(role), registry, audit, default);

        Status(result).ShouldBe(StatusCodes.Status403Forbidden);
        await registry.DidNotReceiveWithAnyArgs().ResolveCollisionAsync(default, default);
    }

    [Fact]
    public async Task InvalidTenantId_BadRequest()
    {
        var registry = Substitute.For<ITenantRegistry>();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("not valid!", Context("ClusterAdmin"), registry, Substitute.For<IAuditLogRepository>(), default);

        Status(result).ShouldBe(StatusCodes.Status400BadRequest);
        await registry.DidNotReceiveWithAnyArgs().ResolveCollisionAsync(default, default);
    }

    [Fact]
    public async Task NoCollisionMark_NotFound_AndNoAuditOfAChange()
    {
        var registry = Substitute.For<ITenantRegistry>();
        registry.ResolveCollisionAsync(Arg.Any<TenantId>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(false));
        var audit = Substitute.For<IAuditLogRepository>();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("Acme", Context("ClusterAdmin"), registry, audit, default);

        Status(result).ShouldBe(StatusCodes.Status404NotFound);
        await audit.DidNotReceiveWithAnyArgs().RecordAuditEventAsync(default!, default);
    }
}
