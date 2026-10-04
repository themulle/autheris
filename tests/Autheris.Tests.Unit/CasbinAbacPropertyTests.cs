using System.Net;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class CasbinAbacPropertyTests : IDisposable
{
    private readonly CasbinEnforcementService _service = new();

    public void Dispose() => _service.Dispose();


    [Fact]
    public async Task TenantIsolation_FailsDeterministically_WhenRequestTenantDiffersFromPolicyTenant()
    {
        var tenantA = new TenantId("tenant-alpha");
        var tenantB = new TenantId("tenant-beta");
        var userSid = new Sid("S-1-5-21-user-1");
        var targetTable = new TableIdentifier("finance", "dbo", "invoices");

        // Allow policy strictly for tenantA
        _service.AddPolicy(tenantA, userSid.Value, targetTable.ToString(), "read", "true", "allow");

        // Context for tenantB (same user, same table, but different tenant)
        var contextB = new SecurityEvaluationContext(
            userSid,
            [],
            tenantB,
            targetTable,
            ["id", "amount"],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "audit");

        var decisionB = await _service.EvaluatePolicyAsync(contextB);
        decisionB.IsAllowed.ShouldBeFalse("Cross-tenant request must be rejected deterministically.");

        // Context for tenantA should be allowed
        var contextA = new SecurityEvaluationContext(
            userSid,
            [],
            tenantA,
            targetTable,
            ["id", "amount"],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            "audit");

        var decisionA = await _service.EvaluatePolicyAsync(contextA);
        decisionA.IsAllowed.ShouldBeTrue("Same-tenant request matching policy must be allowed.");
    }

    [Fact]
    public async Task DenyOverridesAllow_Property_WhenBothRulesMatch()
    {
        var tenant = new TenantId("tenant-sec");
        var userSid = new Sid("S-1-5-21-analyst");
        var targetTable = new TableIdentifier("hr", "dbo", "salaries");

        // Add Allow rule
        _service.AddPolicy(tenant, userSid.Value, targetTable.ToString(), "read", "true", "allow");

        // Add Deny rule
        _service.AddPolicy(tenant, userSid.Value, targetTable.ToString(), "read", "true", "deny");

        var context = new SecurityEvaluationContext(
            userSid,
            [],
            tenant,
            targetTable,
            ["salary"],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            null);

        var decision = await _service.EvaluatePolicyAsync(context);
        decision.IsAllowed.ShouldBeFalse("Deny must strictly override Allow.");
    }

    [Fact]
    public async Task RoleInheritance_AllowsAccessViaGroupSid()
    {
        var tenant = new TenantId("tenant-rbac");
        var userSid = new Sid("S-1-5-21-user-finance");
        var groupSid = new Sid("S-1-5-32-finance-readers");
        var targetTable = new TableIdentifier("finance", "dbo", "ledger");

        // Policy allows role
        _service.AddPolicy(tenant, "role:finance_reader", targetTable.ToString(), "read", "true", "allow");
        _service.AddRoleForUser(tenant, groupSid.Value, "role:finance_reader");

        var context = new SecurityEvaluationContext(
            userSid,
            [groupSid],
            tenant,
            targetTable,
            ["balance"],
            IPAddress.Loopback,
            DateTimeOffset.UtcNow,
            null);

        var decision = await _service.EvaluatePolicyAsync(context);
        decision.IsAllowed.ShouldBeTrue("User should inherit access via group role.");
    }

    [Fact]
    public async Task PolicyReload_IncrementsEpoch_AndClearsTenantState()
    {
        var tenant = new TenantId("tenant-epoch");
        var initialEpoch = _service.CurrentEpoch;

        await _service.ReloadPoliciesAsync(tenant);

        _service.CurrentEpoch.ShouldBeGreaterThan(initialEpoch);
    }
}
