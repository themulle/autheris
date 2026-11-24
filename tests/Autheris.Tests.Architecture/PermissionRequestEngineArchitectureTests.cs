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

    private static readonly Regex VerifiedRevocationCall = new(@"\bForVerifiedRevocation\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));

    // The factory mints tenant-admin revocation authority; only the grant revoke authority may call it after its checks.
    private static readonly string[] VerifiedRevocationMinters =
    [
        "src/Autheris.Application/Governance/Requests/GrantRevokeAuthority.cs",
        "src/Autheris.Domain/Security/ConsentRevocationAuthority.cs"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Only_grant_revoke_authority_mints_verified_revocation_authority()
    {
        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var path = file.RelativePath.Replace('\\', '/');
            var code = string.Join('\n', RepositoryScan.CodeLines(file.Text));
            if (VerifiedRevocationCall.IsMatch(code) && !VerifiedRevocationMinters.Any(w => path.EndsWith(w, StringComparison.Ordinal)))
            {
                violations.Add($"{path}: mints ConsentRevocationAuthority.ForVerifiedRevocation outside GrantRevokeAuthority");
            }
        }

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Verified_revocation_ratchet_detects_a_new_caller()
    {
        VerifiedRevocationCall.IsMatch("var a = ConsentRevocationAuthority.ForVerifiedRevocation(tenant, \"x\");").ShouldBeTrue();
        VerifiedRevocationCall.IsMatch("var a = ConsentRevocationAuthority.FromPrincipal(user);").ShouldBeFalse();
    }
}
