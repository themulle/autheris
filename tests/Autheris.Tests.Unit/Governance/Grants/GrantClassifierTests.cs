using Autheris.Application.Governance.Grants;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants;

/// <summary>WP-SC-16: sensitivity class from catalog metadata; any gap classifies as Sensitive (shorter maximum).</summary>
public sealed class GrantClassifierTests
{
    private static TableMetadata Table(string sensitivity = "NORMAL", bool colSensitive = false, string[]? tags = null, bool fourEyes = false) => new()
    {
        Table = new Table { Sensitivity = sensitivity, Tags = tags ?? [], RequiresFourEyes = fourEyes },
        Columns = [new TableColumn { ColumnName = "id", IsSensitive = colSensitive }]
    };

    [Fact]
    public void SEC_SC_20_PlainTable_IsRegular() =>
        GrantClassifier.Classify([Table()]).ShouldBe(GrantSensitivity.Regular);

    [Theory]
    [InlineData("CONFIDENTIAL")]
    [InlineData("HIGH")]
    [InlineData("SOMETHING_UNKNOWN")]
    public void SEC_SC_20_HighSensitivityTable_IsSensitive(string sensitivity) =>
        GrantClassifier.Classify([Table(sensitivity)]).ShouldBe(GrantSensitivity.Sensitive);

    [Fact]
    public void SEC_SC_20_SensitiveColumn_PiiTag_FourEyesTable_AreSensitive()
    {
        GrantClassifier.Classify([Table(colSensitive: true)]).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify([Table(tags: ["PII"])]).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify([Table(fourEyes: true)]).ShouldBe(GrantSensitivity.Sensitive);
    }

    [Fact]
    public void SEC_SC_20_OneSensitiveTable_AmongRegularOnes_ClassifiesTheGrantSensitive() =>
        GrantClassifier.Classify([Table(), Table("RESTRICTED"), Table()]).ShouldBe(GrantSensitivity.Sensitive);

    [Fact]
    public void SEC_SC_20_ClassificationGaps_FailClosedToSensitive()
    {
        GrantClassifier.Classify(null).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify([]).ShouldBe(GrantSensitivity.Sensitive);
        GrantClassifier.Classify([Table(), null]).ShouldBe(GrantSensitivity.Sensitive);
    }
}
