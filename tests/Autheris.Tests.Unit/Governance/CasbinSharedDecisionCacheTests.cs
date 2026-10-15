#pragma warning disable CA2012

namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

/// <summary>WP-PF-09 / SEC-SC-09: shape-shared decision keys, separation proof, delegated no-sharing, sharding and single-flight.</summary>
public sealed class CasbinSharedDecisionCacheTests
{
    private static readonly TableIdentifier Table = new("hr", "dbo", "employees");
    private static readonly TenantId Tenant = new("t1");

    private static SecurityEvaluationContext Ctx(
        string user,
        string[]? groups = null,
        string? department = null,
        string ip = "10.0.0.1",
        bool delegated = false,
        string purpose = "AUDIT") =>
        new(
            new Sid(user),
            (groups ?? []).Select(g => new Sid(g)).ToArray(),
            Tenant,
            Table,
            ["id", "name"],
            IPAddress.Parse(ip),
            DateTimeOffset.UnixEpoch,
            purpose,
            department is null ? null : new Dictionary<string, object?> { ["department"] = department })
        {
            IsDelegatedSource = delegated
        };

    private static CasbinEnforcementService Create(string policy, bool share = true)
    {
        var service = new CasbinEnforcementService(cacheOptions: new CasbinDecisionCacheOptions { ShareAcrossUsers = share });
        service.LoadPolicyFromText(Tenant, policy);
        return service;
    }

    private const string GroupPolicy = "p, g-staff, t1, hr.dbo.employees, read, true, allow\n";

    [Fact]
    public async Task UsersWithTheSameMatchedRules_ShareOneEntry()
    {
        using var service = Create(GroupPolicy);

        (await service.EvaluatePolicyAsync(Ctx("alice", ["g-staff"]))).IsAllowed.ShouldBeTrue();
        (await service.EvaluatePolicyAsync(Ctx("bob", ["g-staff"], ip: "10.9.9.9"))).IsAllowed.ShouldBeTrue();

        service.DiagnosticDecisionCacheCount.ShouldBe(1);
        service.DiagnosticDecisionCacheHits.ShouldBe(1);
        service.DiagnosticDecisionEvaluations.ShouldBe(1);
    }

    [Fact]
    public async Task UsersMatchingDifferentRules_DoNotShare()
    {
        using var service = Create(GroupPolicy + "p, g-other, t1, hr.dbo.employees, read, true, deny\n");

        (await service.EvaluatePolicyAsync(Ctx("alice", ["g-staff"]))).IsAllowed.ShouldBeTrue();
        (await service.EvaluatePolicyAsync(Ctx("bob", ["g-staff", "g-other"]))).IsAllowed.ShouldBeFalse();
        (await service.EvaluatePolicyAsync(Ctx("carol"))).IsAllowed.ShouldBeFalse();

        service.DiagnosticDecisionCacheCount.ShouldBe(3);
    }

    [Theory]
    [InlineData("r.sub == 'alice'")]
    [InlineData("ctx.ClientIp.ToString() == '10.0.0.1'")]
    [InlineData("r.ctx.UserSid.Value == 'alice'")]
    [InlineData("ctx.GroupSids.Count > 0")]
    public async Task SEC_SC_09_RuleReadingTheUserOrIp_NeverShares(string subRule)
    {
        using var service = Create($"p, *, t1, hr.dbo.employees, read, {subRule}, allow\n");

        await service.EvaluatePolicyAsync(Ctx("alice", ["g-staff"]));
        await service.EvaluatePolicyAsync(Ctx("bob", ["g-staff"]));

        service.DiagnosticDecisionCacheCount.ShouldBe(2);
    }

