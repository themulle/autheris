namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Extensions.DependencyInjection.StartupChecks;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Common;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.Configuration.StartupValidatorGoldenSupport;

/// <summary>
/// ARCH-MNT-14 tests of the ordered startup checks: the order is a golden (SC-04), every check is guarded by a golden case
/// (SC-02, proven by removing one check at a time) and the first violation wins without aggregation (SC-03).
/// </summary>
[Collection(GoldenConsoleCollection.Name)]
public sealed class GatewayStartupChecksTests : IDisposable
{
    /// <summary>
    /// Checks that no configuration can reach: the data annotation range of <c>QueryCostOptions</c> (checked by ObjectGraph first)
    /// is stricter than the production check, so the check is a defence in depth with no observable case. A newly unguarded check
    /// fails the mutation test; if this one becomes reachable it must get a case and leave this list.
    /// </summary>
    private static readonly string[] UnreachableBehindObjectGraph =
    [
        "QueryCostOptions", // production range check is shadowed by the [Range]/[Required] annotations ObjectGraph validates first
    ];

    private readonly string _fixtureDir = CreateFixtureDirectory();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_fixtureDir, recursive: true);
        }
        catch (IOException)
        {
            // best effort cleanup of the temp fixture
        }
    }

    private static ValidateAction With(IReadOnlyList<StartupCheck> checks) =>
        (options, environment, getEnv, logger) => GatewayStartupValidator.ValidateStartup(options, environment, getEnv, logger, checks);

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "ARCH-MNT-14-SC-04")]
    public void StartupChecks_OrderIsStable()
    {
        // ARCH-MNT-14-T-02: names and count are compared with the golden; an added, removed or moved check changes it.
        var golden = File.ReadAllLines(SnapshotFile.SnapshotPath("startup-check-order.txt")).Where(l => l.Length > 0).ToArray();

        GatewayStartupChecks.All.Select(c => c.Name).ToArray().ShouldBe(golden);
        GatewayStartupChecks.All.Select(c => c.Name).Distinct().Count().ShouldBe(GatewayStartupChecks.All.Count);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "ARCH-MNT-14-SC-02")]
    public void EachCheck_IsGuardedByAGoldenCase()
    {
        // ARCH-MNT-14-T-03: dropping one registry entry must change at least one golden case, so no check was dropped.
        var failureCases = GatewayStartupValidatorGoldenTests.FailureCases();
        var warningCases = GatewayStartupValidatorGoldenTests.WarningCases();
        var all = GatewayStartupChecks.All;

        string[] Outcomes(IReadOnlyList<StartupCheck> checks) =>
        [
            .. failureCases.Select(c => RunFailureOutcome(c, With(checks), _fixtureDir)),
            .. warningCases.Select(c => RunWarningOutcome(c, With(checks), _fixtureDir)),
        ];

        var expected = Outcomes(all);
        var unguarded = new List<string>();
        for (var i = 0; i < all.Count; i++)
        {
            var without = all.Where((_, index) => index != i).ToArray();
            if (Outcomes(without).SequenceEqual(expected))
            {
                unguarded.Add(all[i].Name);
            }
        }

        unguarded.ShouldBe(UnreachableBehindObjectGraph);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "ARCH-MNT-14-SC-03")]
    public void Validator_FirstViolationWins_NoAggregation()
    {
        // ARCH-MNT-14-T-06: two violations (egress and a missing Casbin model); only the first check reports, the second never runs.
        var twoViolations = Prod(
            "TwoViolations",
            ("Egress:TrustedInternalNetworks:0", "10.0.0.0/4"),
            ("Casbin:ModelPath", "{tmp}/missing.conf"));
        var outcome = RunFailureOutcome(twoViolations, With(GatewayStartupChecks.All), _fixtureDir);

        outcome.ShouldContain("Egress allowlist configuration error");
        outcome.ShouldNotContain("Casbin model file");
        outcome.ShouldNotContain("Aggregate");

        var ran = new List<string>();
        var first = new StartupCheck("First", _ => ran.Add("First"));
        var failing = new StartupCheck("Failing", _ =>
        {
            ran.Add("Failing");
            throw new ValidationException("failing check");
        });
        var never = new StartupCheck("Never", _ => ran.Add("Never"));

        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateStartup(new GatewayOptions(), HostEnvironment("Production"), _ => null, null, [first, failing, never]));

        ex.Message.ShouldBe("failing check");
        ran.ShouldBe(["First", "Failing"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "ARCH-MNT-14-SC-03")]
    public void Context_ComputesBypassesAndWarningsOnce()
    {
        var options = Bind(new Dictionary<string, string?> { ["Insecure:danger_disable_column_masking"] = "true", ["Insecure:warn_allow_all_cors_origins"] = "true" }, _fixtureDir);
        var context = new StartupCheckContext(options, HostEnvironment("Development"), _ => null, null);

        context.DangerBypasses.ShouldBeSameAs(context.DangerBypasses);
        context.Warnings.ShouldBeSameAs(context.Warnings);
        context.DangerBypasses.ShouldBe(options.GetActiveDangerBypasses());
        context.Warnings.ShouldBe(options.GetActiveWarnings());
    }
}
