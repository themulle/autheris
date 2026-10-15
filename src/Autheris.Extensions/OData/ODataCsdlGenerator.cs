namespace Autheris.Extensions.OData;

using System;
using System.Collections.Generic;
using System.Text;
using Autheris.Domain.Model;

public static class ODataCsdlGenerator
{
    public static string GenerateMetadataXml(IReadOnlyList<TableMetadata> tables, string serviceNamespace = "Autheris.OData")
    {
        var sb = new StringBuilder();
        var escapedNamespace = EscapeAttribute(serviceNamespace);
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<edmx:Edmx Version=\"4.0\" xmlns:edmx=\"http://docs.oasis-open.org/odata/ns/edmx\">");
        sb.AppendLine("  <edmx:Reference Uri=\"https://oasis-tcs.github.io/odata-vocabularies/vocabularies/Org.OData.Core.V1.xml\">");
        sb.AppendLine("    <edmx:Include Namespace=\"Org.OData.Core.V1\" Alias=\"Core\" />");
        sb.AppendLine("  </edmx:Reference>");
        sb.AppendLine("  <edmx:DataServices>");
        sb.AppendLine($"    <Schema Namespace=\"{escapedNamespace}\" xmlns=\"http://docs.oasis-open.org/odata/ns/edm\">");

        // 1. Generate EntityTypes
        foreach (var table in tables)
        {
            var rawEntityName = GetEntityName(table);
            var entityName = EscapeAttribute(rawEntityName);
            sb.AppendLine($"      <EntityType Name=\"{entityName}\">");

            // Keys (Befund 4a.4: ensure keys always reference existing properties with Nullable="false")
            var keys = ResolveEntityKeys(table);
            sb.AppendLine("        <Key>");
            foreach (var key in keys)
            {
                var escapedKey = EscapeAttribute(key);
                sb.AppendLine($"          <PropertyRef Name=\"{escapedKey}\" />");
            }
            sb.AppendLine("        </Key>");

            // Entity description annotations
            if (!string.IsNullOrWhiteSpace(table.Table.Description))
            {
                var escapedDesc = EscapeAttribute(table.Table.Description);
                sb.AppendLine($"        <Annotation Term=\"Core.Description\" String=\"{escapedDesc}\" />");
            }
            if (!string.IsNullOrWhiteSpace(table.Table.LongDescription))
            {
                var escapedLongDesc = EscapeAttribute(table.Table.LongDescription);
                sb.AppendLine($"        <Annotation Term=\"Core.LongDescription\" String=\"{escapedLongDesc}\" />");
            }

            // Properties
            if (table.Columns.Count > 0)
            {
                foreach (var col in table.Columns)
                {
                    var edmType = MapToEdmType(col.DataType);
                    var isKey = keys.Contains(col.ColumnName, StringComparer.OrdinalIgnoreCase);
                    var nullStr = isKey ? " Nullable=\"false\"" : "";
                    var escapedColName = EscapeAttribute(col.ColumnName);

                    var hasDesc = !string.IsNullOrWhiteSpace(col.Description);
                    var hasLongDesc = !string.IsNullOrWhiteSpace(col.LongDescription);

                    if (hasDesc || hasLongDesc)
                    {
                        sb.AppendLine($"        <Property Name=\"{escapedColName}\" Type=\"{edmType}\"{nullStr}>");
                        if (hasDesc)
                        {
                            var escapedDesc = EscapeAttribute(col.Description);
                            sb.AppendLine($"          <Annotation Term=\"Core.Description\" String=\"{escapedDesc}\" />");
                        }
                        if (hasLongDesc)
                        {
                            var escapedLongDesc = EscapeAttribute(col.LongDescription);
                            sb.AppendLine($"          <Annotation Term=\"Core.LongDescription\" String=\"{escapedLongDesc}\" />");
                        }
                        sb.AppendLine("        </Property>");
                    }
                    else
                    {
                        sb.AppendLine($"        <Property Name=\"{escapedColName}\" Type=\"{edmType}\"{nullStr} />");
                    }
                }
            }
            else
            {
                // Fallback id property if no columns defined
                sb.AppendLine("        <Property Name=\"id\" Type=\"Edm.Int32\" Nullable=\"false\" />");
            }

            sb.AppendLine("      </EntityType>");
        }

        // 2. Generate EntityContainer & EntitySets
        sb.AppendLine("      <EntityContainer Name=\"Container\">");
        foreach (var table in tables)
        {
            var rawEntityName = GetEntityName(table);
            var entityName = EscapeAttribute(rawEntityName);
            sb.AppendLine($"        <EntitySet Name=\"{entityName}\" EntityType=\"{escapedNamespace}.{entityName}\" />");
        }
        sb.AppendLine("      </EntityContainer>");

        sb.AppendLine("    </Schema>");
        sb.AppendLine("  </edmx:DataServices>");
        sb.AppendLine("</edmx:Edmx>");

