namespace Autheris.Domain.Model;

using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Interfaces;

/// <summary>
/// POL-3 / R-POL-11: the column snapshot frozen into a consent when a request is approved (GraphQL approve, ITSM
/// activation in both governance repositories). A column that is sensitive or has a masking rule is masked, every other
/// column is clear; columns added later are not part of the consent.
/// </summary>
public static class ConsentColumnSnapshot
{
    public static ColumnAccessLevel AccessLevelFor(bool isSensitive, bool hasMaskingRule) =>
        isSensitive || hasMaskingRule ? ColumnAccessLevel.Mask : ColumnAccessLevel.Clear;

    public static IReadOnlyList<ConsentColumnRule> FromMetadata(TableMetadata metadata) =>
        metadata.Columns
            .Select(col => new ConsentColumnRule
            {
                ColumnName = col.ColumnName,
                AccessLevel = AccessLevelFor(col.IsSensitive, metadata.ColumnMaskingRules.ContainsKey(col.ColumnName))
            })
            .ToList();
}
