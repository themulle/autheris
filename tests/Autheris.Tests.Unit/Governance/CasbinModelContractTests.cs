#pragma warning disable CA2012

namespace Autheris.Tests.Unit.Governance;

using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class CasbinModelContractTests
{
    private static readonly TenantId TenantA = new("tenant-a");

    private static string CreateModel(string matcher, string effect = "some(where (p.eft == allow)) && !some(where (p.eft == deny))", string policyDef = "sub, tenant, obj, act, sub_rule, eft") =>
        $@"
[request_definition]
r = sub, tenant, obj, act, ctx

[policy_definition]
p = {policyDef}

[role_definition]
g = _, _

[policy_effect]
e = {effect}

[matchers]
m = {matcher}
";

    [Fact]
    public void Verify_WithDefaultModelText_SucceedsWithWildcardTrue()
    {
        var capabilities = CasbinModelContract.Verify(CasbinEnforcementService.DefaultModelText);
        capabilities.SupportsWildcardTenant.ShouldBeTrue();
    }

    [Fact]
    public void Verify_WithRbacWithAbacConfFile_SucceedsWithWildcardTrue()
    {
        var configPath = Path.Combine(AppContext.BaseDirectory, "../../../../../src/Autheris.Application/Governance/rbac_with_abac.conf");
        if (!File.Exists(configPath))
        {
            configPath = Path.Combine(Directory.GetCurrentDirectory(), "src/Autheris.Application/Governance/rbac_with_abac.conf");
        }

        File.Exists(configPath).ShouldBeTrue($"Configuration file should exist at '{configPath}'");
        var modelText = File.ReadAllText(configPath);

        var capabilities = CasbinModelContract.Verify(modelText);
        capabilities.SupportsWildcardTenant.ShouldBeTrue();
    }

    [Fact]
    public void Verify_WithoutWildcardTenantClause_SucceedsWithWildcardFalse()
    {
        var model = CreateModel("g(r.sub, p.sub) && r.tenant == p.tenant && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\") && eval(p.sub_rule)");

        var capabilities = CasbinModelContract.Verify(model);
        capabilities.SupportsWildcardTenant.ShouldBeFalse();
    }

    [Fact]
    public void Verify_WithAlternativeWildcardSyntax_SucceedsWithWildcardTrue()
    {
        // Alternative syntax with spaces and reversed operands: ("*" == p.tenant || r.tenant == p.tenant)
        var model = CreateModel("g(r.sub, p.sub) && (\"*\"  ==  p.tenant || r.tenant == p.tenant) && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\") && eval(p.sub_rule)");

        var capabilities = CasbinModelContract.Verify(model);
        capabilities.SupportsWildcardTenant.ShouldBeTrue();
    }

    [Fact]
    public void Verify_WithWildcardInCommentOnly_SucceedsWithWildcardFalse()
    {
        // Wildcard appears only in comment, not in the actual matcher
        var model = @"
[request_definition]
r = sub, tenant, obj, act, ctx

[policy_definition]
p = sub, tenant, obj, act, sub_rule, eft

[role_definition]
g = _, _

[policy_effect]
e = some(where (p.eft == allow)) && !some(where (p.eft == deny))

# Note: previously p.tenant == ""*"" was supported here
[matchers]
m = g(r.sub, p.sub) && r.tenant == p.tenant && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == ""*"") && eval(p.sub_rule)
";

        var capabilities = CasbinModelContract.Verify(model);
        capabilities.SupportsWildcardTenant.ShouldBeFalse();
    }

    [Fact]
    public void Verify_WithMissingParenthesesPrecedenceBug_ThrowsCasbinModelValidationException_ViolatingW2()
    {
        // Missing parentheses: g(r.sub, p.sub) && r.tenant == p.tenant || p.tenant == "*" ...
        // Due to && preceding ||, any '*' rule bypasses subject check!
        var model = CreateModel("g(r.sub, p.sub) && r.tenant == p.tenant || p.tenant == \"*\" && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\") && eval(p.sub_rule)");

        var ex = Should.Throw<CasbinModelValidationException>(() => CasbinModelContract.Verify(model));
        ex.Violations.ShouldContain(v => v.StartsWith("W2", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WithoutTenantCheck_ThrowsCasbinModelValidationException_ViolatingM2()
    {
        // Matcher completely omits tenant check -> M2 (tenant separation) fails
        var model = CreateModel("g(r.sub, p.sub) && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\") && eval(p.sub_rule)");

        var ex = Should.Throw<CasbinModelValidationException>(() => CasbinModelContract.Verify(model));
        ex.Violations.ShouldContain(v => v.StartsWith("M2", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WithAllowOverridePolicyEffect_ThrowsCasbinModelValidationException_ViolatingM5()
    {
        // Effect does not enforce deny precedence -> M5 fails
        var model = CreateModel(
            "g(r.sub, p.sub) && (r.tenant == p.tenant || p.tenant == \"*\") && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\") && eval(p.sub_rule)",
            effect: "some(where (p.eft == allow))");

        var ex = Should.Throw<CasbinModelValidationException>(() => CasbinModelContract.Verify(model));
        ex.Violations.ShouldContain(v => v.StartsWith("M5", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WithoutEvalSubRule_ThrowsCasbinModelValidationException_ViolatingM6()
    {
        // Matcher does not evaluate sub_rule -> M6 fails
        var model = CreateModel("g(r.sub, p.sub) && (r.tenant == p.tenant || p.tenant == \"*\") && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\")");

        var ex = Should.Throw<CasbinModelValidationException>(() => CasbinModelContract.Verify(model));
        ex.Violations.ShouldContain(v => v.StartsWith("M6", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WithoutRoles_ThrowsCasbinModelValidationException_ViolatingM7()
    {
        // Direct subject equality instead of g(r.sub, p.sub) -> M7 fails
        var model = CreateModel("r.sub == p.sub && (r.tenant == p.tenant || p.tenant == \"*\") && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\") && eval(p.sub_rule)");

        var ex = Should.Throw<CasbinModelValidationException>(() => CasbinModelContract.Verify(model));
        ex.Violations.ShouldContain(v => v.StartsWith("M7", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WithIncorrectArity_ThrowsCasbinModelValidationException_ViolatingM1()
    {
        // Policy definition has wrong arity
        var model = CreateModel(
            "g(r.sub, p.sub) && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\")",
            policyDef: "sub, obj, act");

        var ex = Should.Throw<CasbinModelValidationException>(() => CasbinModelContract.Verify(model));
        ex.Violations.ShouldContain(v => v.StartsWith("M1", StringComparison.Ordinal));
    }

    [Fact]
    public void Verify_WithSyntaxError_ThrowsCasbinModelValidationException()
    {
        var model = CreateModel("this is not a valid matcher syntax !!! &&&");

        Should.Throw<CasbinModelValidationException>(() => CasbinModelContract.Verify(model));
    }

    [Fact]
    public void CasbinEnforcementService_WithModelWithoutWildcard_RejectsWildcardRules_WithProbeW1Message()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            var modelNoWildcard = CreateModel("g(r.sub, p.sub) && r.tenant == p.tenant && keyMatch2(r.obj, p.obj) && (r.act == p.act || p.act == \"*\") && eval(p.sub_rule)");
            File.WriteAllText(tempFile, modelNoWildcard);

            using var casbin = new CasbinEnforcementService(tempFile);

            var ex = Should.Throw<InvalidOperationException>(() =>
                casbin.AddWildcardPolicy("alice", "hr.employees", "read"));

            ex.Message.ShouldContain("W1");
            ex.Message.ShouldContain("wildcard tenants");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CasbinEnforcementService_WithInvalidModel_ThrowsCasbinModelValidationException()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            // Invalid model (missing tenant check)
            var brokenModel = CreateModel("g(r.sub, p.sub) && keyMatch2(r.obj, p.obj) && eval(p.sub_rule)");
            File.WriteAllText(tempFile, brokenModel);

            Should.Throw<CasbinModelValidationException>(() => new CasbinEnforcementService(tempFile));
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void CasbinEnforcementService_WithMissingModelFile_ThrowsFileNotFoundException()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"missing_model_{Guid.NewGuid():N}.conf");

        Should.Throw<FileNotFoundException>(() => new CasbinEnforcementService(missingPath));
    }

    [Fact]
    public async Task PolicySimulationService_MatchesEnforcement_ForWildcardRules()
    {
        var auditRepo = Substitute.For<Autheris.Application.Interfaces.IAuditLogRepository>();
        auditRepo.QueryAuditLogsAsync(
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
                    ActorSid = new Sid("S-1-5-21-ALICE"),
                    TargetTable = "hr.employees",
                    EventType = "read",
                    Decision = "ALLOW"
                }
            });

        var simService = new PolicySimulationService(auditRepo);
        var simResult = await simService.SimulateAsync(
            new PolicySimulationRequest(
                DraftPolicyCsv: "p, S-1-5-21-ALICE, *, hr.employees, read, true, allow",
                Tenant: TenantA,
                TargetTable: "hr.employees",
                Limit: 10),
            TenantA);

        simResult.AllowedInSimulation.ShouldBe(1);
        simResult.DeniedInSimulation.ShouldBe(0);
    }
}
