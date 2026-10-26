using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>DT-SC-01: only the tenant resolver may produce <c>TenantResolutionOutcome.DefaultAssigned</c>.</summary>
[Trait("Category", "Security")]
public sealed class TenantResolutionArchitectureTests
{
    private static readonly string[] Allowed =
    [
        "src/Autheris.Application/Tenancy/TenantResolver.cs",
        "src/Autheris.Domain/Tenancy/TenantResolution.cs"
    ];

    [Fact]
    public void DT_SC_01_Only_TenantResolver_Produces_DefaultAssigned()
    {
        var violations = RepositoryScan.ProductionFiles
            .Where(f => f.Text.Contains("TenantResolutionOutcome.DefaultAssigned", StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .Where(p => !Allowed.Contains(p, StringComparer.Ordinal))
            .ToList();

        violations.ShouldBeEmpty("DefaultAssigned is produced by ITenantResolver/TenantResolver only (DT-SC-01).");
    }
}
