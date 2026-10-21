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

    [Fact]
    public async Task ScopedAccessPolicyEngine_NamespacedGroupWithPipe_MatchesUnprefixedGrantee()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-charlie"),
            new Claim(ClaimTypes.GroupSid, "workforce-entra|finance-dept")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var policy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "policy-finance",
            Name = "Permit Finance",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "policy-finance", realmId, AccessPolicyGranteeType.Group, "finance-dept")],
            Selectors = [new AccessPolicySelector(tenantId.Value, "policy-finance", AccessPolicySelectorType.TablePattern, "finance.*")]
        };

        IReadOnlyCollection<string>? capturedGroups = null;
        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Do<IReadOnlyCollection<string>>(g => capturedGroups = g), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([policy]);

        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("finance.general_ledger"),
            new HashSet<string> { "restricted" });

        var result = await _sut.EvaluateAsync(evalContext);

        result.Decision.ShouldBe(AccessPolicyDecision.Permit);
        capturedGroups.ShouldNotBeNull();
        capturedGroups.ShouldContain("workforce-entra|finance-dept");
        capturedGroups.ShouldContain("finance-dept");
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_RoleGrantee_MatchesUserRole()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-dave"),
            new Claim(ClaimTypes.Role, "BillingAdmin")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var policy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "policy-role",
            Name = "Permit Billing Role",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "policy-role", realmId, AccessPolicyGranteeType.Role, "BillingAdmin")],
            Selectors = [new AccessPolicySelector(tenantId.Value, "policy-role", AccessPolicySelectorType.TablePattern, "billing.*")]
        };

        IReadOnlyCollection<string>? capturedRoles = null;
        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Do<IReadOnlyCollection<string>>(r => capturedRoles = r), Arg.Any<CancellationToken>())
            .Returns([policy]);

        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("billing.invoices"),
            new HashSet<string>());

        var result = await _sut.EvaluateAsync(evalContext);

        result.Decision.ShouldBe(AccessPolicyDecision.Permit);
        capturedRoles.ShouldNotBeNull();
        capturedRoles.ShouldContain("BillingAdmin");
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_EmptySelectors_MatchesAnyTable()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-eve"),
            new Claim(ClaimTypes.GroupSid, "all-access")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var policy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "policy-all-tables",
            Name = "Permit All Tables",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "policy-all-tables", realmId, AccessPolicyGranteeType.Group, "all-access")],
            Selectors = [] // Empty selectors = matches all tables
        };

        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([policy]);

        var evalContext1 = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("arbitrary.table1"),
            new HashSet<string>());

        var evalContext2 = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("completely_different.table2"),
            new HashSet<string>());

        var result1 = await _sut.EvaluateAsync(evalContext1);
        var result2 = await _sut.EvaluateAsync(evalContext2);

        result1.Decision.ShouldBe(AccessPolicyDecision.Permit);
        result2.Decision.ShouldBe(AccessPolicyDecision.Permit);
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_LeadingPipeInGroup_DoesNotStripLeadingPipe()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-leading"),
            new Claim(ClaimTypes.GroupSid, "|leading-pipe")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        IReadOnlyCollection<string>? capturedGroups = null;
        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Do<IReadOnlyCollection<string>>(g => capturedGroups = g), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("finance.general_ledger"),
            new HashSet<string>());

        await _sut.EvaluateAsync(evalContext);

        capturedGroups.ShouldNotBeNull();
        capturedGroups.ShouldContain("|leading-pipe");
        capturedGroups.ShouldNotContain("leading-pipe");
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_CatalogTagSelector_MatchesMatchingTag()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "user-tag"),
            new Claim(ClaimTypes.GroupSid, "compliance")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var policy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "policy-tag",
            Name = "Permit PII Tag",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "policy-tag", realmId, AccessPolicyGranteeType.Group, "compliance")],
            Selectors = [new AccessPolicySelector(tenantId.Value, "policy-tag", AccessPolicySelectorType.CatalogTag, "pii")]
        };

        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([policy]);

        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("crm.customers"),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "pii", "confidential" });

        var result = await _sut.EvaluateAsync(evalContext);

        result.Decision.ShouldBe(AccessPolicyDecision.Permit);
        result.MatchingPolicy.ShouldBe(policy);
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_NoSelectorMatches_ReturnsDeny()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[] { new Claim(ClaimTypes.PrimarySid, "user-1"), new Claim(ClaimTypes.GroupSid, "grp") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var policy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "pol-sales",
            Name = "Sales Only",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "pol-sales", realmId, AccessPolicyGranteeType.Group, "grp")],
            Selectors = [new AccessPolicySelector(tenantId.Value, "pol-sales", AccessPolicySelectorType.TablePattern, "sales.*")]
        };

        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([policy]);

        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("hr.salaries"),
            new HashSet<string>());

        var result = await _sut.EvaluateAsync(evalContext);

        result.Decision.ShouldBe(AccessPolicyDecision.Deny);
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldContain("No matching policy selector");
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_EqualPriority_DenyWinsFailClosed()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[] { new Claim(ClaimTypes.PrimarySid, "user-1"), new Claim(ClaimTypes.GroupSid, "grp") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var permitPolicy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "pol-permit",
            Name = "Permit Grp",
            Priority = 100,
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "pol-permit", realmId, AccessPolicyGranteeType.Group, "grp")],
            Selectors = []
        };

        var denyPolicy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "pol-deny",
            Name = "Deny Grp",
            Priority = 100, // Equal priority!
            Decision = AccessPolicyDecision.Deny,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "pol-deny", realmId, AccessPolicyGranteeType.Group, "grp")],
            Selectors = []
        };

        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([permitPolicy, denyPolicy]);

        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("finance.general_ledger"),
            new HashSet<string>());

        var result = await _sut.EvaluateAsync(evalContext);

        // Fail-closed: Deny wins on tie
        result.Decision.ShouldBe(AccessPolicyDecision.Deny);
        result.MatchingPolicy?.PolicyId.ShouldBe("pol-deny");
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_UpdateAndDelete_IncrementsEpochAndAudits()
    {
        var tenantId = "tenant-corp";
        var policy = new AccessPolicy
        {
            TenantId = tenantId,
            PolicyId = "policy-update",
            Name = "Updated Policy",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId, "policy-update", "realm", AccessPolicyGranteeType.Role, "Admin")],
            Selectors = []
        };

        var actor = new Sid("admin-user");

        // Update
        // Update via actor overload sets ApprovedBy = actor
        policy.CreatedBy = "creator-admin";
        await _sut.UpdatePolicyAsync(policy, actor);
        await _repository.Received(1).UpdatePolicyAsync(policy, Arg.Any<CancellationToken>());
        await _epochService.Received(1).IncrementEpochAsync(new TenantId(tenantId), Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "SECURITY_POLICY_MUTATED" &&
                e.ActorSid == actor &&
                e.DetailsJson.Contains("UPDATE")),
            Arg.Any<CancellationToken>());

        // Update with ApprovedBy = null falls back to CreatedBy
        policy.ApprovedBy = null;
        await _sut.UpdatePolicyAsync(policy);
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "SECURITY_POLICY_MUTATED" &&
                e.ActorSid == new Sid("creator-admin") &&
                e.DetailsJson.Contains("UPDATE")),
            Arg.Any<CancellationToken>());

        // Delete
        await _sut.DeletePolicyAsync(new TenantId(tenantId), "policy-update", actor);
        await _repository.Received(1).DeletePolicyAsync(new TenantId(tenantId), "policy-update", Arg.Any<CancellationToken>());
        await _epochService.Received(3).IncrementEpochAsync(new TenantId(tenantId), Arg.Any<CancellationToken>());
        await _auditWriter.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "SECURITY_POLICY_MUTATED" &&
                e.ActorSid == actor &&
                e.DetailsJson.Contains("DELETE")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_UpdatePolicy_UserGrantee_ThrowsArgumentException()
    {
        var tenantId = "tenant-user";
        var policy = new AccessPolicy
        {
            TenantId = tenantId,
            PolicyId = "policy-user-grantee",
            Name = "Bad Policy",
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId, "policy-user-grantee", "realm", AccessPolicyGranteeType.User, "user-direct")],
            Selectors = []
        };

        await Should.ThrowAsync<ArgumentException>(() =>
            _sut.UpdatePolicyAsync(policy));
    }

    [Fact]
    public async Task ScopedAccessPolicyEngine_Evaluate_CatalogTag_NonMatchingTag_Denies()
    {
        var tenantId = new TenantId("tenant-1");
        var realmId = "workforce-entra";

        var claims = new[] { new Claim(ClaimTypes.PrimarySid, "user-1"), new Claim(ClaimTypes.GroupSid, "grp-tags") };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        var policy = new AccessPolicy
        {
            TenantId = tenantId.Value,
            PolicyId = "pol-tag-pci",
            Name = "PCI Data Only",
            Priority = 10,
            Decision = AccessPolicyDecision.Permit,
            Grantees = [new AccessPolicyGrantee(tenantId.Value, "pol-tag-pci", realmId, AccessPolicyGranteeType.Group, "grp-tags")],
            Selectors = [new AccessPolicySelector(tenantId.Value, "pol-tag-pci", AccessPolicySelectorType.CatalogTag, "pci")]
        };

        _repository.GetActivePoliciesForGranteesAsync(
            tenantId, realmId, Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns([policy]);

        // Context has different tag ("gdpr"), not "pci"
        var evalContext = new PolicyEvaluationContext(
            principal,
            tenantId,
            realmId,
            TableIdentifier.Parse("finance.transactions"),
            new HashSet<string>(["gdpr"]));

        var result = await _sut.EvaluateAsync(evalContext);

        result.Decision.ShouldBe(AccessPolicyDecision.Deny);
        result.Reason.ShouldNotBeNull();
        result.Reason.ShouldContain("No matching policy selector");
    }
}
