using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>DT-SC-01: only the tenant resolver may produce <c>TenantResolutionOutcome.DefaultAssigned</c>.</summary>
[Trait("Category", "Security")]
public sealed partial class TenantResolutionArchitectureTests
{
    private static readonly string[] Allowed =
    [
        "src/Autheris.Application/Tenancy/TenantResolver.cs",
        "src/Autheris.Domain/Tenancy/TenantResolution.cs"
    ];

    [System.Text.RegularExpressions.GeneratedRegex(@"new\s+TenantResolution\s*\(\s*TenantResolutionOutcome\.DefaultAssigned|Outcome\s*=\s*TenantResolutionOutcome\.DefaultAssigned")]
    private static partial System.Text.RegularExpressions.Regex ProducesDefaultAssigned();

    [Fact]
    public void DT_SC_01_Only_TenantResolver_Produces_DefaultAssigned()
    {
        var violations = RepositoryScan.ProductionFiles
            // Producing = constructing a resolution with the outcome; reading it (switch, pattern, doc comment) is not producing.
            .Where(f => ProducesDefaultAssigned().IsMatch(f.Text))
            .Select(f => f.RelativePath)
            .Where(p => !Allowed.Contains(p, StringComparer.Ordinal))
            .ToList();

        violations.ShouldBeEmpty("DefaultAssigned is produced by ITenantResolver/TenantResolver only (DT-SC-01).");
    }
}
