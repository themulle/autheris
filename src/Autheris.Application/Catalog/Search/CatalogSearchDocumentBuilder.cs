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
    public static string BuildScrubbedDocumentText(TableMetadata t) => BuildDocuments(t).CombinedText;

    public static (Bm25Document Document, string CombinedText) BuildDocuments(TableMetadata t)
    {
        ArgumentNullException.ThrowIfNull(t);

        var fields = new StringBuilder[CatalogSearchFieldWeights.FieldCount];
        for (int i = 0; i < fields.Length; i++)
        {
            fields[i] = new StringBuilder(64);
        }

        var combined = new StringBuilder(1024);

        void Add(Bm25Field field, string? text, bool scrub = true)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var value = scrub ? SecretScrubber.Redact(text) : text;
            fields[(int)field].Append(value).Append(' ');
            combined.Append(value).Append(' ');
        }

        // Technical names
        Add(Bm25Field.TechnicalName, $"{t.Identifier.Domain} {t.Identifier.Schema} {t.Identifier.TableName}", scrub: false);

        // Default language texts (scrubbed)
        Add(Bm25Field.DisplayName, t.Table.DisplayName);
        Add(Bm25Field.ShortDescription, t.Table.Description);
        Add(Bm25Field.LongDescription, t.Table.LongDescription);

        // All table translation texts (scrubbed), each in the field of its source text
        foreach (var (_, entry) in t.Table.Texts.Entries)
        {
            Add(Bm25Field.DisplayName, entry.DisplayName);
            Add(Bm25Field.ShortDescription, entry.Description);
            Add(Bm25Field.LongDescription, entry.LongDescription);
        }

        // Columns: technical name, types and display names are names; descriptions are descriptions (scrubbed)
        foreach (var c in t.Columns)
        {
            if (c.DiscoveryHidden)
            {
                continue;
            }

            Add(Bm25Field.ColumnNames, $"{c.ColumnName} {c.DataType}", scrub: false);
            Add(Bm25Field.ColumnNames, c.DisplayName);
            Add(Bm25Field.ColumnDescriptions, c.Description);

            foreach (var (_, colEntry) in c.Texts.Entries)
            {
                Add(Bm25Field.ColumnNames, colEntry.DisplayName);
                Add(Bm25Field.ColumnDescriptions, colEntry.Description);
            }
        }

        var doc = new Bm25Document(
            t.Identifier,
            TechnicalName: fields[(int)Bm25Field.TechnicalName].ToString(),
            DisplayName: fields[(int)Bm25Field.DisplayName].ToString(),
            TagsAndOwner: fields[(int)Bm25Field.TagsAndOwner].ToString(),
            ShortDescription: fields[(int)Bm25Field.ShortDescription].ToString(),
            LongDescription: fields[(int)Bm25Field.LongDescription].ToString(),
            ColumnNames: fields[(int)Bm25Field.ColumnNames].ToString(),
            ColumnDescriptions: fields[(int)Bm25Field.ColumnDescriptions].ToString());

        return (doc, combined.ToString());
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
