namespace TrinoSqlEngine.Ast.Buffer;

using System;
using System.Globalization;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Generators;

/// <summary>
/// Encapsulates state during SQL dialect generation (dialect, parameter tracking, indentation, context).
/// </summary>
public sealed class SqlEmitterContext
{
    public TargetSqlDialect Dialect { get; init; }
    public int MaxParameterBudget { get; init; }
    public int ParameterCount { get; set; }
    public bool InPredicateContext { get; set; }
    public bool InProjectionContext { get; set; }
    public int IndentLevel { get; set; }

    public SqlEmitterContext(TargetSqlDialect dialect, int maxParameterBudget = int.MaxValue)
    {
        Dialect = dialect;
        MaxParameterBudget = maxParameterBudget;
        ParameterCount = 0;
    }

    public void CheckParameterBudget(int additional = 1)
    {
        if (ParameterCount + additional > MaxParameterBudget)
        {
            throw new DialectLimitExceededException(
                $"Target dialect '{Dialect}' parameter budget exceeded: requested {ParameterCount + additional}, maximum allowed is {MaxParameterBudget}.");
        }
    }

    public string NextParameterMarker()
    {
        CheckParameterBudget(1);
        return Dialect switch
        {
            TargetSqlDialect.SqlServer => $"@p{ParameterCount++}",
            TargetSqlDialect.PostgreSql => $"${++ParameterCount}",
            TargetSqlDialect.Sqlite => $"?{++ParameterCount}",
            TargetSqlDialect.DuckDb => $"${++ParameterCount}",
            TargetSqlDialect.Snowflake => $":{++ParameterCount}",
            _ => $"?{++ParameterCount}"
        };
    }
}
