namespace TrinoSqlEngine.Ast.Generators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// PostgreSQL code generator.
/// Handles "identifier" double-quote quoting with "" escaping, $1 parameter placeholders (budget 65,535),
/// native TRUE/FALSE booleans, standard-conforming string literals, and LIMIT ... OFFSET ... pagination.
/// </summary>
public sealed class PostgreSqlDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.PostgreSql;
    protected override bool SupportsAggregateFilter => true;
    protected override bool SupportsOrderedAggregates => true;
    protected override bool SupportsGroupByDistinct => true;

    /// <summary>Wunsch 4: PostgreSQL has no double, tinyint or varbinary.</summary>
    protected override string FormatTypeName(TrinoType type) => type.Name switch
    {
        "double" => "double precision",
        "tinyint" => "smallint",
        "varbinary" => "bytea",
        _ => type.Normalized
    };
    public override int MaxParameterBudget => 65535;

    /// <summary>Virtual filters (phase 7b): <c>(x + (n) * INTERVAL '1 unit')</c>.</summary>
    protected override void FormatDateAdd(ref ValueStringBuilder builder, DateUnit unit, long amount, Expression source, SqlEmitterContext context)
    {
        builder.Append('(');
        GenerateExpression(source, ref builder, context);
        builder.Append(" + (");
        builder.Append(amount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        builder.Append(") * INTERVAL '1 ");
        builder.Append(DateUnitName(unit));
        builder.Append("')");
    }

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        builder.Append('"');
        string val = identifier.IsQuoted ? identifier.Value : identifier.Value.ToLowerInvariant();
        builder.Append(val.Replace("\"", "\"\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    public override void FormatStringLiteral(ref ValueStringBuilder builder, string value, SqlEmitterContext context)
    {
        builder.Append('\'');
        builder.Append(value.Replace("'", "''", StringComparison.Ordinal));
        builder.Append('\'');
    }

    public override void FormatBoolean(ref ValueStringBuilder builder, bool value, SqlEmitterContext context)
    {
        builder.Append(value ? "TRUE" : "FALSE");
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        bool hasLimit = pagination.Limit != null;
        if (hasLimit)
        {
            builder.Append("LIMIT ");
            GenerateExpression(pagination.Limit!, ref builder, context);
        }

        if (pagination.Offset != null)
        {
            if (hasLimit) builder.Append(' ');
            builder.Append("OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
        }
    }
}
