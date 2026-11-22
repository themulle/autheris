using Autheris.Application.Governance.Grants;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

[Trait("Category", "Security")]
public sealed class GrantClassifierTests
{
    private static EffectiveProtection Protection(
        ProtectionClass tableClass,
        string? tableMaskCategory = null,
        params (string Name, ProtectionClass Class, string? MaskCategory)[] columns)
    {
        var colDict = columns.ToDictionary(
            c => c.Name,
            c => new ColumnProtection(c.Class, c.MaskCategory),
            StringComparer.OrdinalIgnoreCase);
        return new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            tableClass,
            tableClass,
            tableMaskCategory,
            colDict.AsReadOnly(),
            CatalogVisibilityMode.OnRequest);
    }

    private static TableMetadata Table(bool colSensitive = false) => new()
    {
        ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(colSensitive ? new[] { "id" } : Array.Empty<string>()),
        Table = new Table(),
        Columns = [new TableColumn { ColumnName = "id" }]
    };

    [Fact]
    public void SEC_SC_20_PlainTable_IsRegular()
    {
        GrantClassifier.Classify([Table()]).ShouldBe(GrantSensitivity.Regular);
        GrantClassifier.Classify(Protection(ProtectionClass.Public)).ShouldBe(GrantSensitivity.Regular);
        GrantClassifier.Classify(Protection(ProtectionClass.Internal)).ShouldBe(GrantSensitivity.Regular);
    }

    [Theory]
    [InlineData(ProtectionClass.Confidential)]
    [InlineData(ProtectionClass.Secret)]
    public void SEC_SC_20_ConfidentialOrSecretProtection_IsSensitive(ProtectionClass cls) =>
        GrantClassifier.Classify(Protection(cls)).ShouldBe(GrantSensitivity.Sensitive);

    [Fact]
    public void SEC_SC_20_SensitiveColumn_OrMaskCategory_IsSensitive()
    {
        GrantClassifier.Classify([Table(colSensitive: true)]).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify(Protection(ProtectionClass.Internal, tableMaskCategory: "PII")).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify(Protection(ProtectionClass.Internal, null, ("email", ProtectionClass.Internal, "PII"))).ShouldBe(GrantSensitivity.Sensitive);
    }

    [Fact]
    public void SEC_SC_20_OneSensitiveTable_AmongRegularOnes_ClassifiesTheGrantSensitive() =>
        GrantClassifier.Classify([Protection(ProtectionClass.Public), Protection(ProtectionClass.Confidential), Protection(ProtectionClass.Internal)])
            .ShouldBe(GrantSensitivity.Sensitive);

    [Fact]
    public void SEC_SC_20_ClassificationGaps_FailClosedToSensitive()
    {
        GrantClassifier.Classify((IEnumerable<TableMetadata?>?)null).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify(Array.Empty<TableMetadata?>()).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify([Table(), null]).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify((EffectiveProtection?)null).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify((IEnumerable<EffectiveProtection?>?)null).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify([Protection(ProtectionClass.Public), null]).ShouldBe(GrantSensitivity.Sensitive);
    }
}
