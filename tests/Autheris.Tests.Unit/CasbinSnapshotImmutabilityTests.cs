#pragma warning disable CA2012

namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class CasbinSnapshotImmutabilityTests
{
    private static readonly TableIdentifier HrTable = new("hr", "dbo", "employees");
    private static readonly TableIdentifier SalariesTable = new("hr", "dbo", "salaries");
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private static SecurityEvaluationContext CreateContext(
        TenantId tenant,
        string subject,
        TableIdentifier table,
        IReadOnlyCollection<Sid>? groups = null) =>
        new(
            UserSid: new Sid(subject),
            GroupSids: groups ?? Array.Empty<Sid>(),
            Tenant: tenant,
            TargetTable: table,
            RequestedColumns: ["id"],
            ClientIp: IPAddress.Parse("10.1.2.3"),
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: "AUDIT");

    [Fact]
    public async Task Test01_RemovedWildcardAllow_NoLongerActiveForTenantWithOwnFile()
    {
        using var casbin = new CasbinEnforcementService();

        // Tenant A has its own policy file with allow for alice
        casbin.LoadPolicyFromText(TenantA, "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Global file has wildcard allow for bob
        casbin.LoadPolicyFromText("p, bob, *, hr.dbo.employees, read, true, allow\n");

        // Both alice and bob should have access
        var aliceEval1 = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable));
        aliceEval1.IsAllowed.ShouldBeTrue();

        var bobEval1 = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "bob", HrTable));
        bobEval1.IsAllowed.ShouldBeTrue();

        // Reload global file WITHOUT bob's wildcard rule
        casbin.LoadPolicyFromText("p, charlie, *, hr.dbo.employees, read, true, allow\n");

        // Alice still has access via tenant file
        var aliceEval2 = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable));
        aliceEval2.IsAllowed.ShouldBeTrue();

        // Charlie has access via new global wildcard rule
        var charlieEval = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "charlie", HrTable));
        charlieEval.IsAllowed.ShouldBeTrue();

        // Bob MUST NOT have access anymore
        var bobEval2 = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "bob", HrTable));
        bobEval2.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public void Test02_RepeatedReloads_DoNotAccumulateDuplicateRules()
    {
        using var casbin = new CasbinEnforcementService();

        // 1 own rule for tenant-a
        casbin.LoadPolicyFromText(TenantA, "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Global file with 1 wildcard rule
        casbin.LoadPolicyFromText("p, bob, *, hr.dbo.employees, read, true, allow\n");

        var countFirst = casbin.DiagnosticPolicyCount(TenantA.Value);
        countFirst.ShouldBe(2);

        // Reload global file again with same content
        casbin.LoadPolicyFromText("p, bob, *, hr.dbo.employees, read, true, allow\n");

        var countSecond = casbin.DiagnosticPolicyCount(TenantA.Value);
        countSecond.ShouldBe(2);

        // Reload a third time
        casbin.LoadPolicyFromText("p, bob, *, hr.dbo.employees, read, true, allow\n");

        var countThird = casbin.DiagnosticPolicyCount(TenantA.Value);
        countThird.ShouldBe(2);
    }

    [Fact]
    public void Test03_AddPolicy_ReplacesEnforcerInstanceAndLeavesOldEnforcerIntact()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.LoadPolicyFromText(TenantA, "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");

        var oldEnforcer = casbin.DiagnosticEnforcerIdentity(TenantA.Value);
        var oldCount = casbin.DiagnosticPolicyCount(TenantA.Value);
        oldCount.ShouldBe(1);

        // Add a programmatic policy for tenant-a
        casbin.AddPolicy(TenantA, "bob", "hr.dbo.employees", "read", "true", "allow");

        var newEnforcer = casbin.DiagnosticEnforcerIdentity(TenantA.Value);
        var newCount = casbin.DiagnosticPolicyCount(TenantA.Value);

        // Enforcer instance in snapshot must be a new, distinct instance
        newEnforcer.ShouldNotBeSameAs(oldEnforcer);
        newCount.ShouldBe(2);

        // The old enforcer instance must NOT have been mutated
        CasbinEnforcementService.DiagnosticEnforcerPolicyCount(oldEnforcer).ShouldBe(1);
    }

    [Fact]
    public async Task Test04_GlobalRoles_ApplyToTenantsWithOwnPolicyFile()
    {
        using var casbin = new CasbinEnforcementService();

        // Tenant A has own file with rule for role 'auditor'
        casbin.LoadPolicyFromText(TenantA, "p, auditor, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Global file defines role assignment: alice is an auditor
        casbin.LoadPolicyFromText(
            "p, dummy, *, dummy.table, read, true, allow\n" +
            "g, alice, auditor\n");

        // Alice accessing hr.employees for tenant-a should be allowed through global role 'auditor'
        var decision = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable));
        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Test05_ConcurrencyStress_ParallelReadsAndMutations()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.LoadPolicyFromText(TenantA, "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");
        casbin.LoadPolicyFromText("p, bob, *, hr.dbo.employees, read, true, allow\n");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        // 1 mutating task
        var writeTask = Task.Run(async () =>
        {
            int iteration = 0;
            try
            {
                while (!cts.Token.IsCancellationRequested && iteration < 200)
                {
                    iteration++;
                    if (iteration % 3 == 0)
                    {
                        casbin.LoadPolicyFromText(
                            $"p, bob_{iteration % 5}, *, hr.dbo.employees, read, true, allow\n");
                    }
                    else if (iteration % 3 == 1)
                    {
                        casbin.LoadPolicyFromText(
                            TenantA,
                            $"p, alice, tenant-a, hr.dbo.employees, read, true, allow\np, user_{iteration % 5}, tenant-a, hr.dbo.employees, read, true, allow\n");
                    }
                    else
                    {
                        casbin.AddPolicy(TenantA, $"prog_{iteration}", "hr.dbo.employees", "read", "true", "allow");
                    }

                    await Task.Delay(1);
                }
            }
            catch (Exception ex)
            {
                exceptions.Add(ex);
            }
        });

        // 8 reader tasks
        var readTasks = new List<Task>();
        for (int i = 0; i < 8; i++)
        {
            readTasks.Add(Task.Run(async () =>
            {
                try
                {
                    for (int j = 0; j < 1000; j++)
                    {
                        var dec = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable));
                        // Alice should always be allowed
                        dec.IsAllowed.ShouldBeTrue();
                    }
                }
                catch (Exception ex)
                {
                    exceptions.Add(ex);
                }
            }));
        }

        await Task.WhenAll(readTasks);
        cts.Cancel();
        await writeTask;

        exceptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Test06_UnionOfGlobalAndTenantRules()
    {
        using var casbin = new CasbinEnforcementService();

        // Global file contains rule for tenant-a: alice can read hr.employees
        casbin.LoadPolicyFromText("p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Tenant file contains rule for tenant-a: bob can read hr.salaries
        casbin.LoadPolicyFromText(TenantA, "p, bob, tenant-a, hr.dbo.salaries, read, true, allow\n");

        // Both rules must be active for tenant-a
        var aliceEmployees = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable));
        aliceEmployees.IsAllowed.ShouldBeTrue();

        var bobSalaries = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "bob", SalariesTable));
        bobSalaries.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void Test07_TenantFileWithForeignTenantOrWildcard_ThrowsFormatException()
    {
        using var casbin = new CasbinEnforcementService();

        // Tenant file for tenant-a trying to define a rule for tenant-b
        Should.Throw<FormatException>(() =>
            casbin.LoadPolicyFromText(TenantA, "p, alice, tenant-b, hr.dbo.employees, read, true, allow\n"));

        // Tenant file for tenant-a trying to define a wildcard tenant rule
        Should.Throw<FormatException>(() =>
            casbin.LoadPolicyFromText(TenantA, "p, alice, *, hr.dbo.employees, read, true, allow\n"));
    }

    [Fact]
    public async Task Test08_E1_GlobalFileWithOnlyGroupingRulesWhenPoliciesActive_ThrowsInvalidOperationException()
    {
        using var casbin = new CasbinEnforcementService();

        // Active policy set with p-rules exists
        casbin.LoadPolicyFromText("p, alice, *, hr.dbo.employees, read, true, allow\n");

        // Attempting to reload global file with only g-rules must be rejected fail-closed
        Should.Throw<InvalidOperationException>(() =>
            casbin.LoadPolicyFromText("g, bob, admin\n"));

        // Prior policy must remain active
        var aliceEval = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable));
        aliceEval.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Test09_AddPolicyWithWildcardTenant_AffectsTenantWithOwnPolicyFile()
    {
        using var casbin = new CasbinEnforcementService();

        // Tenant A has own policy file
        casbin.LoadPolicyFromText(TenantA, "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Programmatic AddPolicy with wildcard tenant '*'
        casbin.AddWildcardPolicy("charlie", "hr.dbo.employees", "read", "true", "allow");

        // Charlie should now be allowed for tenant-a
        var decision = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "charlie", HrTable));
        decision.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Test10_ReloadPoliciesWithoutFile_RemovesOnlyProgrammaticRules()
    {
        using var casbin = new CasbinEnforcementService();

        // Global file has rule for alice in tenant-a
        casbin.LoadPolicyFromText("p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Programmatic rule for bob in tenant-a
        casbin.AddPolicy(TenantA, "bob", "hr.dbo.employees", "read", "true", "allow");

        // Both are allowed
        (await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable))).IsAllowed.ShouldBeTrue();
        (await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "bob", HrTable))).IsAllowed.ShouldBeTrue();

        // Reload tenant policies when no file exists for tenant-a
        await casbin.ReloadPoliciesAsync(TenantA);

        // Global file rule for alice must still be active
        (await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable))).IsAllowed.ShouldBeTrue();

        // Programmatic rule for bob must be gone
        (await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "bob", HrTable))).IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Test11_F1_TenantFileWithOnlyGroupingRulesWhenPoliciesActive_ThrowsInvalidOperationException_AndPreservesSnapshot()
    {
        using var casbin = new CasbinEnforcementService();

        // Tenant A has active p-rules in its tenant file
        casbin.LoadPolicyFromText(TenantA, "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Reloading tenant file with only grouping rules (e.g. truncated file) must be rejected fail-closed
        Should.Throw<InvalidOperationException>(() =>
            casbin.LoadPolicyFromText(TenantA, "g, alice, role:viewer\n"));

        // Prior policy must remain active
        var aliceEval = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable));
        aliceEval.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Test12_F2_GlobalReloadWithOnlyGroupingRules_AllowedWhenGlobalHadNoPRulesEvenIfTenantsHaveRules()
    {
        using var casbin = new CasbinEnforcementService();

        // Global file initially has only grouping rules, no p-rules
        casbin.LoadPolicyFromText("g, alice, role:viewer\n");

        // Tenant A has active p-rules
        casbin.LoadPolicyFromText(TenantA, "p, role:viewer, tenant-a, hr.dbo.employees, read, true, allow\n");

        // Global reload with another g-rule should NOT throw, because global source never had p-rules
        Should.NotThrow(() =>
            casbin.LoadPolicyFromText("g, alice, role:viewer\ng, bob, role:viewer\n"));

        // Both alice and bob should now be allowed for tenant-a
        (await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "alice", HrTable))).IsAllowed.ShouldBeTrue();
        (await casbin.EvaluatePolicyAsync(CreateContext(TenantA, "bob", HrTable))).IsAllowed.ShouldBeTrue();
    }
}
