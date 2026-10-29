using System.Security.Claims;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Security;

/// <summary>L13 / DEF-SC-13: an invalid tuple tenant is rejected before any mutation (400 INVALID_TENANT_ID, no echo) instead of silently auditing under the caller tenant.</summary>
[Trait("Category", "Security")]
public sealed class RebacTupleTenantValidationTests
{
    private const string BadTenant = "bad tenant!";

    private static DefaultHttpContext ClusterAdminContext()
    {
        var claims = new List<Claim> { new("sub", "S-1-5-21-ADMIN1"), new("tenant_id", "home"), new(ClaimTypes.Role, "ClusterAdmin") };
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-ADMIN1"),
            TenantId = new TenantId("home"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string> { "ClusterAdmin" },
            AuthenticationScheme = "Bearer"
        };
        return context;
    }

    private static RebacTuple InvalidTenantTuple() => new(BadTenant, "user:alice", "viewer", "table:sales.public.orders") { ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };

    private static void AssertGovernedBadRequest(IResult result)
    {
        ((IStatusCodeHttpResult)result).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        var body = JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        body.ShouldContain("INVALID_TENANT_ID");
        body.ShouldNotContain(BadTenant);
    }

    [Fact]
    public async Task Rebac_Tuple_Invalid_Tenant_Is_Rejected()
    {
        var context = ClusterAdminContext();
        var store = Substitute.For<IRebacStore>();
        var evaluator = Substitute.For<IRebacEvaluator>();
        var audit = AuditWriterTestServices.Over(Substitute.For<IAuditLogRepository>());

        AssertGovernedBadRequest(await RebacEndpoints.AddTuplesAsync([InvalidTenantTuple()], context.Request, store, evaluator, audit, null));
        AssertGovernedBadRequest(await RebacEndpoints.RemoveTupleAsync(InvalidTenantTuple(), context.Request, store, evaluator, audit));
    }

    [Fact]
    public async Task Rebac_Tuple_Invalid_Tenant_Rejected_Before_Mutation()
    {
        var context = ClusterAdminContext();
        var store = Substitute.For<IRebacStore>();
        var evaluator = Substitute.For<IRebacEvaluator>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var audit = AuditWriterTestServices.Over(auditRepo);

        await RebacEndpoints.AddTuplesAsync([InvalidTenantTuple()], context.Request, store, evaluator, audit, null);
        await RebacEndpoints.RemoveTupleAsync(InvalidTenantTuple(), context.Request, store, evaluator, audit);

        store.ReceivedCalls().ShouldBeEmpty();
        evaluator.ReceivedCalls().ShouldBeEmpty();
        auditRepo.ReceivedCalls().ShouldBeEmpty();
    }
}
