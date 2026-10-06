namespace TrinoSqlEngine.Ast.Generators;

using System;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Buffer;
using TrinoSqlEngine.Ast.Nodes;

/// <summary>
/// ANSI standard SQL code generator.
/// </summary>
public sealed class AnsiDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.Ansi;
    public override int MaxParameterBudget => int.MaxValue;

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
        builder.Append(value ? "TRUE" : "FALSE");
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.Limit != null)
        {
            builder.Append("LIMIT ");
            GenerateExpression(pagination.Limit, ref builder, context);
        }

        if (pagination.Offset != null)
        {
            if (pagination.Limit != null) builder.Append(' ');
            builder.Append("OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
        }
    }
}

/// <summary>
/// DuckDB Lakehouse analytical dialect generator.
/// </summary>
public sealed class DuckDbDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.DuckDb;
    public override int MaxParameterBudget => 65535;

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
        builder.Append(value ? "TRUE" : "FALSE");
    }

    protected override void GeneratePagination(PaginationClause pagination, OrderByClause? orderBy, ref ValueStringBuilder builder, SqlEmitterContext context)
    {
        if (pagination.Limit != null)
        {
            builder.Append("LIMIT ");
            GenerateExpression(pagination.Limit, ref builder, context);
        }

        if (pagination.Offset != null)
        {
            if (pagination.Limit != null) builder.Append(' ');
            builder.Append("OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
        }
    }
}

/// <summary>
/// Snowflake analytical dialect generator with uppercase folding for unquoted identifiers.
/// </summary>
public sealed class SnowflakeDialectGenerator : SqlDialectGeneratorBase
{
    public override TargetSqlDialect TargetDialect => TargetSqlDialect.Snowflake;
    public override int MaxParameterBudget => 65535;

    public override void FormatIdentifier(ref ValueStringBuilder builder, SqlIdentifier identifier, SqlEmitterContext context)
    {
        if (identifier.IsQuoted)
        {
            builder.Append('"');
            builder.Append(identifier.Value.Replace("\"", "\"\"", StringComparison.Ordinal));
            builder.Append('"');
        }
        else
        {
            builder.Append(identifier.Value.ToUpperInvariant());
        }
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
        if (pagination.Limit != null)
        {
            builder.Append("LIMIT ");
            GenerateExpression(pagination.Limit, ref builder, context);
        }

        if (pagination.Offset != null)
        {
            if (pagination.Limit != null) builder.Append(' ');
            builder.Append("OFFSET ");
            GenerateExpression(pagination.Offset, ref builder, context);
        }
    }
}