        return sb.ToString();
    }

    /// <summary>
    /// Escapes XML attribute values. In addition to standard XML characters (&lt;, &gt;, &amp;, &quot;, &apos;),
    /// converts line breaks and tabs into XML character references (&amp;#10;, &amp;#13;, &amp;#9;) to prevent
    /// raw unescaped newlines in attributes which break strict XML parsers such as Microsoft Excel's OData feed consumer.
    /// </summary>
    public static string EscapeAttribute(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var escaped = System.Security.SecurityElement.Escape(value) ?? value;
        return escaped
            .Replace("\r\n", "&#10;")
            .Replace("\r", "&#13;")
            .Replace("\n", "&#10;")
            .Replace("\t", "&#9;");
    }

    public static string GetEntityName(TableMetadata table)
    {
        var id = table.Identifier;
        return $"{id.Domain}_{id.Schema}_{id.TableName}";
    }

    public static string MapToEdmType(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return "Edm.String";
        }

        var normalized = dataType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "int" or "integer" or "int4" or "serial" => "Edm.Int32",
            "bigint" or "int8" or "bigserial" => "Edm.Int64",
            "smallint" or "int2" => "Edm.Int16",
            "decimal" or "numeric" or "money" => "Edm.Decimal",
            "float" or "real" or "float4" => "Edm.Single",
            "double" or "float8" or "double precision" => "Edm.Double",
            "bool" or "boolean" or "bit" => "Edm.Boolean",
            "date" => "Edm.Date",
            "timestamp" or "timestamptz" or "datetime" or "datetime2" or "datetimeoffset" => "Edm.DateTimeOffset",
            "guid" or "uuid" => "Edm.Guid",
            _ => "Edm.String"
        };
    }

    /// <summary>
    /// Befund 4a.4: Resolves entity keys referencing strictly existing properties of the entity type.
    /// Priority:
    /// 1. Declared primary keys from catalog where all columns are present in <see cref="TableMetadata.Columns"/>.
    /// 2. Declared unique index in <see cref="TableMetadata.Indexes"/> (fewest columns, all present in <see cref="TableMetadata.Columns"/>).
    /// 3. Column <c>Meta</c> primary key markers (<c>primary_key_ordinal</c>, <c>is_primary_key</c>, <c>role=primary_key</c>).
    /// 4. Existing column named <c>id</c> (case-insensitive).
    /// 5. Conventional <c>(client_id, ts)</c> if both present, else <c>client_id</c> if present.
    /// 6. All table columns as a composite key (or <c>id</c> if the table has zero columns).
    /// </summary>
    public static IReadOnlyList<string> ResolveEntityKeys(TableMetadata table)
    {
        if (table.PrimaryKeyColumns.Count > 0 &&
            TryMatchAllColumns(table, table.PrimaryKeyColumns, out var pkKeys))
        {
            return pkKeys;
        }

        if (table.Indexes.Count > 0)
        {
            foreach (var index in table.Indexes
                         .Where(i => i is { IsUnique: true, Columns.Count: > 0 })
                         .OrderBy(i => i.Columns.Count))
            {
                if (TryMatchAllColumns(table, index.Columns, out var indexKeys))
                {
                    return indexKeys;
                }
            }
        }

        var metaKeys = Autheris.Application.Data.Services.GovernedTableReadPlanner.ExtractMetaPrimaryKeyColumns(table.Columns);
        if (metaKeys.Count > 0 && TryMatchAllColumns(table, metaKeys, out var resolvedMetaKeys))
        {
            return resolvedMetaKeys;
        }

        var idCol = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "id", StringComparison.OrdinalIgnoreCase));
        if (idCol != null)
        {
            return [idCol.ColumnName];
        }

        var clientIdCol = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "client_id", StringComparison.OrdinalIgnoreCase));
        if (clientIdCol != null)
        {
            var tsCol = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "ts", StringComparison.OrdinalIgnoreCase));
            if (tsCol != null)
            {
                return [clientIdCol.ColumnName, tsCol.ColumnName];
            }

            return [clientIdCol.ColumnName];
        }

        if (table.Columns.Count > 0)
        {
            return table.Columns.Select(c => c.ColumnName).ToList();
        }

        return ["id"];
    }

    private static bool TryMatchAllColumns(TableMetadata table, IReadOnlyList<string> candidates, out List<string> matched)
    {
        matched = new List<string>(candidates.Count);
        foreach (var colName in candidates)
        {
            var match = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, colName, StringComparison.OrdinalIgnoreCase));
            if (match == null)
            {
                matched.Clear();
                return false;
            }

            if (!matched.Contains(match.ColumnName, StringComparer.OrdinalIgnoreCase))
            {
                matched.Add(match.ColumnName);
            }
        }

        return matched.Count > 0;
    }
}

