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
        var escapedNamespace = System.Security.SecurityElement.Escape(serviceNamespace) ?? serviceNamespace;
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
            var entityName = System.Security.SecurityElement.Escape(rawEntityName) ?? rawEntityName;
            sb.AppendLine($"      <EntityType Name=\"{entityName}\">");

            // Keys (Befund 4a.4: ensure keys always reference existing properties with Nullable="false")
            var keys = ResolveEntityKeys(table);
            sb.AppendLine("        <Key>");
            foreach (var key in keys)
            {
                var escapedKey = System.Security.SecurityElement.Escape(key) ?? key;
                sb.AppendLine($"          <PropertyRef Name=\"{escapedKey}\" />");
            }
            sb.AppendLine("        </Key>");

            // Entity description annotations
            if (!string.IsNullOrWhiteSpace(table.Table.Description))
            {
                var escapedDesc = System.Security.SecurityElement.Escape(table.Table.Description) ?? table.Table.Description;
                sb.AppendLine($"        <Annotation Term=\"Core.Description\" String=\"{escapedDesc}\" />");
            }
            if (!string.IsNullOrWhiteSpace(table.Table.LongDescription))
            {
                var escapedLongDesc = System.Security.SecurityElement.Escape(table.Table.LongDescription) ?? table.Table.LongDescription;
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
                    var escapedColName = System.Security.SecurityElement.Escape(col.ColumnName) ?? col.ColumnName;

                    var hasDesc = !string.IsNullOrWhiteSpace(col.Description);
                    var hasLongDesc = !string.IsNullOrWhiteSpace(col.LongDescription);

                    if (hasDesc || hasLongDesc)
                    {
                        sb.AppendLine($"        <Property Name=\"{escapedColName}\" Type=\"{edmType}\"{nullStr}>");
                        if (hasDesc)
                        {
                            var escapedDesc = System.Security.SecurityElement.Escape(col.Description) ?? col.Description;
                            sb.AppendLine($"          <Annotation Term=\"Core.Description\" String=\"{escapedDesc}\" />");
                        }
                        if (hasLongDesc)
                        {
                            var escapedLongDesc = System.Security.SecurityElement.Escape(col.LongDescription) ?? col.LongDescription;
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
            var entityName = System.Security.SecurityElement.Escape(rawEntityName) ?? rawEntityName;
            sb.AppendLine($"        <EntitySet Name=\"{entityName}\" EntityType=\"{escapedNamespace}.{entityName}\" />");
        }
        sb.AppendLine("      </EntityContainer>");

        sb.AppendLine("    </Schema>");
        sb.AppendLine("  </edmx:DataServices>");
        sb.AppendLine("</edmx:Edmx>");

        return sb.ToString();
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
    /// 1. Declared primary keys from catalog matching an existing column.
    /// 2. Existing column named 'id' (case-insensitive).
    /// 3. All table columns as a composite key (read-only query gateway).
    /// 4. Fallback to 'id' if the table has zero columns.
    /// </summary>
    public static IReadOnlyList<string> ResolveEntityKeys(TableMetadata table)
    {
        var validKeys = new List<string>();
        if (table.PrimaryKeyColumns.Count > 0)
        {
            foreach (var pk in table.PrimaryKeyColumns)
            {
                var match = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, pk, StringComparison.OrdinalIgnoreCase));
                if (match != null && !validKeys.Contains(match.ColumnName, StringComparer.OrdinalIgnoreCase))
                {
                    validKeys.Add(match.ColumnName);
                }
            }
        }

        if (validKeys.Count > 0)
        {
            return validKeys;
        }

        var idCol = table.Columns.FirstOrDefault(c => string.Equals(c.ColumnName, "id", StringComparison.OrdinalIgnoreCase));
        if (idCol != null)
        {
            return [idCol.ColumnName];
        }

        if (table.Columns.Count > 0)
        {
            return table.Columns.Select(c => c.ColumnName).ToList();
        }

        return ["id"];
    }
}

