namespace Autheris.Application.Catalog.Search;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Builds scrubbed searchable document text and inferred foreign-key relationships for a catalog table.
/// </summary>
internal static class CatalogSearchDocumentBuilder
{
    public static string BuildScrubbedDocumentText(TableMetadata t)
    {
        var docBuilder = new StringBuilder(1024);

        docBuilder.Append($"{t.Identifier.Domain} {t.Identifier.Schema} {t.Identifier.TableName} ");

        if (!string.IsNullOrWhiteSpace(t.Table.DisplayName))
            docBuilder.Append($"{SecretScrubber.Redact(t.Table.DisplayName)} ");
        if (!string.IsNullOrWhiteSpace(t.Table.Description))
            docBuilder.Append($"{SecretScrubber.Redact(t.Table.Description)} ");
        if (!string.IsNullOrWhiteSpace(t.Table.LongDescription))
            docBuilder.Append($"{SecretScrubber.Redact(t.Table.LongDescription)} ");

        foreach (var (_, entry) in t.Table.Texts.Entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.DisplayName))
                docBuilder.Append($"{SecretScrubber.Redact(entry.DisplayName)} ");
            if (!string.IsNullOrWhiteSpace(entry.Description))
                docBuilder.Append($"{SecretScrubber.Redact(entry.Description)} ");
            if (!string.IsNullOrWhiteSpace(entry.LongDescription))
                docBuilder.Append($"{SecretScrubber.Redact(entry.LongDescription)} ");
        }

        foreach (var c in t.Columns)
        {
            if (c.DiscoveryHidden)
            {
                continue;
            }

            docBuilder.Append($"{c.ColumnName} {c.DataType} ");
            if (!string.IsNullOrWhiteSpace(c.DisplayName))
                docBuilder.Append($"{SecretScrubber.Redact(c.DisplayName)} ");
            if (!string.IsNullOrWhiteSpace(c.Description))
                docBuilder.Append($"{SecretScrubber.Redact(c.Description)} ");

            foreach (var (_, colEntry) in c.Texts.Entries)
            {
                if (!string.IsNullOrWhiteSpace(colEntry.DisplayName))
                    docBuilder.Append($"{SecretScrubber.Redact(colEntry.DisplayName)} ");
                if (!string.IsNullOrWhiteSpace(colEntry.Description))
                    docBuilder.Append($"{SecretScrubber.Redact(colEntry.Description)} ");
            }
        }

        return docBuilder.ToString();
    }

    public static List<TableRelationship>? BuildForeignKeys(TableMetadata t)
    {
        var declaredKey = t.PrimaryKeyColumns;
        List<TableRelationship>? list = null;

        foreach (var col in t.Columns)
        {
            if (col.DiscoveryHidden || declaredKey.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase))
                continue;

            string? targetTable = null;
            string? targetCol = null;

            if (col.Meta != null && col.Meta.TryGetValue("foreign_key_table", out var fkt))
            {
                targetTable = fkt;
                col.Meta.TryGetValue("foreign_key_column", out targetCol);
            }
            else if (col.ColumnName.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(col.ColumnName, "id", StringComparison.OrdinalIgnoreCase) &&
                     !string.Equals(col.ColumnName, "tenant_id", StringComparison.OrdinalIgnoreCase))
            {
                var entityName = col.ColumnName[..^3];
                targetTable = entityName.EndsWith('s') ? entityName : entityName + "s";
                targetCol = col.ColumnName;
            }

            if (string.IsNullOrWhiteSpace(targetTable) ||
                string.Equals(targetTable, t.Identifier.TableName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var refTable = new TableIdentifier(t.Identifier.Domain, t.Identifier.Schema, targetTable);
            list ??= [];
            list.Add(new TableRelationship(
                FromTable: t.Identifier,
                FromColumn: col.ColumnName,
                ToTable: refTable,
                ToColumn: targetCol ?? col.ColumnName,
                Type: TableRelationshipType.ForeignKey));
        }

        return list;
    }
}
