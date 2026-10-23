namespace Autheris.Tests.Unit.Security;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Mcp.Interfaces;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// MCP-12: the table-approver ownership check must fail closed when the ticket cannot be resolved.
/// </summary>
public sealed class Mcp12HitLApproverFailClosedTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task MCP_12_NullTicket_DoesNotSkipOwnershipCheck()
    {
        var hitl = Substitute.For<IHitLStepUpApprovalService>();
        hitl.GetTicketAsync("missing", Arg.Any<System.Threading.CancellationToken>()).Returns((HitLApprovalTicket?)null);
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity()) };
        var approver = new HitLApproverContext("S-1-5-21-APPROVER", new List<string>(), "tenant-a");

        var result = await HitLEndpoints.EnsureTableApproverAsync(context, hitl, "missing", approver);

        result.ShouldNotBeNull();
    }
}
