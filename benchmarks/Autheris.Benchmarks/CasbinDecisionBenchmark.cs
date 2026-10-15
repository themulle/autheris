using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.InProcess.Emit;

namespace Autheris.Benchmarks;

/// <summary>
/// WP-PF-03 / WP-PF-09 (perf analysis H-1): the Casbin decision path with 5,000 rules, 30 groups and 20% sub_rules.
/// <c>Cold_UniqueUser</c> evaluates a user the cache has never seen (per-user key); <c>Cold_SharedShape</c> evaluates a new user that
/// shares the shape of cached users after the first one (warm shared entry); <c>Warm_SameUser</c> is a plain per-user hit.
/// <c>SubRule_Interpreter</c> is the pre-WP-PF-03 evaluation (interpreter plus regex per call), <c>SubRule_Compiled</c> the delegate.
/// Run with <c>dotnet run -c Release --project benchmarks/Autheris.Benchmarks -- casbin</c>. Not part of CI.
/// </summary>
[MemoryDiagnoser]
public class CasbinDecisionBenchmark : IDisposable
{
    private static readonly TableIdentifier Table = new("hr", "dbo", "employees");
    private static readonly TenantId Tenant = new("t1");
    private const string SubRule = "r.ctx.Department == 'HR' && r.act == 'read'";

    private CasbinEnforcementService _service = null!;
    private SecurityEvaluationContext _template = null!;
    private int _counter;

    public static void Run() => BenchmarkRunner.Run<CasbinDecisionBenchmark>(
        DefaultConfig.Instance.AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance)));

    [GlobalSetup]
    public void Setup()
    {
        _service = new CasbinEnforcementService(cacheOptions: new CasbinDecisionCacheOptions { Capacity = 200_000 });
        var policy = new StringBuilder();
        var rng = new Random(7);
        for (var i = 0; i < 5_000; i++)
        {
            var table = i % 50 == 0 ? "hr.dbo.employees" : $"s{i % 40}.dbo.t{i}";
            var subRule = i % 5 == 0 ? SubRule : "true";
            policy.Append($"p, g{rng.Next(30)}, t1, {table}, read, {subRule}, allow\n");
        }

        _service.LoadPolicyFromText(Tenant, policy.ToString());
        _template = Context("template");
        _ = _service.EvaluatePolicyAsync(_template).AsTask().GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Dispose() => _service?.Dispose();

    private static SecurityEvaluationContext Context(string user) => new(
        new Sid(user),
        Enumerable.Range(0, 30).Where(g => g % 3 == 0).Select(g => new Sid("g" + g)).ToArray(),
        Tenant,
        Table,
        ["id", "name"],
        IPAddress.Loopback,
        DateTimeOffset.UnixEpoch,
        "AUDIT",
        new Dictionary<string, object?> { ["department"] = "HR" });

    [Benchmark]
    public bool Warm_SameUser() => _service.EvaluatePolicyAsync(_template).AsTask().GetAwaiter().GetResult().IsAllowed;

    [Benchmark]
    public bool Cold_SharedShape() => _service.EvaluatePolicyAsync(Context("user" + (++_counter))).AsTask().GetAwaiter().GetResult().IsAllowed;

    [Benchmark]
    public bool SubRule_Compiled() =>
        CompiledSubRuleCache.Evaluate(SubRule, _template, "alice", "hr.dbo.employees", "read") == true;

    [Benchmark(Baseline = true)]
    public bool SubRule_Interpreter()
    {
        var interpreter = new DynamicExpresso.Interpreter();
        interpreter.SetVariable("r", new { ctx = _template, sub = "alice", tenant = "t1", obj = "hr.dbo.employees", act = "read" });
        interpreter.SetVariable("ctx", _template);
        interpreter.SetVariable("context", _template);
        return interpreter.Eval(Regex.Replace(SubRule, @"'([^']{2,})'", "\"$1\"")) is true;
    }
}