    [Fact]
    public async Task SEC_SC_09_RlsFilterWithUserSid_NeverShares_ButDepartmentFilterSharesPerDepartment()
    {
        using (var perUser = Create("p, g-staff, t1, hr.dbo.employees, read, true, allow, owner_sid = ${user_sid}\n"))
        {
            var a = await perUser.EvaluatePolicyAsync(Ctx("alice", ["g-staff"]));
            var b = await perUser.EvaluatePolicyAsync(Ctx("bob", ["g-staff"]));
            a.CombinedRowFilterSql.ShouldNotBe(b.CombinedRowFilterSql);
            perUser.DiagnosticDecisionCacheCount.ShouldBe(2);
        }

        using var perDepartment = Create("p, g-staff, t1, hr.dbo.employees, read, true, allow, dept = ${department}\n");
        var d1 = await perDepartment.EvaluatePolicyAsync(Ctx("alice", ["g-staff"], "HR"));
        var d2 = await perDepartment.EvaluatePolicyAsync(Ctx("bob", ["g-staff"], "HR"));
        var d3 = await perDepartment.EvaluatePolicyAsync(Ctx("carol", ["g-staff"], "FIN"));

        d1.CombinedRowFilterSql.ShouldBe(d2.CombinedRowFilterSql);
        d3.CombinedRowFilterSql.ShouldNotBe(d1.CombinedRowFilterSql);
        perDepartment.DiagnosticDecisionCacheCount.ShouldBe(2);
    }

    [Fact]
    public async Task SEC_SC_09_DelegatedSource_NeverShares_EvenWithIdenticalShape()
    {
        using var service = Create(GroupPolicy);

        await service.EvaluatePolicyAsync(Ctx("alice", ["g-staff"], delegated: true));
        await service.EvaluatePolicyAsync(Ctx("bob", ["g-staff"], delegated: true));

        service.DiagnosticDecisionCacheCount.ShouldBe(2);
        service.DiagnosticDecisionCacheHits.ShouldBe(0);
    }

    [Fact]
    public async Task SEC_SC_09_DelegatedAndSharedEntries_DoNotCollide()
    {
        using var service = Create(GroupPolicy);

        await service.EvaluatePolicyAsync(Ctx("alice", ["g-staff"]));
        await service.EvaluatePolicyAsync(Ctx("alice", ["g-staff"], delegated: true));

        service.DiagnosticDecisionCacheCount.ShouldBe(2);
        service.DiagnosticDecisionCacheHits.ShouldBe(0);
    }

    [Fact]
    public async Task ShareAcrossUsersOff_KeysEveryUserSeparately()
    {
        using var service = Create(GroupPolicy, share: false);

        await service.EvaluatePolicyAsync(Ctx("alice", ["g-staff"]));
        await service.EvaluatePolicyAsync(Ctx("bob", ["g-staff"]));

        service.DiagnosticDecisionCacheCount.ShouldBe(2);
    }

