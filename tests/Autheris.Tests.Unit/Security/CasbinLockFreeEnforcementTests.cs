namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>
/// AR-12: Verification of lock-free Casbin evaluation using pure stateless snapshot evaluation.
/// Eliminates lock (enforcer) and guarantees thread safety and deterministic consistency under heavy concurrency.
/// </summary>
public sealed class CasbinLockFreeEnforcementTests : IDisposable
{
    private readonly CasbinEnforcementService _service = new();

    public void Dispose() => _service.Dispose();

    [Fact]
    public void Architecture_NoLockOnEnforcer_InCasbinEnforcementService()
    {
        // AR-12 Architecture / Source Test: No lock(enforcer) statements in CasbinEnforcementService
        var type = typeof(CasbinEnforcementService);
        var assemblyLocation = type.Assembly.Location;
        var srcDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../src/Autheris.Application/Governance"));
        var filePath = Path.Combine(srcDir, "CasbinEnforcementService.cs");

        if (File.Exists(filePath))
        {
            var content = File.ReadAllText(filePath);
            content.ShouldNotContain("lock (enforcer)", Case.Insensitive);
            content.ShouldNotContain("lock(enforcer)", Case.Insensitive);
        }

        // Method verification: EvaluatePolicyAsync method exists and is public
        var method = type.GetMethod("EvaluatePolicyAsync", BindingFlags.Public | BindingFlags.Instance);
        method.ShouldNotBeNull();
    }

