using Autheris.Application.Interfaces;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using HotChocolate.Types;

namespace Autheris.GraphQL.DynamicTypes;

public sealed class DynamicTableType : ObjectType
{
    private readonly TableMetadata _metadata;
    private readonly IColumnMaskingProvider _maskingProvider;

    public DynamicTableType(TableMetadata metadata, IColumnMaskingProvider maskingProvider)
    {
        _metadata = metadata;
        _maskingProvider = maskingProvider;
    }

    protected override void Configure(IObjectTypeDescriptor descriptor)
    {
        descriptor.Name($"{_metadata.Identifier.Domain}_{_metadata.Identifier.TableName}");
        descriptor.Description(!string.IsNullOrWhiteSpace(_metadata.Table.Description) ? _metadata.Table.Description : _metadata.Table.DisplayName);

        foreach (var col in _metadata.Columns)
        {
            var fieldDesc = descriptor.Field(col.ColumnName);
            ConfigureType(fieldDesc, col.DataType, _metadata.Dialect);

            var colDesc = FormatColumnMarkdownDescription(col);
            if (!string.IsNullOrWhiteSpace(colDesc))
            {
                fieldDesc.Description(colDesc);
            }

            fieldDesc.Resolve(ctx =>
            {
                var row = ctx.Parent<IReadOnlyDictionary<string, object?>>();
                if (!row.TryGetValue(col.ColumnName, out var val) || val == null || val is DBNull)
                {
                    return null;
                }

                // Strict Fail-Closed: Check ColumnAccess in ScopedContextData or ContextData
                IReadOnlyDictionary<string, ColumnAccessLevel>? colAccess = null;
                if (ctx.ScopedContextData.TryGetValue("ColumnAccess", out var scopedObj) && scopedObj is IReadOnlyDictionary<string, ColumnAccessLevel> sc)
                {
                    colAccess = sc;
                }
                else if (ctx.ContextData.TryGetValue("ColumnAccess", out var accessObj) && accessObj is IReadOnlyDictionary<string, ColumnAccessLevel> ac)
                {
                    colAccess = ac;
                }

                if (colAccess == null ||
                    !colAccess.TryGetValue(col.ColumnName, out var level) ||
                    level == ColumnAccessLevel.Deny)
                {
                    return null;
                }

                if (level == ColumnAccessLevel.Mask)
                {
                    var rule = _metadata.ColumnMaskingRules.TryGetValue(col.ColumnName, out var r)
                        ? r
                        : new MaskingRule { RuleType = "REDACT" };
                    var masked = _maskingProvider.MaskValue(col.ColumnName, val, rule);
                    if (masked is string s && s == "REDACTED" && !IsStringType(col.DataType))
                    {
                        return null;
                    }
                    return masked;
                }

                return val;
            });
        }
    }

    private static bool IsStringType(string dataType)
    {
        var lower = dataType.ToLowerInvariant();
        return lower.Contains("char") || lower.Contains("text") || lower.Contains("string") || lower.Contains("clob") ||
               lower.Contains("geo") || lower.Contains("point") || lower.Contains("polygon") || lower.Contains("spatial") ||
               lower.Contains("byte") || lower.Contains("bin") || lower.Contains("blob");
    }

    private static void ConfigureType(IObjectFieldDescriptor field, string dataType, DatabaseDialect dialect = DatabaseDialect.PostgreSql)
    {
        var lower = dataType.ToLowerInvariant();

        // MSSQL special case: timestamp / rowversion is an 8-byte binary counter, NOT a DateTime!
        if (dialect == DatabaseDialect.SqlServer && (lower == "timestamp" || lower.Contains("rowversion")))
        {
            field.Type<StringType>();
            return;
        }

        if (lower.Contains("byte") || lower.Contains("bin") || lower.Contains("blob") || lower.Contains("raw") ||
            lower.Contains("geo") || lower.Contains("point") || lower.Contains("spatial"))
        {
            field.Type<StringType>();
            return;
        }

        if (lower.Contains("bigint") || lower.Contains("long"))
        {
            field.Type<LongType>();
        }
        else if (lower.Contains("int"))
        {
            field.Type<IntType>();
        }
        else if (lower.Contains("decimal") || lower.Contains("numeric") || lower.Contains("money"))
        {
            field.Type<DecimalType>();
        }
        else if (lower.Contains("bool"))
        {
            field.Type<BooleanType>();
        }
        else if (lower.Contains("date") || lower.Contains("time"))
        {
            field.Type<DateTimeType>();
        }
        else
        {
            field.Type<StringType>();
        }
    }

    private static string? FormatColumnMarkdownDescription(TableColumn col)
    {
        var hasDesc = !string.IsNullOrWhiteSpace(col.Description);
        var longDesc = !string.IsNullOrWhiteSpace(col.LongDescription)
            ? col.LongDescription
            : (col.Meta != null && col.Meta.TryGetValue("long_description", out var ld) && !string.IsNullOrWhiteSpace(ld)
                ? ld
                : (col.Meta != null && col.Meta.TryGetValue("specification", out var spec) && !string.IsNullOrWhiteSpace(spec)
                    ? spec
                    : null));
        var hasLongDesc = !string.IsNullOrWhiteSpace(longDesc);

        if (hasDesc && hasLongDesc)
        {
            return $"{col.Description}\n\n---\n**Ausführliche Spezifikation:**\n{longDesc}";
        }

        if (hasDesc)
        {
            return col.Description;
        }

        if (hasLongDesc)
        {
            return longDesc;
        }

        return null;
    }
}
