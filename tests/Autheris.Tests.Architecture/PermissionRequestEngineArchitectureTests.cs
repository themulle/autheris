namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

/// <summary>Structural rules for the permission request engine: it is the only writer of decisions and requests.</summary>
public sealed class PermissionRequestEngineArchitectureTests
{
    private static readonly Regex DecisionCasCall = new(@"\.TryDecideAsync\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    private static readonly Regex RequestCreateCall = new(@"\.CreateConsentRequestAsync\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // The engine files, plus the persistence layer that implements the store.
    private static readonly string[] DecisionWriters =
    [
        "src/Autheris.Application/Governance/Requests/PermissionRequestEngine",
        "src/Autheris.Infrastructure/Persistence/"
    ];

    // System-initiated queueing of owner approval for newly matched objects of pattern grants; no caller exists to submit as.
    private static readonly string[] RequestCreators =
    [
        "src/Autheris.Application/Governance/Requests/PermissionRequestEngine",
        "src/Autheris.Application/Policy/RowScopes/PatternCoverageMonitor.cs",
        "src/Autheris.Application/ConsentRequests/ProfileAssignments/ProfileAssignmentMaintenanceService.cs",
        "src/Autheris.Infrastructure/Persistence/"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Only_engine_writes_requests_and_grants()
    {
        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var path = file.RelativePath.Replace('\\', '/');
            var code = string.Join('\n', RepositoryScan.CodeLines(file.Text));
            if (DecisionCasCall.IsMatch(code) && !DecisionWriters.Any(w => path.Contains(w, StringComparison.Ordinal)))
            {
                violations.Add($"{path}: decides a request outside the engine");
            }

            if (RequestCreateCall.IsMatch(code) && !RequestCreators.Any(w => path.Contains(w, StringComparison.Ordinal)))
            {
                violations.Add($"{path}: creates a request outside the engine");
            }
        }

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }
}
