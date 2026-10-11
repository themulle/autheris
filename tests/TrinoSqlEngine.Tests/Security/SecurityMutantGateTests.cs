#if DEBUG
using System.Text;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// WP-X0v, plan M-3 and SEC-ADG-13: the deterministic security mutant suite. It runs on every PR (it needs no container and no
/// network) and is part of the <c>SqlGate</c> category. The seam it uses is compiled out of Release builds, so this suite runs in
/// the Debug configuration (<c>dotnet test -c Debug --filter Category=SqlGate</c>); a Release build proves that the hooks are gone
/// instead (<see cref="ReleaseHookAbsenceTests"/>).
/// <para>
/// To see one mutant fail the gate tests: <c>SQLGATE_MUTANT=drop-cte-rls dotnet test --filter "FullyQualifiedName~Probe_Passes"</c>
/// keeps that mutant active for every probe, so the probes that guard the control go red.
/// </para>
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "SqlGate")]
public class SecurityMutantGateTests(ITestOutputHelper output)
{
    private const string MutantEnvironmentVariable = "SQLGATE_MUTANT";

    public static IEnumerable<object[]> ProbeNames() => SecurityProbes.All.Select(p => new object[] { p.Name });

    public static IEnumerable<object[]> MutantIds() => SecurityMutants.Active.Select(m => new object[] { m.Id });

    // An empty theory fails in xUnit; with no pending mutant a single empty id stands in and the test returns.
    public static IEnumerable<object[]> PendingIds() =>
        SecurityMutants.Pending.Count == 0 ? new[] { new object[] { string.Empty } } : SecurityMutants.Pending.Select(m => new object[] { m.Id });

    /// <summary>A probe passes on the real pipeline. With <c>SQLGATE_MUTANT</c> set, the named mutant stays active and the probes that guard it fail.</summary>
    [Theory]
    [MemberData(nameof(ProbeNames))]
    public void Probe_Passes(string name)
    {
        var probe = SecurityProbes.All.Single(p => p.Name == name);
        string? active = Environment.GetEnvironmentVariable(MutantEnvironmentVariable);
        var mutant = string.IsNullOrEmpty(active) ? null : SecurityMutants.Active.SingleOrDefault(m => m.Id == active)
            ?? throw new InvalidOperationException($"Unknown mutant '{active}' in {MutantEnvironmentVariable}.");
        using var _ = MutantContext.Enter(mutant?.Seam);
        probe.Run();
    }

    /// <summary>The kill check: with the mutant active, at least one probe must fail. A mutant that survives is a gap in the gate.</summary>
    [Theory]
    [MemberData(nameof(MutantIds))]
    public void Mutant_IsKilled(string id)
    {
        var mutant = SecurityMutants.Active.Single(m => m.Id == id);
        var killers = RunProbesUnder(mutant);
        output.WriteLine($"{mutant.Id} ({mutant.PlanRef}): killed by {killers.Count} probe(s)");
        foreach (var killer in killers.Take(12)) output.WriteLine("  " + killer);

        Assert.True(killers.Count > 0,
            $"SURVIVING MUTANT '{mutant.Id}': {mutant.Description}. No gate probe fails when this control is switched off ({mutant.PlanRef}).");
    }

    /// <summary>The unmutated pipeline passes every probe; a probe that is red here would make the kill check meaningless.</summary>
    [Fact]
    public void NoMutant_NoProbeFails()
    {
        Assert.Empty(RunProbesUnder(null));
    }

    [Fact]
    public void MutantIds_AreUnique_AndEveryProbeNameIsUnique()
    {
        Assert.Equal(SecurityMutants.Active.Count + SecurityMutants.Pending.Count,
            SecurityMutants.Active.Select(m => m.Id).Concat(SecurityMutants.Pending.Select(m => m.Id)).Distinct().Count());
        Assert.Equal(SecurityProbes.All.Count, SecurityProbes.All.Select(p => p.Name).Distinct().Count());
    }

