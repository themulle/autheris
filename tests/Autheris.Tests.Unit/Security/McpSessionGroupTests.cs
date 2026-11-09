namespace Autheris.Tests.Unit.Security;

using System;
using System.Linq;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class McpSessionGroupTests
{
    private const string GroupSid = "S-1-5-21-1-2-3-1100";

    private static McpSessionContext Session(DateTimeOffset? groupsExpireAt, bool degraded = false) => new(
        "s", "principal", "tenant-a", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-1),
        UserSid: "S-1-5-21-1-2-3-1001", GroupSids: [GroupSid], GroupsExpireAt: groupsExpireAt, GroupsDegraded: degraded);

    [Fact]
    public void Mcp_session_group_snapshot_expires_with_resolution()
    {
        var live = McpProtocolHandler.BuildPrincipalFromSession(Session(DateTimeOffset.UtcNow.AddMinutes(5)))!;
        var stale = McpProtocolHandler.BuildPrincipalFromSession(Session(DateTimeOffset.UtcNow.AddSeconds(-1)))!;
        var degraded = McpProtocolHandler.BuildPrincipalFromSession(Session(DateTimeOffset.UtcNow.AddMinutes(5), degraded: true))!;

        live.GetGroupSids().Select(g => g.Value).ShouldBe([GroupSid]);
        GroupResolutionClaims.Read(live).ShouldBe(GroupResolutionStatus.Complete);

        // After the resolution expired, no group claim is minted from the stored strings, whatever the sliding session lifetime is.
        stale.GetGroupSids().ShouldBeEmpty();
        GroupResolutionClaims.Read(stale).ShouldBe(GroupResolutionStatus.Degraded);
        degraded.GetGroupSids().ShouldBeEmpty();
        GroupResolutionClaims.Read(degraded).ShouldBe(GroupResolutionStatus.Degraded);
    }
}
