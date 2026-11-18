namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Test helper: marks catalog columns as classified (global PII), the only way a column is sensitive since the legacy flag was dropped.</summary>
public static class TableMetadataClassificationTestExtensions
{
    /// <summary>Global PII classifications for the named columns (the column id is the column name in these fixtures).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<ColumnClassification>> PiiFor(params string[] columnNames) =>
        columnNames.ToDictionary(
            static n => n,
            static n => (IReadOnlyList<ColumnClassification>)[new ColumnClassification { TableColumnId = n, Code = ClassificationScopes.PiiCode, Source = "TEST" }],
            StringComparer.OrdinalIgnoreCase);

    public static TableMetadata WithPii(this TableMetadata metadata, params string[] columnNames)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var wanted = new HashSet<string>(columnNames, StringComparer.OrdinalIgnoreCase);
        var flagged = metadata.Columns.Where(c => wanted.Contains(c.ColumnName)).ToList();
        if (flagged.Count != wanted.Count)
        {
            throw new ArgumentException("A column to classify is not part of the table metadata.", nameof(columnNames));
        }

        return metadata with
        {
            ColumnClassifications = ClassificationMetadata.Merge(metadata.ColumnClassifications, ClassificationMetadata.AssignPii(flagged, "TEST"))
        };
    }
}
