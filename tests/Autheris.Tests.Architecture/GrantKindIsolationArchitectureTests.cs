using System;
using System.IO;
using System.Linq;
using Autheris.Application.Governance.Requests;
using Autheris.Application.Services;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed class GrantKindIsolationArchitectureTests
{
    [Fact]
    public void Data_resolution_ignores_unmask_and_catalog_grants()
    {
        var resolutionFile = RepositoryScan.ProductionFiles
            .Single(f => f.RelativePath.Replace('\\', '/').EndsWith("Application/Services/ConsentResolutionService.cs", StringComparison.OrdinalIgnoreCase));

        resolutionFile.Text.ShouldContain("PermissionGrantKind.Data");

        var catalogApplier = RepositoryScan.ProductionFiles
            .Single(f => f.RelativePath.Replace('\\', '/').EndsWith("Application/Governance/Requests/CatalogGrantApplier.cs", StringComparison.OrdinalIgnoreCase));
        catalogApplier.Text.ShouldContain("ICatalogVisibilityGrantRepository");
        catalogApplier.Text.ShouldNotContain("CONSENTS");
        catalogApplier.Text.ShouldNotContain("IGrantWriter");
        catalogApplier.Text.ShouldNotContain("IConsentRepository");
    }

    [Fact]
    public void All_consent_reads_carry_grant_kind()
    {
        string[] consentReadFiles =
        [
            "Infrastructure/Persistence/GovernanceRepository.Consent.cs",
            "Infrastructure/Persistence/GovernanceRepository.PatternGrants.cs",
            "Infrastructure/Persistence/GovernanceRepository.ConsentDiscovery.cs",
            "Infrastructure/Persistence/GovernanceRepository.TenantTables.cs",
            "Infrastructure/Persistence/GovernanceRepository.ConsentPage.cs"
        ];

        foreach (var suffix in consentReadFiles)
        {
            var file = RepositoryScan.ProductionFiles
                .Single(f => f.RelativePath.Replace('\\', '/').EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            var hasPredicate = file.Text.Contains("grant_kind", StringComparison.OrdinalIgnoreCase)
                || file.Text.Contains("DataGrantPredicate", StringComparison.Ordinal);
            hasPredicate.ShouldBeTrue($"{suffix} must filter data consent queries by grant_kind.");
        }
    }
}
