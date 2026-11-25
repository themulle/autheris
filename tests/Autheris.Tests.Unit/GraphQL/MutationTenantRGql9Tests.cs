namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Tests.Unit.TestSupport;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>
/// R-GQL-9: mutations use the tenant resolved for the request (ClusterAdmin's X-Tenant-ID), the claim only as a
/// cross-check, with the same admin exception everywhere.
/// </summary>
public sealed class MutationTenantRGql9Tests
{
    private sealed class Accessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private static Accessor Context(string tokenTenant, string requestTenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-MUT"), new("tenant_id", tokenTenant) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        http.Items["TenantId"] = new TenantId(requestTenant);
        return new Accessor(http);
    }

    [Fact]
    public async Task ClusterAdmin_RequestsAccess_InTheSelectedTenant()
    {
        var engine = new RecordingPermissionRequestEngine();

        await new Mutation().RequestTableAccessAsync("hr", "dbo", "salaries", "Quarterly audit", 7, null, new ConsentRequestEndpointAdapter(engine), Context("tenant-a", "tenant-b", "ClusterAdmin"));

        engine.Submissions.ShouldHaveSingleItem().TenantId.ShouldBe(new TenantId("tenant-b"));
    }

    [Fact]
    public async Task NonAdmin_WithDifferentClaimAndRequestTenant_IsForbidden()
    {
        var engine = new RecordingPermissionRequestEngine();

        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            new Mutation().RequestTableAccessAsync("hr", "dbo", "salaries", "Quarterly audit", 7, null, new ConsentRequestEndpointAdapter(engine), Context("tenant-a", "tenant-b")));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
        engine.Submissions.ShouldBeEmpty();
    }
}
