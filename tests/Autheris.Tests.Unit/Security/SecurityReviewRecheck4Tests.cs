namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-06 (recheck 4): PG-3, PG-4, PG-5 (shared approval policy), R4-4.</summary>
public sealed class SecurityReviewRecheck4Tests
{
    private static ConsentRequest Request(string requester, params string[] identifiers)
    {
        var req = new ConsentRequest { RequesterSid = new Sid(requester), Status = "PENDING_EXTERNAL_APPROVAL" };
        req.RequesterIdentifiers.AddRange(identifiers);
        return req;
    }

    [Fact]
    public void ItsmApprover_IsRequester_ByAccountPart_IsSelfApproval()
    {
        var req = Request("S-1-5-21-1-ALICE", "alice@corp.local");

        ConsentApprovalPolicy.IsSelfApproval(req, new Sid("ITSM_SNOW:inst:alice@corp.local")).ShouldBeTrue();
    }

    [Fact]
    public void ItsmApprover_PassedTyped_IsRequester_IsSelfApproval()
    {
        var req = Request("S-1-5-21-1-ALICE", "alice@corp.local");

        ConsentApprovalPolicy.IsSelfApproval(req, new Sid("ITSM_SNOW:inst:svc"), "Alice@corp.local").ShouldBeTrue();
    }

    [Fact]
    public void ItsmApprover_OtherPerson_IsNotSelfApproval()
    {
        var req = Request("S-1-5-21-1-ALICE", "alice@corp.local");

        ConsentApprovalPolicy.IsSelfApproval(req, new Sid("ITSM_SNOW:inst:bob@corp.local"), "bob@corp.local").ShouldBeFalse();
    }

    [Fact]
    public void FourEyes_SamePersonViaItsmAndGraphQlAccount_IsSameApprover()
    {
        ConsentApprovalPolicy.IsSameApprover("ITSM_SNOW:inst:dataowner@corp.local", new Sid("dataowner@corp.local")).ShouldBeTrue();
        ConsentApprovalPolicy.IsSameApprover("S-1-5-21-1-OWNER", new Sid("ITSM_SNOW:inst:x"), "S-1-5-21-1-OWNER").ShouldBeTrue();
        ConsentApprovalPolicy.IsSameApprover("ITSM_SNOW:inst:a@corp.local", new Sid("ITSM_SNOW:inst:b@corp.local"), "b@corp.local").ShouldBeFalse();
    }

    [Fact]
    public void EmptyApproverIdentity_NeverMatches()
    {
        ConsentApprovalPolicy.IsSameApprover("ITSM_SNOW:inst:", new Sid("ITSM_OTHER:inst:")).ShouldBeFalse();
        ConsentApprovalPolicy.IsSelfApproval(Request("S-1-5-21-1-ALICE"), new Sid("ITSM_SNOW:inst:")).ShouldBeFalse();
        Should.NotThrow(() => ConsentApprovalPolicy.IsSelfApproval(Request("S-1-5-21-1-ALICE"), default));
    }

    [Theory]
    [InlineData(true, 1, true, "PENDING_EXTERNAL_APPROVAL")]
    [InlineData(true, 1, false, "PENDING_SECOND_APPROVAL")]
    [InlineData(true, 2, true, "APPROVED")]
    [InlineData(false, 1, false, "APPROVED")]
    public void StatusAfterApproval_FollowsFourEyesRules(bool fourEyes, int step, bool itsm, string expected) =>
        ConsentApprovalPolicy.StatusAfterApproval(fourEyes, step, itsm).ShouldBe(expected);

    [Theory]
    [InlineData("PENDING", false, true)]
    [InlineData("PENDING_SECOND_APPROVAL", false, true)]
    [InlineData("PENDING_EXTERNAL_APPROVAL", false, false)]
    [InlineData("PENDING_EXTERNAL_APPROVAL", true, true)]
    [InlineData("PENDING", true, false)]
    [InlineData("APPROVED", false, false)]
    [InlineData("ACTIVE", true, false)]
    [InlineData("REJECTED", false, false)]
    public void EnsureApprovableStatus_OnlyAllowsTheMatchingChannel(string status, bool itsm, bool allowed)
    {
        void Act() => ConsentApprovalPolicy.EnsureApprovableStatus(Guid.NewGuid(), status, itsm);

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
