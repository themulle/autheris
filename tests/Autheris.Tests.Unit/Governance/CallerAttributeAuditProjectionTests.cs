namespace Autheris.Tests.Unit.Governance;

using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>ST-129-10, BC-129-15: the caller-visible audit projection is a field allowlist; <c>caller_attributes</c> and <c>caller_attribute_binds</c> never leave it.</summary>
[Trait("Category", "Security")]
public sealed class CallerAttributeAuditProjectionTests
{
    [Fact]
    public async Task ST_129_10_AuditListing_NeverContainsTheCallerAttributeFields()
    {
        var entry = new AuditLogEntry
        {
            ChainSequence = 5, TenantId = new TenantId("t1"), EventType = "TABLE_QUERY", ActorSid = new Sid("S-1-5-21-X"), TargetTable = "default.sales.orders",
            Decision = "ALLOW", TraceId = "t",
            DetailsJson = "{\"caller_attributes\":{\"orders\":[{\"id\":\"a\",\"outcome\":\"Resolved\"}]},\"caller_attribute_binds\":3}"
        };
        var repo = Substitute.For<IAuditLogRepository>();
        repo.QueryAuditLogPageAsync(Arg.Any<AuditLogPageQuery>(), Arg.Any<CancellationToken>()).Returns(new AuditLogPage([entry], null));
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "GovernanceAdmin"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-ME")], "test"));
        ctx.Items[TenantResolutionMiddleware.TenantIdItemKey] = new TenantId("t1");

        var result = await GovernanceEndpoints.ListAuditPageAsync(ctx, repo, null, null, null, null, null, null);
        var render = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        render.Response.Body = new MemoryStream();
        await result.ExecuteAsync(render);
        render.Response.Body.Position = 0;
        var body = await new StreamReader(render.Response.Body).ReadToEndAsync();

        body.ShouldContain("TABLE_QUERY");
        body.ShouldNotContain("caller_attribute");
        body.ShouldNotContain("detailsJson", Case.Insensitive);
    }
}
