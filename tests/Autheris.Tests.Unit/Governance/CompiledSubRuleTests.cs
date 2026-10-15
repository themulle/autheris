#pragma warning disable CA2012

namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>WP-PF-03 / SEC-SC-07: compiled sub_rule delegates, polarity on compile failure, sandbox.</summary>
public sealed class CompiledSubRuleTests
{
    private static readonly TableIdentifier Table = new("hr", "dbo", "employees");
    private static readonly TenantId Tenant = new("tenant-a");

    private static SecurityEvaluationContext Ctx(string user = "alice", string? department = "HR", int hour = 10) =>
        new(
            UserSid: new Sid(user),
            GroupSids: [new Sid("g-staff")],
            Tenant: Tenant,
            TargetTable: Table,
            RequestedColumns: ["id"],
            ClientIp: IPAddress.Parse("10.1.2.3"),
            Timestamp: new DateTimeOffset(2026, 10, 15, hour, 0, 0, TimeSpan.Zero),
            PurposeId: "AUDIT",
            Attributes: department is null ? null : new Dictionary<string, object?> { ["department"] = department });

    // The pre-WP-PF-03 implementation, kept as the equivalence oracle.
    private static bool? Oracle(string subRule, SecurityEvaluationContext context, string subject, string tableStr, string act)
    {
        try
        {
            var interpreter = new DynamicExpresso.Interpreter();
            interpreter.SetVariable("r", new { ctx = context, sub = subject, tenant = context.Tenant.Value, obj = tableStr, act });
            interpreter.SetVariable("ctx", context);
            interpreter.SetVariable("context", context);
            var normalized = Regex.Replace(subRule, @"'([^']{2,})'", "\"$1\"");
            return interpreter.Eval(normalized) is bool b ? b : null;
        }
        catch
        {
            return null;
        }
    }

    public static TheoryData<string> Expressions() => new()
    {
        "r.ctx.Department == 'HR'",
        "r.ctx.Department == 'Finance'",
        "r.sub == 'alice'",
        "r.sub == 'g-staff'",
        "r.tenant == 'tenant-a' && r.act == 'read'",
        "r.obj == 'hr.dbo.employees'",
        "ctx.Department == 'HR' || context.Region == 'EU'",
        "ctx.Timestamp.Hour >= 8 && ctx.Timestamp.Hour < 18",
        "ctx.Timestamp.Hour > 12",
        "!(r.ctx.Department == 'HR')",
        "r.ctx.Department",              // not boolean -> unknown
        "r.ctx.Nonexistent == 'x'",      // does not compile -> unknown
        "((((",                           // syntax error -> unknown
        "ctx.GroupSids.Count == 1",
    };

    [Theory]
    [MemberData(nameof(Expressions))]
    public void Compiled_IsEquivalentToInterpreterOracle(string expression)
    {
        foreach (var context in new[] { Ctx(), Ctx(department: null), Ctx(department: "Finance", hour: 20) })
        {
            foreach (var subject in new[] { "alice", "g-staff" })
            {
                var expected = Oracle(expression, context, subject, "hr.dbo.employees", "read");
                var actual = CompiledSubRuleCache.Evaluate(expression, context, subject, "hr.dbo.employees", "read");
                actual.ShouldBe(expected, $"expression: {expression}");
            }
        }
    }

    [Fact]
    public void SEC_SC_07_CompileFailure_IsUnknown_AndStaysUnknown()
    {
        CompiledSubRuleCache.Evaluate("r.ctx.Nonexistent == 'x'", Ctx(), "alice", "t", "read").ShouldBeNull();
        CompiledSubRuleCache.Evaluate("r.ctx.Nonexistent == 'x'", Ctx(), "alice", "t", "read").ShouldBeNull();
    }

    [Fact]
    public void Rule_IsCompiledOnce_AndReused()
    {
        CompiledSubRuleCache.Clear();
        for (var i = 0; i < 50; i++)
        {
            CompiledSubRuleCache.Evaluate("r.sub == 'alice'", Ctx(), "alice", "t", "read").ShouldBe(true);
        }

        CompiledSubRuleCache.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("ctx.PurposeId = 'X'")]
    [InlineData("r.sub = 'bob'")]
    [InlineData("x = 5")]
    [InlineData("ctx.Timestamp += 1")]
    [InlineData("typeof(System.IO.File) != null")]
    [InlineData("System.Diagnostics.Process.GetCurrentProcess() != null")]
    [InlineData("new System.Object() != null")]
    public void SEC_SC_07_Sandbox_AssignmentAndTypeAccess_AreUnknown(string expression)
    {
        CompiledSubRuleCache.Evaluate(expression, Ctx(), "alice", "t", "read").ShouldBeNull();
    }

    [Fact]
    public async Task SEC_SC_07_AllowRule_WithCompileFailure_DoesNotMatch()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.LoadPolicyFromText(Tenant, "p, alice, tenant-a, hr.dbo.employees, read, r.ctx.Nonexistent == 'x', allow\n");

        var decision = await casbin.EvaluatePolicyAsync(Ctx());

        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task SEC_SC_07_DenyRule_WithCompileFailure_Matches()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.LoadPolicyFromText(Tenant,
            "p, alice, tenant-a, hr.dbo.employees, read, true, allow\n" +
            "p, alice, tenant-a, hr.dbo.employees, read, r.ctx.Nonexistent == 'x', deny\n");

        var decision = await casbin.EvaluatePolicyAsync(Ctx());

        decision.IsAllowed.ShouldBeFalse();
    }
}
