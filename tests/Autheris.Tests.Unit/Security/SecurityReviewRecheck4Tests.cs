namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-06 (recheck 4): PG-3, PG-4, PG-5 (shared approval policy), R4-4.</summary>
[Trait("Category", "Security")]
public sealed class SecurityReviewRecheck4Tests
{
    private static ConsentRequest Request(string requester, params string[] identifiers)
    {
        var req = new ConsentRequest { TenantId = Autheris.Tests.Common.TestTenants.Default, RequesterSid = new Sid(requester), Status = "PENDING_EXTERNAL_APPROVAL" };
        req.RequesterIdentifiers.AddRange(identifiers);
        return req;
    }

    [Fact]
    public void EmptyApproverIdentity_NeverMatches()
    {
        ConsentApprovalPolicy.IsSameApprover(string.Empty, new Sid(" ")).ShouldBeFalse();
        ConsentApprovalPolicy.IsSelfApproval(Request("S-1-5-21-1-ALICE"), default).ShouldBeFalse();
        Should.NotThrow(() => ConsentApprovalPolicy.IsSelfApproval(Request("S-1-5-21-1-ALICE"), default));
    }

    [Theory]
    [InlineData(true, 1, ConsentWorkflowMode.External, "PENDING_EXTERNAL_APPROVAL")]
    [InlineData(true, 1, ConsentWorkflowMode.Internal, "PENDING_SECOND_APPROVAL")]
    [InlineData(true, 2, ConsentWorkflowMode.External, "APPROVED")]
    [InlineData(false, 1, ConsentWorkflowMode.Internal, "APPROVED")]
    public void StatusAfterApproval_FollowsFourEyesRules(bool fourEyes, int step, ConsentWorkflowMode mode, string expected) =>
        ConsentApprovalPolicy.StatusAfterApproval(fourEyes, step, mode).ShouldBe(expected);

    [Theory]
    [InlineData("PENDING", ConsentWorkflowMode.Internal, true)]
    [InlineData("PENDING_SECOND_APPROVAL", ConsentWorkflowMode.Internal, true)]
    [InlineData("PENDING_EXTERNAL_APPROVAL", ConsentWorkflowMode.Internal, false)]
    [InlineData("PENDING_EXTERNAL_APPROVAL", ConsentWorkflowMode.External, true)]
    [InlineData("PENDING", ConsentWorkflowMode.External, false)]
    [InlineData("APPROVED", ConsentWorkflowMode.Internal, false)]
    [InlineData("ACTIVE", ConsentWorkflowMode.External, false)]
    [InlineData("REJECTED", ConsentWorkflowMode.Internal, false)]
    public void EnsureApprovableStatus_OnlyAllowsTheMatchingChannel(string status, ConsentWorkflowMode mode, bool allowed)
    {
        void Act() => ConsentApprovalPolicy.EnsureApprovableStatus(Guid.NewGuid(), status, mode);

        if (allowed) Should.NotThrow(Act); else Should.Throw<InvalidOperationException>(Act);
    }

    [Theory]
    [InlineData("PENDING", true)]
    [InlineData("PENDING_SECOND_APPROVAL", true)]
    [InlineData("PENDING_EXTERNAL_APPROVAL", true)]
    [InlineData("APPROVED", false)]
    [InlineData("ACTIVE", false)]
    [InlineData("REJECTED", false)]
    public void IsRejectableStatus_OnlyPendingStates(string status, bool expected) =>
        ConsentApprovalPolicy.IsRejectableStatus(status).ShouldBe(expected);
}
