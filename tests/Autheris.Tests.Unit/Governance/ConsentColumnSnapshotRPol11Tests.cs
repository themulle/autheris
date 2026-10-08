namespace Autheris.Tests.Unit.Governance;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>POL-3 / R-POL-11: one snapshot rule for consents frozen at approval (GraphQL and ITSM activation).</summary>
public sealed class ConsentColumnSnapshotRPol11Tests
{
    [Theory]
    [InlineData(false, false, ColumnAccessLevel.Clear)]
    [InlineData(true, false, ColumnAccessLevel.Mask)]
    [InlineData(false, true, ColumnAccessLevel.Mask)]
    [InlineData(true, true, ColumnAccessLevel.Mask)]
    public void AccessLevel_ValueTable(bool sensitive, bool masked, ColumnAccessLevel expected)
    {
        ConsentColumnSnapshot.AccessLevelFor(sensitive, masked).ShouldBe(expected);
    }

    [Fact]
    public void FromMetadata_CoversEveryColumn()
    {
        var meta = new TableMetadata
        {
            Columns =
            [
                new TableColumn { ColumnName = "id" },
                new TableColumn { ColumnName = "ssn", IsSensitive = true },
                new TableColumn { ColumnName = "email" }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "HMAC" } }
        };

        ConsentColumnSnapshot.FromMetadata(meta).Select(r => (r.ColumnName, r.AccessLevel)).ShouldBe(
        [
            ("id", ColumnAccessLevel.Clear),
            ("ssn", ColumnAccessLevel.Mask),
            ("email", ColumnAccessLevel.Mask)
        ]);
    }
}