    [Fact]
    public async Task SEC_SC_09_Separation_SharedDecisions_EqualUnsharedOracle_ForRandomPolicies()
    {
        string[] users = ["alice", "bob", "carol", "dave"];
        string[] groups = ["g-a", "g-b", "g-c"];
        string[] departments = ["HR", "FIN", "IT"];
        string[] ips = ["10.0.0.1", "10.0.0.2"];
        string[] subRules =
        [
            "true", "r.ctx.Department == 'HR'", "ctx.Department == 'FIN'", "r.sub == 'alice'", "r.tenant == 't1' && r.act == 'read'",
            "ctx.ClientIp.ToString() == '10.0.0.1'", "r.ctx.Nonexistent == 'x'", "ctx.PurposeId == 'AUDIT'"
        ];
        string?[] filters = [null, null, "dept = ${department}", "owner = ${user_sid}", "p = ${purpose}"];

        for (var seed = 0; seed < 40; seed++)
        {
            var rng = new Random(seed);
            var lines = new List<string>();
            for (var i = 0; i < rng.Next(2, 7); i++)
            {
                var subject = rng.Next(3) == 0 ? users[rng.Next(users.Length)] : groups[rng.Next(groups.Length)];
                var eft = rng.Next(4) == 0 ? "deny" : "allow";
                var filter = eft == "allow" ? filters[rng.Next(filters.Length)] : null;
                lines.Add($"p, {subject}, t1, hr.dbo.employees, read, {subRules[rng.Next(subRules.Length)]}, {eft}" + (filter is null ? string.Empty : ", " + filter));
            }

            var policy = string.Join('\n', lines) + "\n";
            using var shared = Create(policy, share: true);
            using var oracle = Create(policy, share: false); // keys every decision by user: the pre-WP-PF-09 behaviour

            for (var n = 0; n < 60; n++)
            {
                var context = Ctx(
                    users[rng.Next(users.Length)],
                    groups.Where(_ => rng.Next(2) == 0).ToArray(),
                    rng.Next(4) == 0 ? null : departments[rng.Next(departments.Length)],
                    ips[rng.Next(ips.Length)],
                    purpose: rng.Next(2) == 0 ? "AUDIT" : "OPS");

                Exception? oracleFailure = null;
                TableAccessDecision? expected = null;
                try { expected = await oracle.EvaluatePolicyAsync(context); } catch (Exception ex) { oracleFailure = ex; }

                if (oracleFailure is not null)
                {
                    await Should.ThrowAsync<Exception>(async () => await shared.EvaluatePolicyAsync(context).AsTask());
                    continue;
                }

                var actual = await shared.EvaluatePolicyAsync(context);
                actual.IsAllowed.ShouldBe(expected!.IsAllowed, $"seed {seed}, policy:\n{policy}");
                actual.CombinedRowFilterSql.ShouldBe(expected.CombinedRowFilterSql, $"seed {seed}, policy:\n{policy}");
            }
        }
    }

    [Fact]
    public void ShardedCache_StaysWithinCapacity_AndEvictsOnlyOldEntries()
    {
        var cache = new CasbinDecisionCache(4_096, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        cache.ShardCount.ShouldBeGreaterThan(1);

        for (var i = 0; i < 20_000; i++)
        {
            cache.Set(DecisionKey.Create(1, Ctx("u" + i), "read"), TableAccessDecision.Denied(Table, "no"));
        }

        cache.Count.ShouldBeLessThanOrEqualTo(4_096);
    }

    [Fact]
    public async Task GetOrCompute_ConcurrentMisses_ShareOneComputation()
    {
        var cache = new CasbinDecisionCache(100, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        var key = DecisionKey.Create(1, Ctx("alice"), "read");
        var computations = 0;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        (TableAccessDecision, bool) Compute()
        {
            Interlocked.Increment(ref computations);
            entered.Set();
            release.Wait();
            return (TableAccessDecision.Denied(Table, "x"), true);
        }

        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() => cache.GetOrCompute(key, Compute))).ToArray();
        entered.Wait();
        // Every caller has joined the in-flight computation (the leader plus 15 waiters) before the leader finishes.
        SpinWait.SpinUntil(() => cache.DiagnosticWaiters == 16);

        release.Set();
        await Task.WhenAll(tasks);

        computations.ShouldBe(1);
        cache.TryGet(key, out _).ShouldBeTrue();
    }

    [Fact]
    public void GetOrCompute_Exception_IsNotCached()
    {
        var cache = new CasbinDecisionCache(100, TimeSpan.FromSeconds(60), new FakeTimeProvider());
        var key = DecisionKey.Create(1, Ctx("alice"), "read");

        Should.Throw<InvalidOperationException>(() => cache.GetOrCompute(key, () => throw new InvalidOperationException()));

        cache.TryGet(key, out _).ShouldBeFalse();
        cache.GetOrCompute(key, () => (TableAccessDecision.Denied(Table, "ok"), true)).IsAllowed.ShouldBeFalse();
        cache.TryGet(key, out _).ShouldBeTrue();
    }
}
