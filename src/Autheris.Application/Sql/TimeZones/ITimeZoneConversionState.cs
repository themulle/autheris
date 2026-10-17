namespace Autheris.Application.Sql.TimeZones;

using TrinoSqlEngine;

/// <summary>
/// REQ-SQL-130 (A-7): whether <c>convert_timezone</c> is proven for a dialect on this node. The answer is false until the state is evaluated and
/// for every dialect without a conversion form (fail closed).
/// </summary>
public interface ITimeZoneConversionState
{
    bool IsAvailable(TargetSqlDialect dialect);
}
