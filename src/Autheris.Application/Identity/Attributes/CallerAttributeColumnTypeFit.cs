namespace Autheris.Application.Identity.Attributes;

using System;
using Autheris.Domain.Model;

/// <summary>
/// The type fit of a caller attribute and the catalogued column it is compared with (V-129-4, BC-129-16). A clear mismatch (a text attribute
/// against a numeric column, a number against a text column) is refused at save time; a type the catalog does not classify is left to the
/// database, which fails closed at run time without a value in any message.
/// </summary>
public static class CallerAttributeColumnTypeFit
{
    private enum Family { Unknown, Text, Whole, Number, Flag, Uuid, Other }

    /// <summary>False only for a clear mismatch between the attribute type and the declared column data type.</summary>
    public static bool Fits(CallerAttributeType attribute, string? columnDataType)
    {
        var column = Classify(columnDataType);
        return column == Family.Unknown || attribute switch
        {
            CallerAttributeType.Text => column == Family.Text,
            CallerAttributeType.Whole => column is Family.Whole or Family.Number,
            CallerAttributeType.Number => column is Family.Whole or Family.Number,
            CallerAttributeType.Flag => column is Family.Flag or Family.Whole,
            CallerAttributeType.Uuid => column is Family.Uuid or Family.Text,
            _ => true
        };
    }

    private static Family Classify(string? dataType)
    {
        if (string.IsNullOrWhiteSpace(dataType))
        {
            return Family.Unknown;
        }

        var span = dataType.AsSpan().Trim();
        int paren = span.IndexOf('(');
        if (paren >= 0)
        {
            span = span[..paren].Trim();
        }

        var name = span.ToString().ToLowerInvariant();
        return name switch
        {
            "char" or "varchar" or "nchar" or "nvarchar" or "text" or "ntext" or "string" or "clob" or "character" or "character varying" or "varchar2" or "nvarchar2" or "citext" => Family.Text,
            "int" or "integer" or "bigint" or "smallint" or "tinyint" or "int2" or "int4" or "int8" or "serial" or "bigserial" or "mediumint" => Family.Whole,
            "decimal" or "numeric" or "float" or "real" or "double" or "double precision" or "money" or "smallmoney" or "number" or "float4" or "float8" => Family.Number,
            "bit" or "boolean" or "bool" => Family.Flag,
            "uuid" or "uniqueidentifier" => Family.Uuid,
            _ => Family.Unknown
        };
    }
}
