using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>Data products (stage 1 and the product object decision): structural rules of the product code.</summary>
[Trait("Category", "Security")]
public sealed partial class ProductArchitectureTests
{
    private static IReadOnlyList<string> ProductFiles() => RepositoryScan.EnumerateFiles("src/Autheris.Application/Products", "*.cs");

    [GeneratedRegex(@"""\s*(CREATE|ALTER|DROP|TRUNCATE|GRANT|REVOKE)\s+(TABLE|VIEW|INDEX|SCHEMA|DATABASE|PROCEDURE|FUNCTION|USER|ROLE|TEMP|TEMPORARY)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DdlText();

    [Fact]
    public void No_ddl_statement_built_in_product_code()
    {
        ProductFiles().ShouldNotBeEmpty();
        var offenders = ProductFiles().Where(f => DdlText().IsMatch(RepositoryScan.ReadText(f))).ToList();

        offenders.ShouldBeEmpty("product code sends no DDL to a data source: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Products_decided_only_by_decision_point()
    {
        // the product code never evaluates grants itself: the decision point decides the product object
        string[] forbidden = ["IConsentRepository", "IConsentResolutionService", "TableAccessPolicy", "IConsentApprovalRepository", "ConsentResolutionService"];
        var offenders = ProductFiles()
            .SelectMany(f => forbidden.Where(t => RepositoryScan.ReadText(f).Contains(t, StringComparison.Ordinal)).Select(t => $"{f}: {t}"))
            .ToList();

        offenders.ShouldBeEmpty();
        RepositoryScan.ReadText("src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs").ShouldContain("ApplyProductColumnClassesAsync");
    }

    [Fact]
    public void Load_reads_definition_by_hash_only()
    {
        var load = RepositoryScan.ReadText("src/Autheris.Application/Products/ProductLoadService.cs");

        load.ShouldContain("ProductDefinitions.Hash(current.Definition)");
        load.ShouldContain("ProductErrorCodes.DefinitionChanged");
        // the runner only ever receives the re-read current revision
        load.ShouldContain("runner.RunAsync(new ProductDefiner(product, limits), ct)");
        Regex.Count(load, @"runner\.RunAsync\(").ShouldBe(1);
    }
}
