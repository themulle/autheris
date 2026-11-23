using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Governance.Requests;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed class FourEyesRemovalArchitectureTests
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

    [Fact]
    public void No_second_approver_path()
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

                if (norm.EndsWith("Decide.cs", StringComparison.OrdinalIgnoreCase)
                    && line.Contains("PENDING_SECOND_APPROVAL", StringComparison.Ordinal))
                {
                    violations.Add($"{file.RelativePath}:{i + 1}: {line.Trim()}");
                }
            }
        }

        checkedFiles.ShouldBe(ScannedSourceSuffixes.Length);
        violations.ShouldBeEmpty();

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
