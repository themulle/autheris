namespace Autheris.Infrastructure.Persistence.Dialects;

using System;
using System.Data.Common;
using Autheris.Domain.Interfaces;

/// <summary>
/// Engine-specific SQL fragment and connection provider for the unified governance repository.
/// Has exactly the 11 divergence members mandated by the architecture spec.
/// </summary>
public interface IGovernanceSqlDialect
{
    string Upsert(GovernanceTable table, ReadOnlySpan<string> keyColumns, ReadOnlySpan<string> valueColumns);
    string InsertReturning(string column);
    string LockRowsForUpdate { get; }
    string UtcNow { get; }
    string JsonValue(string column, string path);
    string StringAggregate(string expr, string separator);
    string LimitWithoutOrder(int n);
    bool IsUniqueViolation(DbException ex);
    bool IsTransient(DbException ex);
    IParameterBudgetProvider ParameterBudget { get; }
    DbConnection CreateConnection();
}

/// <summary>Strict validation of dialect SQL-fragment inputs.</summary>
internal static partial class GovernanceSqlInput
{
    private static readonly string[] Separators = [",", ", ", ";", "|", " "];

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9_.]+$")]
    private static partial System.Text.RegularExpressions.Regex Safe();

    public static string Identifier(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return Safe().IsMatch(value) ? value : throw new ArgumentException("Unsafe SQL identifier or path.", name);
    }

    public static string Separator(string? separator)
    {
        separator ??= ",";
        return Array.IndexOf(Separators, separator) >= 0 ? separator : throw new ArgumentException("Separator not allowed.", nameof(separator));
    }
}
