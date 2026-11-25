using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Requests;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #1121 (partially reversing #1009 D8): a second approval exists for grants on <c>secret</c> data only, and only through
/// <see cref="SecretFourEyesRule"/>. The legacy four-eyes switches (<c>requireSecondApprover</c>, sensitivity-based <c>RequiresFourEyes</c>,
/// approver counting) stay out of the engine and its store, and the declarative single-decider paths refuse <c>secret</c> targets.
/// Issue #1132: the repository multi-step approval API and the sensitivity-based four-eyes refusals are removed, so the engine with
/// <see cref="SecretFourEyesRule"/> is the one four-eyes code path.
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

    private static readonly string[] RemovedRepositoryApprovalMethods =
    [
        "ApproveConsentRequestStepAsync",
        "ApproveExternalConsentRequestStepAsync",
        "ExecuteAtomicApprovalWithConsentAsync"
    ];

    /// <summary>Production files that once decided four eyes from the data sensitivity, counted approvers or carried a second-approver switch.</summary>
    private static readonly string[] LegacyFourEyesSourceSuffixes =
    [
        "Governance/Grants/GrantPolicy.cs",
        "Governance/Grants/GrantModels.cs",
        "Governance/Services/AccessPlanningService.cs",
        "Governance/Services/DeclarativeConsentApplyService.cs",
        "Governance/ConsentRequests/SeparationOfDutiesService.cs",
        "ConsentRequests/ProfileAssignments/ProfileAssignmentMaintenanceService.cs",
        "Domain/Model/AccessProfileAssignment.cs",
        "Persistence/ConsentApprovalPolicy.cs",
        "Persistence/GovernanceRepository.Consent.cs",
        "Persistence/GovernanceRepository.ConsentWorkflow.cs",
        "Persistence/GovernanceRepository.ConsentSql.cs",
        "Common/Interfaces/IGovernanceRepository.cs"
    ];

    private static readonly string[] LegacyFourEyesTokens =
    [
        "requireSecondApprover",
        "RequiresFourEyes",
        "CountDistinctIndependentApprovers",
        "IsHighSensitivity",
        "StatusAfterApproval",
        "ReadApprovalStateAsync",
        "UNMASKED_REQUIRES_FOUR_EYES",
        "ApproveConsentRequestStepAsync",
        "ApproveExternalConsentRequestStepAsync",
        "ExecuteAtomicApprovalWithConsentAsync"
    ];

    [Fact]
    public void Legacy_four_eyes_paths_do_not_come_back()
    {
        // Issue #1132: no repository approval-step API (internal, external or atomic) next to the engine.
        typeof(IConsentApprovalRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name)
            .ShouldNotContain(n => RemovedRepositoryApprovalMethods.Contains(n));
        typeof(GovernanceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Select(m => m.Name)
            .ShouldNotContain(n => RemovedRepositoryApprovalMethods.Contains(n));
        typeof(GovernanceRepository)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .ShouldNotContain(p => string.Equals(p.Name, "requireSecondApprover", StringComparison.OrdinalIgnoreCase));

        var approvalPolicy = typeof(GovernanceRepository).Assembly.GetType("Autheris.Infrastructure.Persistence.ConsentApprovalPolicy", throwOnError: true)!;
        approvalPolicy.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Select(m => m.Name)
            .ShouldNotContain(n => n == "RequiresFourEyesApproval" || n == "StatusAfterApproval" || n == "IsHighSensitivity");

        // No sensitivity-based four-eyes decision and no approver counting in the grant policy.
        typeof(GrantDecision).GetProperty("RequiresFourEyes").ShouldBeNull();
        typeof(GrantRequest).GetProperty("Approvers").ShouldBeNull();
        typeof(SeparationOfDutiesService).GetMethod("CountDistinctIndependentApprovers").ShouldBeNull();

        // A single approval of a non-secret Unmasked profile assignment is not called (or refused as) four eyes.
        AccessProfileAssignmentRules.ApprovalRequired.ShouldBe("UNMASKED_REQUIRES_APPROVAL");

        var checkedFiles = 0;
        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var norm = file.RelativePath.Replace('\\', '/');
            if (!LegacyFourEyesSourceSuffixes.Any(s => norm.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            checkedFiles++;
            var lines = RepositoryScan.CodeLines(file.Text).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                if (LegacyFourEyesTokens.Any(t => System.Text.RegularExpressions.Regex.IsMatch(lines[i], $@"\b{t}\b")))
                {
                    violations.Add($"{file.RelativePath}:{i + 1}: {lines[i].Trim()}");
                }
            }

            // The single-administrator access plan refuses only secret targets (through the rule), never a sensitivity class.
            if (norm.EndsWith("Governance/Services/AccessPlanningService.cs", StringComparison.OrdinalIgnoreCase))
            {
                file.Text.ShouldNotContain("GrantErrorCodes.FourEyesRequired", customMessage: "the access plan refuses secret targets only, via SecretFourEyesRule");
            }
        }

        checkedFiles.ShouldBe(LegacyFourEyesSourceSuffixes.Length);
        violations.ShouldBeEmpty();
    }
}
