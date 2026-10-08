namespace TrinoSqlEngine.Ast.Generators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// SQLite code generator.
/// Handles "identifier" ANSI double-quote quoting with "" escaping, ?1 parameter placeholders (budget 999),
/// integer booleans (1/0), standard strings, and LIMIT ... OFFSET ... pagination.
/// </summary>
public sealed class SqliteDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.Sqlite;
    protected override bool SupportsAggregateFilter => true;
    protected override bool SupportsGroupingSets => false;

    protected override string SubstringFunctionName => "SUBSTR";

    protected override void FormatCurrentDateTime(ref ValueStringBuilder builder, CurrentDateTimeKind kind, SqlEmitterContext context)
    {
        builder.Append(kind switch
        {
            CurrentDateTimeKind.CurrentDate => "CURRENT_DATE",
            CurrentDateTimeKind.CurrentTime => "CURRENT_TIME",
            CurrentDateTimeKind.CurrentTimestamp => "CURRENT_TIMESTAMP",
            CurrentDateTimeKind.LocalTime => "time('now', 'localtime')",
            _ => "datetime('now', 'localtime')"
        });
    }

    /// <summary>SQLite: TRIM/LTRIM/RTRIM(x[, chars]).</summary>
    protected override void FormatTrim(ref ValueStringBuilder builder, TrimExpression trim, SqlEmitterContext context)
    {
        builder.Append(trim.Specification switch
        {
            TrimSpecification.Leading => "LTRIM(",
            TrimSpecification.Trailing => "RTRIM(",
            _ => "TRIM("
        });
        GenerateExpression(trim.Source, ref builder, context);
        if (trim.Characters != null)
        {
            builder.Append(", ");
            GenerateExpression(trim.Characters, ref builder, context);
        }
        builder.Append(')');
    }

    protected override void FormatPosition(ref ValueStringBuilder builder, PositionExpression position, SqlEmitterContext context) =>
        FormatInstr(ref builder, position, context);

    /// <summary>Wunsch 4: SQLite has no EXTRACT; strftime on ISO text, with the ISO day of week (%w counts Sunday = 0).</summary>
    protected override void FormatExtract(ref ValueStringBuilder builder, string field, Expression source, SqlEmitterContext context)
    {
        (string format, string prefix, string suffix) = field switch
        {
            "YEAR" => ("%Y", "", ""),
            "MONTH" => ("%m", "", ""),
            "DAY" => ("%d", "", ""),
            "HOUR" => ("%H", "", ""),
            "MINUTE" => ("%M", "", ""),
            "SECOND" => ("%S", "", ""),
            "DAY_OF_YEAR" => ("%j", "", ""),
            "DAY_OF_WEEK" => ("%w", "((", " + 6) % 7 + 1)"),
            "QUARTER" => ("%m", "((", " + 2) / 3)"),
            _ => throw UnsupportedExtract(field, TargetDialect)
        };
        builder.Append(prefix);
        builder.Append("CAST(strftime('");
        builder.Append(format);
        builder.Append("', ");
        GenerateExpression(source, ref builder, context);
        builder.Append(") AS INTEGER)");
        builder.Append(suffix);
    }

    /// <summary>Wunsch 4: SQLite stores dates as ISO text and has no typed literals; the ISO string compares correctly.</summary>
    protected override void FormatTypedLiteral(ref ValueStringBuilder builder, TypedLiteralExpression literal, SqlEmitterContext context) =>
        FormatStringLiteral(ref builder, literal.Value, context);

    protected override void FormatIntervalLiteral(ref ValueStringBuilder builder, IntervalLiteralExpression interval, SqlEmitterContext context) =>
        throw new TrinoSqlEngine.Ast.Builder.AstBuildException($"SQL construct INTERVAL literal is not supported for {TargetDialect} (no interval type).");

    public override int MaxParameterBudget => 999;

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        builder.Append('"');
        builder.Append(identifier.Value.Replace("\"", "\"\"", StringComparison.Ordinal));
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
        builder.Append(value ? '1' : '0');
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.Limit != null)
        {
            builder.Append("LIMIT ");
            GenerateExpression(pagination.Limit, ref builder, context);
        }
        else if (pagination.Offset != null)
        {
            builder.Append("LIMIT -1");
        }

        if (pagination.Offset != null)
        {
            builder.Append(" OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
        }
    }
}
