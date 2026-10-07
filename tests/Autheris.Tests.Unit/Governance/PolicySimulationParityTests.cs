#pragma warning disable CA2012

namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class PolicySimulationParityTests
{
    private static readonly TableIdentifier HrTable = new("hr", "dbo", "employees");
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly Sid AliceSid = new("alice");

    private static IAuditLogRepository CreateAuditRepo(
        Sid actorSid,
        string targetTable = "hr.dbo.employees",
        string action = "read",
        string decision = "ALLOW")
    {
        var repo = Substitute.For<IAuditLogRepository>();
        repo.QueryAuditLogsAsync(
            Arg.Any<string?>(),
            Arg.Any<Sid?>(),
            Arg.Any<DateTimeOffset?>(),
            Arg.Any<int>(),
            Arg.Any<TenantId?>(),
            Arg.Any<System.Threading.CancellationToken>())
            .Returns(new List<AuditLogEntry>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = DateTimeOffset.UtcNow,
                    ActorSid = actorSid,
                    TargetTable = targetTable,
                    EventType = action,
                    Decision = decision
                }
            });
        return repo;
    }

    private static SecurityEvaluationContext CreateContext(
        TenantId tenant,
        Sid subject,
        TableIdentifier table) =>
        new(
            UserSid: subject,
            GroupSids: Array.Empty<Sid>(),
            Tenant: tenant,
            TargetTable: table,
            RequestedColumns: ["id"],
            ClientIp: IPAddress.Parse("127.0.0.1"),
            Timestamp: DateTimeOffset.UtcNow,
            PurposeId: "SIM_PARITY");

    [Fact]
    public async Task Parity_BasicAllowPolicy_BothSimulationAndEnforcementAllow()
    {
        using var casbin = new CasbinEnforcementService();
        var auditRepo = CreateAuditRepo(AliceSid);
        var simService = new PolicySimulationService(auditRepo, casbin);

        const string policyCsv = "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n";

        // Simulation
        var simResult = await simService.SimulateAsync(
            new PolicySimulationRequest(DraftPolicyCsv: policyCsv, Tenant: TenantA, TargetTable: "hr.dbo.employees", Limit: 10),
            TenantA);
        simResult.AllowedInSimulation.ShouldBe(1);
        simResult.DeniedInSimulation.ShouldBe(0);

        // Enforcement
        casbin.LoadPolicyFromText(TenantA, policyCsv);
        var eval = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, AliceSid, HrTable));
        eval.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Parity_ExplicitDenyPolicy_BothSimulationAndEnforcementDeny()
    {
        using var casbin = new CasbinEnforcementService();
        var auditRepo = CreateAuditRepo(AliceSid);
        var simService = new PolicySimulationService(auditRepo, casbin);

        const string policyCsv = "p, alice, tenant-a, hr.dbo.employees, read, true, deny\n";

        // Simulation
        var simResult = await simService.SimulateAsync(
            new PolicySimulationRequest(DraftPolicyCsv: policyCsv, Tenant: TenantA, TargetTable: "hr.dbo.employees", Limit: 10),
            TenantA);
        simResult.AllowedInSimulation.ShouldBe(0);
        simResult.DeniedInSimulation.ShouldBe(1);

        // Enforcement
        casbin.LoadPolicyFromText(TenantA, policyCsv);
        var eval = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, AliceSid, HrTable));
        eval.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task Parity_WildcardTenantPolicy_BothSimulationAndEnforcementAllow()
    {
        using var casbin = new CasbinEnforcementService();
        var auditRepo = CreateAuditRepo(AliceSid);
        var simService = new PolicySimulationService(auditRepo, casbin);

        const string policyCsv = "p, alice, *, hr.dbo.employees, read, true, allow\n";

        // Simulation
        var simResult = await simService.SimulateAsync(
            new PolicySimulationRequest(DraftPolicyCsv: policyCsv, Tenant: TenantA, TargetTable: "hr.dbo.employees", Limit: 10),
            TenantA);
        simResult.AllowedInSimulation.ShouldBe(1);
        simResult.DeniedInSimulation.ShouldBe(0);

        // Enforcement
        casbin.LoadPolicyFromText(policyCsv);
        var eval = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, AliceSid, HrTable));
        eval.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Parity_InvalidEffect_BothSimulationAndEnforcementThrowFormatException()
    {
        using var casbin = new CasbinEnforcementService();
        var auditRepo = CreateAuditRepo(AliceSid);
        var simService = new PolicySimulationService(auditRepo, casbin);

        const string invalidPolicyCsv = "p, alice, tenant-a, hr.dbo.employees, read, true, unsupported_effect\n";

        // Simulation must reject invalid effect with FormatException
        await Should.ThrowAsync<FormatException>(() =>
            simService.SimulateAsync(
                new PolicySimulationRequest(DraftPolicyCsv: invalidPolicyCsv, Tenant: TenantA, TargetTable: "hr.dbo.employees", Limit: 10),
                TenantA));

        // Enforcement must also reject invalid effect with FormatException
        Should.Throw<FormatException>(() =>
            casbin.LoadPolicyFromText(TenantA, invalidPolicyCsv));
    }

    [Fact]
    public async Task Parity_ModelWithoutWildcard_BothSimulationAndEnforcementRejectWildcardRule()
    {
        const string nonWildcardModel = @"
[request_definition]
r = sub, tenant, obj, act, ctx

[policy_definition]
p = sub, tenant, obj, act, sub_rule, eft

[role_definition]
g = _, _

[policy_effect]
e = some(where (p.eft == allow)) && !some(where (p.eft == deny))

[matchers]
m = g(r.sub, p.sub) && r.tenant == p.tenant && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == ""*"") && eval(p.sub_rule)
";
        var tempModelFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempModelFile, nonWildcardModel);

            using var casbin = new CasbinEnforcementService(tempModelFile);
            var auditRepo = CreateAuditRepo(AliceSid);
            var simService = new PolicySimulationService(auditRepo, casbin);

            const string wildcardPolicyCsv = "p, alice, *, hr.dbo.employees, read, true, allow\n";

            // Simulation must reject wildcard rule because model does not support wildcard tenant
            var simEx = await Should.ThrowAsync<InvalidOperationException>(() =>
                simService.SimulateAsync(
                    new PolicySimulationRequest(DraftPolicyCsv: wildcardPolicyCsv, Tenant: TenantA, TargetTable: "hr.dbo.employees", Limit: 10),
                    TenantA));
            simEx.Message.ShouldContain("Wildcard-Mandanten");

            // Enforcement must reject wildcard rule because model does not support wildcard tenant
            var enfEx = Should.Throw<InvalidOperationException>(() =>
                casbin.LoadPolicyFromText(wildcardPolicyCsv));
            enfEx.Message.ShouldContain("Wildcard-Mandanten");
        }
        finally
        {
            if (File.Exists(tempModelFile))
            {
                File.Delete(tempModelFile);
            }
        }
    }

    [Fact]
    public async Task Parity_AbacContextSubRule_BothSimulationAndEnforcementEvaluateContextAttributes()
    {
        using var casbin = new CasbinEnforcementService();
        var auditRepo = CreateAuditRepo(AliceSid);
        var simService = new PolicySimulationService(auditRepo, casbin);

        // Sub-rule requiring context.Tenant.Value to be 'tenant-a'
        const string policyCsv = "p, alice, tenant-a, hr.dbo.employees, read, r.ctx.Tenant.Value == 'tenant-a', allow\n";

        // Simulation
        var simResult = await simService.SimulateAsync(
            new PolicySimulationRequest(DraftPolicyCsv: policyCsv, Tenant: TenantA, TargetTable: "hr.dbo.employees", Limit: 10),
            TenantA);
        simResult.AllowedInSimulation.ShouldBe(1);

        // Enforcement
        casbin.LoadPolicyFromText(TenantA, policyCsv);
        var eval = await casbin.EvaluatePolicyAsync(CreateContext(TenantA, AliceSid, HrTable));
        eval.IsAllowed.ShouldBeTrue();
    }
}
