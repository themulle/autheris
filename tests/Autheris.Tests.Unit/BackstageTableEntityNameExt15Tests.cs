namespace Autheris.Tests.Unit;

using Autheris.Domain.Common;
using Autheris.Extensions.Backstage;
using Shouldly;
using Xunit;

/// <summary>EXT-15: table entity names are unique per exact identifier and stay within the Backstage name limit.</summary>
public sealed class BackstageTableEntityNameExt15Tests
{
    [Fact]
    public void SameSchemaAndTable_InDifferentDomains_GetDifferentNames()
    {
        var a = BackstageCatalogExportService.BuildTableEntityName(new TableIdentifier("tenanta", "public", "orders"));
        var b = BackstageCatalogExportService.BuildTableEntityName(new TableIdentifier("tenantb", "public", "orders"));

        a.ShouldNotBe(b);
    }

    [Fact]
    public void NamesThatOnlyDifferInCaseOrSpecialCharacters_GetDifferentNames()
    {
        var a = BackstageCatalogExportService.BuildTableEntityName(new TableIdentifier("d", "public", "orders"));
        var b = BackstageCatalogExportService.BuildTableEntityName(new TableIdentifier("d", "Public", "Orders"));
        var c = BackstageCatalogExportService.BuildTableEntityName(new TableIdentifier("d", "public", "orders!"));

        new[] { a, b, c }.Distinct().Count().ShouldBe(3);
    }

    [Fact]
    public void LongIdentifiers_StayWithin63Characters()
    {
        var name = BackstageCatalogExportService.BuildTableEntityName(
            new TableIdentifier(new string('d', 40), new string('s', 40), new string('t', 40)));

        name.Length.ShouldBeLessThanOrEqualTo(63);
    }
}
