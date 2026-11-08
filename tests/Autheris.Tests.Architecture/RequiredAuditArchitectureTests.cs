using System.Text.RegularExpressions;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Sql.Services;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// SEC-36-SP2-06: security decision paths write their audit records with <c>RecordRequiredAsync</c>, never fire-and-forget.
/// IL scan over the whole type including compiler-generated state machines, closures and local functions.
/// </summary>
[Trait("Category", "Security")]
public sealed partial class RequiredAuditArchitectureTests
{
    private const string Writer = "Autheris.Application.Audit.IAuditWriter";
    private const string Repository = "Autheris.Application.Common.Interfaces.IAuditLogRepository";

    /// <summary>The read path of <c>GovernedSqlExecutor</c> only. This count may only be lowered.</summary>
    private const int BaselineCount = 3;

    private static readonly Type[] ScannedTypes =
    [
        typeof(DatasourceTestingService),
        typeof(AccessPlanningService),
        typeof(CatalogDiscoveryService),
        typeof(GovernedSqlExecutor)
    ];

    /// <summary>Literal fixed set (type, owning method, callee); no wildcard and no partial match.</summary>
    private static readonly HashSet<(string Type, string Method, string Callee)> Baseline =
    [
        ("GovernedSqlExecutor", "ExecuteRewrittenAsync", "IAuditWriter.RecordAsync"),
        ("GovernedSqlExecutor", "ExecuteRewrittenAsync", "IAuditWriter.RecordBestEffortAsync"),
        ("GovernedSqlExecutor", "ExecuteCoreAsync", "IAuditWriter.RecordAsync")
    ];

    [GeneratedRegex(@"^<(?<name>[^>]+)>")]
    private static partial Regex GeneratedName();

    private sealed record Call(string Type, string Method, string Callee, bool FireAndForget);

    private static IEnumerable<TypeDefinition> WithNested(TypeDefinition type)
    {
        yield return type;
        foreach (var nested in type.NestedTypes.SelectMany(WithNested))
        {
            yield return nested;
        }
    }

    private static string OwningMethod(MethodDefinition method)
    {
        if (GeneratedName().Match(method.Name) is { Success: true } byMethod)
        {
            return byMethod.Groups["name"].Value;
        }

        if (GeneratedName().Match(method.DeclaringType.Name) is { Success: true } byType && byType.Groups["name"].Value.Length > 0)
        {
            return byType.Groups["name"].Value;
        }

        return method.Name;
    }

    private static IEnumerable<Call> AuditCalls(TypeDefinition root)
    {
        foreach (var type in WithNested(root))
        {
            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                var instructions = method.Body.Instructions;
                for (var i = 0; i < instructions.Count; i++)
                {
                    if (instructions[i].Operand is not MethodReference callee)
                    {
                        continue;
                    }

                    var declaring = callee.DeclaringType.FullName;
                    var isTaskRun = declaring == "System.Threading.Tasks.Task" && callee.Name == "Run";
                    if (declaring is not (Writer or Repository) && !isTaskRun)
                    {
                        continue;
                    }

                    // The result is discarded when, after optional ConfigureAwait wrappers, the next instruction is a pop.
                    var next = i + 1;
                    while (next + 1 < instructions.Count
                        && instructions[next].OpCode.Code is Code.Ldc_I4_0 or Code.Ldc_I4_1
                        && instructions[next + 1].Operand is MethodReference { Name: "ConfigureAwait" })
                    {
                        next += 2;
                    }

                    var discarded = next < instructions.Count && instructions[next].OpCode == OpCodes.Pop;
                    var simple = declaring[(declaring.LastIndexOf('.') + 1)..] + "." + callee.Name;
                    yield return new Call(root.Name, OwningMethod(method), simple, discarded);
                }
            }
        }
    }

    private static TypeDefinition Resolve(Type type) =>
        CecilModuleCache.For(type.Assembly).GetType(type.FullName!.Replace('+', '/')) ?? throw new InvalidOperationException(type.FullName);

    private static List<Call> Scan() => ScannedTypes.SelectMany(t => AuditCalls(Resolve(t))).ToList();

    [Fact]
    public void Security_decision_paths_use_RecordRequiredAsync()
    {
        var offenders = Scan()
            .Where(c => c.Callee == "IAuditLogRepository.RecordAuditEventAsync"
                || (c.Callee is "IAuditWriter.RecordAsync" or "IAuditWriter.RecordBestEffortAsync" && !Baseline.Contains((c.Type, c.Method, c.Callee))))
            .Select(c => $"{c.Type}.{c.Method} -> {c.Callee}")
            .Distinct()
            .ToList();

        offenders.ShouldBeEmpty("Use IAuditWriter.RecordRequiredAsync on security decision paths.");
    }

    [Fact]
    public void Allowlist_is_exact_and_not_larger_than_baseline()
    {
        Baseline.Count.ShouldBeLessThanOrEqualTo(BaselineCount);
        var present = Scan().Select(c => (c.Type, c.Method, c.Callee)).ToHashSet();

        Baseline.Where(entry => !present.Contains(entry)).ShouldBeEmpty("Remove allowlist entries that no longer exist and lower BaselineCount.");
    }

    [Fact]
    public void No_scanned_type_discards_an_audit_write()
    {
        Scan().Where(c => c.FireAndForget).Select(c => $"{c.Type}.{c.Method} -> {c.Callee}").ShouldBeEmpty();
    }

    [Fact]
    public void Fire_and_forget_audit_calls_are_rejected()
    {
        // The detector must flag the discard shapes in a known fixture, otherwise the rule above proves nothing.
        var flagged = AuditCalls(Resolve(typeof(FireAndForgetFixture))).Where(c => c.FireAndForget).Select(c => c.Method).Distinct().ToList();

        flagged.ShouldBe([nameof(FireAndForgetFixture.Discarded), nameof(FireAndForgetFixture.DiscardedWithConfigureAwait), nameof(FireAndForgetFixture.DiscardedTaskRun)], ignoreOrder: true);
        AuditCalls(Resolve(typeof(FireAndForgetFixture))).Where(c => c.Method == nameof(FireAndForgetFixture.Awaited)).ShouldAllBe(c => !c.FireAndForget);
    }

    internal sealed class FireAndForgetFixture(IAuditWriter writer, IAuditLogRepository repository)
    {
        public void Discarded() => _ = writer.RecordRequiredAsync(null!);

        public void DiscardedWithConfigureAwait() => _ = repository.RecordAuditEventAsync(null!).ConfigureAwait(false);

        public void DiscardedTaskRun() => _ = Task.Run(() => 1);

        public async Task Awaited() => await writer.RecordRequiredAsync(null!).ConfigureAwait(false);
    }
}
