using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;

namespace Autheris.Infrastructure.Persistence;

public sealed class DatabaseParameterBudgetProvider : IParameterBudgetProvider
{
    public static readonly DatabaseParameterBudgetProvider Instance = new();

    public int GetMaxParameters(DatabaseDialect dialect) => dialect switch
    {
        DatabaseDialect.Sqlite => 999,
        DatabaseDialect.Oracle => 1000,
        DatabaseDialect.SqlServer => 2100,
        DatabaseDialect.PostgreSql => 10000,
        DatabaseDialect.Databricks => 10000,
        _ => 999
    };

    public int CalculateEffectiveChunkSize(
        int defaultChunkSize,
        int keyColumnCount,
        int contextParameterCount = 0,
        DatabaseDialect dialect = DatabaseDialect.Sqlite,
        int safetyBuffer = 50)
    {
        int maxEngineParams = GetMaxParameters(dialect);
        int cols = Math.Max(1, keyColumnCount);
        int available = Math.Max(1, maxEngineParams - Math.Max(0, contextParameterCount) - Math.Max(0, safetyBuffer));
        int sizeFromBudget = available / cols;

        return Math.Max(1, Math.Min(defaultChunkSize, sizeFromBudget));
    }
}
