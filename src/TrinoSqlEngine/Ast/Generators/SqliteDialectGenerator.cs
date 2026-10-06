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
