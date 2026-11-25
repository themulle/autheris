namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-06 (recheck 4): PG-3, PG-4, PG-5 (shared approval policy), R4-4. Approval steps and their four-eyes
/// rules moved to the permission request engine (issue #1132); the legacy policy keeps the rejection rules.</summary>
[Trait("Category", "Security")]
public sealed class SecurityReviewRecheck4Tests
{
    [Fact]
    public void EmptyApproverIdentity_NeverMatchesARecordedApproval()
    {
        // Issue #1132: the approver comparison of the one four-eyes path (SecretFourEyesRule) never matches an empty identity.
        var step = new ApprovalStep { ApproverSid = new Sid(" "), Decision = "APPROVED" };
        SecretFourEyesRule.IsSameApprover(step, [string.Empty, " "]).ShouldBeFalse();
        SecretFourEyesRule.IsSameApprover(new ApprovalStep { ApproverSid = new Sid("S-1-5-21-1-ALICE"), Decision = "APPROVED" }, [string.Empty]).ShouldBeFalse();
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
