namespace Autheris.Infrastructure.Persistence.Dialects;

using System;
using System.Data.Common;
using Autheris.Domain.Interfaces;

/// <summary>
/// Engine-specific SQL fragment and connection provider for the unified governance repository.
/// Has exactly the 11 divergence members mandated by the architecture spec.
/// </summary>
internal interface IGovernanceSqlDialect
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
