using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Rebac;

/// <summary>
/// DT-WP7: the ReBAC tuple endpoints are ClusterAdmin-only in effect, because a tenant-scoped (or default-assigned) principal
/// can never satisfy the global GovernanceAdmin role. The denial is by design, identical with and without a tenant claim,
/// and is a governed ProblemDetails with a reason instead of a bare status.
/// </summary>
[Trait("Category", "Security")]
public sealed class RebacGovernanceAdminAuthorizationTests
{
    private static DefaultHttpContext GovernanceAdminContext(bool explicitTenantClaim)
    {
        var context = new DefaultHttpContext();
        context.RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider();
        context.Response.Body = new MemoryStream();
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-GA1"),
            ResolvedTenant = new TenantId("default-tenant"),
            TenantSource = explicitTenantClaim ? "claim" : "default",
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string> { "GovernanceAdmin" },
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer"
        };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "S-1-5-21-GA1")], "Bearer"));
        return context;
    }

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        return (context.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)context.Response.Body).ToArray()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DT_WP7_GovernanceAdmin_AddTuples_Is_Denied_With_A_Reason_Regardless_Of_The_Tenant_Source(bool explicitTenantClaim)
    {
        var context = GovernanceAdminContext(explicitTenantClaim);
        var tuple = new RebacTuple("default-tenant", "S-1-5-21-X", "viewer", "table:a.b.c");

        var result = await RebacEndpoints.AddTuplesAsync([tuple], context.Request, Substitute.For<IRebacStore>(), Substitute.For<IRebacEvaluator>(), AuditWriterTestServices.Over(Substitute.For<Autheris.Application.Interfaces.IAuditLogRepository>()));
        var (status, body) = await ExecuteAsync(result, context);

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("ACCESS_DENIED");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DT_WP7_GovernanceAdmin_GetTuples_Is_Denied_With_A_Reason_Regardless_Of_The_Tenant_Source(bool explicitTenantClaim)
    {
        var context = GovernanceAdminContext(explicitTenantClaim);

        var result = await RebacEndpoints.GetTuplesAsync(context.Request, Substitute.For<IRebacStore>());
        var (status, body) = await ExecuteAsync(result, context);

        status.ShouldBe(StatusCodes.Status403Forbidden);
        body.ShouldContain("ACCESS_DENIED");
    }
}
