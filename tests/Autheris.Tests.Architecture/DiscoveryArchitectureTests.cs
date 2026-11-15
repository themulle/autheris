using System;
using System.Linq;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class DiscoveryArchitectureTests
{
    private static readonly string[] ForbiddenConsentStoreTokens =
    [
        "ConsentResolutionService",
        "IConsentResolutionService",
        "GranteeMatcher",
        "GetAllActiveConsentsForSubjectsAsync",
        "GetActiveConsentsForSubjectsAsync",
        "GetActiveRoleConsentsAsync"
    ];

    private static readonly string[] ForbiddenExecutionTokens =
    [
        "IGatewayExecutionService",
        "ISqlExecutionService",
        "ISqlTableReadExecutor",
        "GovernedSqlExecutor",
        "IDataSourceClient",
        "IDataSourceConnectionFactory"
    ];

    [Fact]
    [Trait("Category", "Security")]
    public void Discovery_DoesNotReference_ConsentResolution_GranteeMatcher_Or_GrantStoreForAllow()
    {
        var files = RepositoryScan.ProductionFiles
            .Where(static f => f.RelativePath.StartsWith("src/Autheris.Application/Catalog/Discovery/", StringComparison.Ordinal))
            .ToList();
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            foreach (var token in ForbiddenConsentStoreTokens)
            {
                file.Text.Contains(token, StringComparison.Ordinal).ShouldBeFalse(
                    $"{file.RelativePath} must not reference '{token}'; discovery allow decisions come solely from IAccessDecisionPoint.");
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void DiscoveryNamespace_DoesNotReference_DataSourceExecution()
    {
        var files = RepositoryScan.ProductionFiles
            .Where(static f => f.RelativePath.StartsWith("src/Autheris.Application/Catalog/Discovery/", StringComparison.Ordinal))
            .ToList();
        files.ShouldNotBeEmpty();

        foreach (var file in files)
        {
            foreach (var token in ForbiddenExecutionTokens)
            {
                file.Text.Contains(token, StringComparison.Ordinal).ShouldBeFalse(
                    $"{file.RelativePath} must not reference '{token}'; discovery must never touch data-source connections or execution.");
            }
        }
    }
}
