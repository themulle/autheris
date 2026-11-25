using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Governance.Requests;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #1121 (partially reversing #1009 D8): a second approval exists for grants on <c>secret</c> data only, and only through
/// <see cref="SecretFourEyesRule"/>. The legacy four-eyes switches (<c>requireSecondApprover</c>, sensitivity-based <c>RequiresFourEyes</c>,
/// approver counting) stay out of the engine and its store, and the declarative single-decider paths refuse <c>secret</c> targets.
/// </summary>
[Trait("Category", "Security")]
public sealed class SecretFourEyesArchitectureTests
{
    private static readonly string[] ScannedSourceSuffixes =
    [
        "Governance/Requests/PermissionRequestEngine.cs",
        "Governance/Requests/PermissionRequestEngine.Submit.cs",
        "Governance/Requests/PermissionRequestEngine.Decide.cs",
        "Governance/Requests/PermissionRequestModels.cs",
        "Governance/Requests/DataGrantApplier.cs",
        "Governance/Requests/UnmaskGrantApplier.cs",
        "Governance/Requests/CatalogGrantApplier.cs",
        "Governance/Requests/ProfileAssignmentApplier.cs",
        "Persistence/PermissionRequestStore.cs"
    ];

    private static readonly string[] SingleDeciderPathSuffixes =
    [
        "Governance/Services/DeclarativeConsentApplyService.cs",
        "Governance/Services/AccessPlanningService.cs"
    ];

    [Fact]
    public void Second_approval_only_through_the_secret_rule()
    {
        var checkedFiles = 0;
        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var norm = file.RelativePath.Replace('\\', '/');
            if (!ScannedSourceSuffixes.Any(s => norm.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            checkedFiles++;
            var lines = RepositoryScan.CodeLines(file.Text).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                if (line.Contains("requireSecondApprover", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("CountDistinctIndependentApprovers", StringComparison.OrdinalIgnoreCase)
                    || line.Contains("RequiresFourEyes", StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{file.RelativePath}:{i + 1}: {line.Trim()}");
                }

                // The engine produces the second-approval status only through the rule's constant.
                if (norm.Contains("/Governance/Requests/", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("PENDING_SECOND_APPROVAL", StringComparison.Ordinal))
                {
                    violations.Add($"{file.RelativePath}:{i + 1}: {line.Trim()}");
                }
            }

            if (norm.EndsWith("PermissionRequestEngine.Decide.cs", StringComparison.OrdinalIgnoreCase))
            {
                file.Text.ShouldContain("SecretFourEyesRule.Applies(", customMessage: "the decision path evaluates the secret four-eyes rule");
                file.Text.ShouldContain("SecretFourEyesRule.IsSameApprover(", customMessage: "the decision path refuses a second approval by the same person");
            }
        }

        checkedFiles.ShouldBe(ScannedSourceSuffixes.Length);
        violations.ShouldBeEmpty();

        var singleDecider = RepositoryScan.ProductionFiles
            .Where(f => SingleDeciderPathSuffixes.Any(s => f.RelativePath.Replace('\\', '/').EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        singleDecider.Count.ShouldBe(SingleDeciderPathSuffixes.Length);
        singleDecider.ShouldAllBe(f => f.Text.Contains("SecretFourEyesRule.DeclarativeRefusal(", StringComparison.Ordinal));

        SecretFourEyesRule.PendingSecondApproval.ShouldBe("PENDING_SECOND_APPROVAL");
        SecretFourEyesRule.RequiredApprovals.ShouldBe(2);

        typeof(PermissionRequestEngine)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(m => m.GetParameters())
            .ShouldNotContain(p => string.Equals(p.Name, "requireSecondApprover", StringComparison.OrdinalIgnoreCase));

        typeof(PermissionDecisionCommand)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ShouldNotContain(p => string.Equals(p.Name, "SelfGrant", StringComparison.OrdinalIgnoreCase));

        typeof(PermissionRequestStore)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SelectMany(m => m.GetParameters())
            .ShouldNotContain(p => string.Equals(p.Name, "requireSecondApprover", StringComparison.OrdinalIgnoreCase));
    }
}