    /// <summary>A new control switch cannot be added without a mutant that uses it (and so without a probe that guards it).</summary>
    [Fact]
    public void EverySecurityControlSwitch_HasAnActiveMutant()
    {
        var used = SecurityMutants.Active.SelectMany(m => m.Seam.DisabledControls ?? Enumerable.Empty<SecurityControl>()).ToHashSet();
        var unused = Enum.GetValues<SecurityControl>().Where(c => !used.Contains(c)).ToList();
        Assert.True(unused.Count == 0, "Security control switches without a mutant: " + string.Join(", ", unused));
    }

    /// <summary>The M-3 named set, the section 14 set and the Phase 3 amendment are all present (an id cannot be dropped silently).</summary>
    [Fact]
    public void TheNamedMutantSets_ArePresent()
    {
        string[] required =
        {
            // S14/6
            "drop-cte-rls", "drop-set-operation-branch-rls", "skip-scalar-subquery", "unmask-wildcard-expansion", "allow-tenant-column-in-update",
            "accept-trivially-true-merge-on", "emit-string-literal-unbound", "bypass-in-list-limit",
            // M-3
            "drop-policysubquery-tenant-predicate", "skip-coverage-verifier", "disable-bind-by-name", "drop-binary-tenant-template",
            "allow-policy-column-assignment-in-update", "accept-when-not-matched-by-source", "omit-policy-values-from-cache-rebind",
            "cte-case-insensitive-scope", "accept-dollar-brace-in-databricks-identifier", "skip-execution-stack-guard"
        };
        var known = SecurityMutants.Active.Select(m => m.Id).ToHashSet();
        foreach (string id in required) Assert.Contains(id, known);
    }

    /// <summary>
    /// Activation check for the controls of other work packages: while the control is absent from <c>src</c> the mutant is pending;
    /// once it is present this test fails until the mutant is implemented, so a merged control can never go unguarded.
    /// </summary>
    [Theory]
    [MemberData(nameof(PendingIds))]
    public void PendingMutant_IsActivated_WhenItsControlExists(string id)
    {
        if (id.Length == 0) return;   // no pending mutant
        var pending = SecurityMutants.Pending.Single(m => m.Id == id);
        var regex = new Regex(pending.ActivationRegex, RegexOptions.CultureInvariant);
        bool present = RepositoryFiles.Search(pending.SourcePattern).Any(f => regex.IsMatch(File.ReadAllText(f)));
        bool applicable = pending.ActivatesWhenAbsent ? !present : present;
        output.WriteLine($"{pending.Id}: target {pending.TargetWorkPackage}; control on this branch: {applicable}");
        Assert.False(applicable,
            $"The control of pending mutant '{pending.Id}' ({pending.Description}; {pending.TargetWorkPackage}) is now on this branch. " +
            "Implement the mutant (a hook plus a probe that fails without the control) and move it from SecurityMutants.Pending to Active.");
    }

    /// <summary>Writes the kill report when <c>SQLGATE_MUTANT_REPORT</c> names a file (CI artifact).</summary>
    [Fact]
    public void MutantReport_IsWritten_WhenRequested()
    {
        string? path = Environment.GetEnvironmentVariable("SQLGATE_MUTANT_REPORT");
        if (string.IsNullOrEmpty(path)) return;
        var report = new StringBuilder("# Security mutant report\n\n| Mutant | Plan | Killed by |\n|---|---|---|\n");
        foreach (var mutant in SecurityMutants.Active)
        {
            var killers = RunProbesUnder(mutant);
            report.Append($"| {mutant.Id} | {mutant.PlanRef} | {(killers.Count == 0 ? "**SURVIVED**" : killers.Count + " probe(s): " + killers[0])} |\n");
        }

        foreach (var pending in SecurityMutants.Pending) report.Append($"| {pending.Id} | {pending.PlanRef} | pending ({pending.TargetWorkPackage}) |\n");
        File.WriteAllText(path, report.ToString());
    }

    private static List<string> RunProbesUnder(SecurityMutant? mutant)
    {
        var killers = new List<string>();
        foreach (var probe in SecurityProbes.All)
        {
            try
            {
                using var _ = MutantContext.Enter(mutant?.Seam);
                probe.Run();
            }
            catch (Exception ex)
            {
                killers.Add($"{probe.Name}: {ex.GetType().Name}");
            }
        }

        return killers;
    }
}
#endif
