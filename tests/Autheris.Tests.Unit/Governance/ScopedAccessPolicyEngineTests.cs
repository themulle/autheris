namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.Policies;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ScopedAccessPolicyEngineTests
{
    private readonly IAccessPolicyRepository _repository = Substitute.For<IAccessPolicyRepository>();
    private readonly IPolicyEpochService _epochService = Substitute.For<IPolicyEpochService>();
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly ScopedAccessPolicyEngine _sut;

    public ScopedAccessPolicyEngineTests()
    {
        _sut = new ScopedAccessPolicyEngine(_repository, _epochService, _auditWriter);
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_UserGrantee_Rejected()
    {
        var tenantId = "tenant-1";
        var policy = new AccessPolicy
        {
            TenantId = tenantId,
            PolicyId = "policy-1",
            Name = "Test Policy",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId, "policy-1", "workforce-entra", AccessPolicyGranteeType.User, "user-alice")],
            Selectors = [new AccessPolicySelector(tenantId, "policy-1", AccessPolicySelectorType.TablePattern, "finance.*")]
        };

        var act = () => _sut.CreatePolicyAsync(policy, new Sid("ADMIN"));

        await act.ShouldThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_GroupAndRoleGrantees_EvaluatesPermit()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-bob"),
            new Claim(ClaimTypes.GroupSid, "analysts"),
            new Claim(ClaimTypes.Role, "DataViewer")
        };
        var identity = new ClaimsIdentity(claims, "TestAuth");
        var principal = new ClaimsPrincipal(identity);

        var policy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "policy-10",
            Name = "Permit Analysts",
            Decision = AccessPolicyDecision.Permit,
            Priority = 100,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "policy-10", realmId, AccessPolicyGranteeType.Group, "analysts")],
            Selectors = [new AccessPolicySelector(tenantId.Value, "policy-10", AccessPolicySelectorType.TablePattern, "sales.*")]
        };

        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([policy]);

        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("sales.orders"),
            new HashSet<string> { "public" });

        var result = await _sut.EvaluateAsync(evalContext);

        result.Decision.ShouldBe(AccessPolicyDecision.Permit);
        result.MatchingPolicy.ShouldBe(policy);
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_EpochBumped_OnPolicyMutation()
    {
        var tenantId = "tenant-1";
        var policy = new AccessPolicy
        {
            TenantId = tenantId,
            PolicyId = "policy-2",
            Name = "Valid Policy",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId, "policy-2", "workforce-entra", AccessPolicyGranteeType.Group, "engineers")],
            Selectors = [new AccessPolicySelector(tenantId, "policy-2", AccessPolicySelectorType.TablePattern, "logs.*")]
        };

        await _sut.CreatePolicyAsync(policy, new Sid("ADMIN"));

        await _epochService.Received(1).IncrementEpochAsync(new TenantId(tenantId), Arg.Any<CancellationToken>());
        await _repository.Received(1).CreatePolicyAsync(policy, Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "SECURITY_POLICY_MUTATED"),
            Arg.Any<CancellationToken>());
    }
}
