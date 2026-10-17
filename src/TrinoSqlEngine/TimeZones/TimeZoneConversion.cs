namespace TrinoSqlEngine;

using TrinoSqlEngine.Ast.Nodes;

/// <summary>Public facade of the time zone call validator for the federated planner, which builds DuckDB SQL without the governed compiler.</summary>
public static class TimeZoneConversion
{
    /// <exception cref="SqlTimeZoneException">The statement contains a convert_timezone call that is not valid for the federated DuckDB path.</exception>
    public static void ValidateFederated(SqlStatement statement, bool duckDbAvailable) => throw new NotImplementedException();
}