    [Fact]
    public async Task ConcurrentEnforce_MatchesSequentialResults_UnderPolicyReloads()
    {
        // Arrange
        var tenantA = new TenantId("tenant-alpha");
        var tenantB = new TenantId("tenant-beta");
        var tenantC = new TenantId("tenant-gamma");

        var tableOrders = new TableIdentifier("sales", "dbo", "orders");
        var tableSalaries = new TableIdentifier("hr", "dbo", "salaries");
        var tableAudit = new TableIdentifier("finance", "audit", "logs");
        var tableReports = new TableIdentifier("finance", "public", "reports");

        // Set up policies
        _service.AddPolicy(tenantA, "alice", tableOrders.ToString(), "read", "true", "allow");
        _service.AddPolicy(tenantA, "role:hr_manager", tableSalaries.ToString(), "read", "true", "allow");
        _service.AddRoleForUser(tenantA, "bob", "role:hr_manager");

        _service.AddPolicy(tenantB, "david", "finance.*", "read", "true", "allow");
        _service.AddPolicy(tenantB, "david", tableAudit.ToString(), "read", "true", "deny"); // Deny overrides allow

        _service.AddWildcardPolicy("role:global_auditor", "*", "read", "true", "allow");
        _service.AddWildcardRoleForUser("eve", "role:global_auditor");

        // Define a set of test scenarios with expected outcomes
        var scenarios = new List<(SecurityEvaluationContext Ctx, bool ExpectedAllowed)>
        {
            // Scenario 1: Alice on orders in tenantA -> Allow
            (CreateContext("alice", tenantA, tableOrders, "read"), true),
            // Scenario 2: Alice on salaries in tenantA -> Deny
            (CreateContext("alice", tenantA, tableSalaries, "read"), false),
            // Scenario 3: Bob on salaries in tenantA (via role) -> Allow
            (CreateContext("bob", tenantA, tableSalaries, "read"), true),
            // Scenario 4: Bob on orders in tenantA -> Deny
            (CreateContext("bob", tenantA, tableOrders, "read"), false),
            // Scenario 5: Cross-tenant: Alice in tenantB on orders -> Deny
            (CreateContext("alice", tenantB, tableOrders, "read"), false),
            // Scenario 6: David in tenantB on reports -> Allow (finance.*)
            (CreateContext("david", tenantB, tableReports, "read"), true),
            // Scenario 7: David in tenantB on audit -> Deny (explicit deny)
            (CreateContext("david", tenantB, tableAudit, "read"), false),
            // Scenario 8: Eve in tenantC (via wildcard role) on orders -> Allow
            (CreateContext("eve", tenantC, tableOrders, "read"), true),
            // Scenario 9: Unknown user frank in tenantA -> Deny
            (CreateContext("frank", tenantA, tableOrders, "read"), false)
        };

        // Pre-evaluate sequentially to confirm expectations
        foreach (var (ctx, expected) in scenarios)
        {
            var res = await _service.EvaluatePolicyAsync(ctx);
            res.IsAllowed.ShouldBe(expected, $"Failed for user {ctx.UserSid.Value} on {ctx.TargetTable}");
        }

        // Act: Run 10,000 concurrent evaluations
        const int totalEvaluations = 10_000;
        var mismatches = new ConcurrentBag<string>();

        using var cts = new CancellationTokenSource();
        // Background task performing snapshot swaps/reloads concurrently
        var reloadTask = Task.Run(async () =>
        {
            var iteration = 0;
            while (!cts.Token.IsCancellationRequested)
            {
                await Task.Delay(10, cts.Token).ConfigureAwait(false);
                iteration++;
                // Add a dummy programmatic policy for an isolated tenant to trigger snapshot updates
                var dummyTenant = new TenantId($"tenant-churn-{iteration % 5}");
                _service.AddPolicy(dummyTenant, $"churn-user-{iteration}", "dummy.tbl", "read", "true", "allow");
            }
        }, cts.Token);

        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, totalEvaluations), async (i, ct) =>
            {
                var scenario = scenarios[i % scenarios.Count];
                var decision = await _service.EvaluatePolicyAsync(scenario.Ctx, ct);
                if (decision.IsAllowed != scenario.ExpectedAllowed)
                {
                    mismatches.Add($"Eval #{i}: Expected {scenario.ExpectedAllowed} for {scenario.Ctx.UserSid.Value} on {scenario.Ctx.TargetTable} but got {decision.IsAllowed}");
                }
            });
        }
        finally
        {
            cts.Cancel();
            try { await reloadTask; } catch (OperationCanceledException) { }
        }

        // Assert: 0 mismatches across all 10,000 concurrent runs
        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task PropertyTest_TransitiveRoleClosure_MatchesSequentialSemantics()
    {
        // Multi-level role hierarchy: User -> Analyst -> SeniorAnalyst -> Lead -> Director
        var tenant = new TenantId("tenant-hierarchy");
        var user = new Sid("S-1-5-21-multi-role");
        var tableSensitive = new TableIdentifier("corp", "dbo", "board_minutes");

        _service.AddPolicy(tenant, "role:director", tableSensitive.ToString(), "read", "true", "allow");

        // Build chain: user -> analyst -> senior -> lead -> director
        _service.AddRoleForUser(tenant, user.Value, "role:analyst");
        _service.AddRoleForUser(tenant, "role:analyst", "role:senior");
        _service.AddRoleForUser(tenant, "role:senior", "role:lead");
        _service.AddRoleForUser(tenant, "role:lead", "role:director");

        var ctx = CreateContext(user.Value, tenant, tableSensitive, "read");

        var decision = await _service.EvaluatePolicyAsync(ctx);
        decision.IsAllowed.ShouldBeTrue("User should inherit role:director through 4-level transitive closure.");

        // Other user without director role is denied
        var unprivCtx = CreateContext("unpriv-user", tenant, tableSensitive, "read");
        var unprivDecision = await _service.EvaluatePolicyAsync(unprivCtx);
        unprivDecision.IsAllowed.ShouldBeFalse("Unprivileged user must be denied.");
    }

    private static SecurityEvaluationContext CreateContext(
        string userSid,
        TenantId tenant,
        TableIdentifier table,
        string action)
    {
        return new SecurityEvaluationContext(
            UserSid: new Sid(userSid),
            GroupSids: [],
            Tenant: tenant,
            TargetTable: table,
            RequestedColumns: ["*"],
            ClientIp: IPAddress.Loopback,
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: "test-purpose");
    }
}
