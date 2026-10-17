namespace TrinoSqlEngine;

/// <summary>Result of <see cref="TimeZoneRegistry.SelfTest"/>. <c>FailedCheck</c> is a constant check id, never data.</summary>
public sealed record TimeZoneSelfTestResult(bool Passed, string? FailedCheck, bool CrossCheckSkipped);

/// <summary>
/// REQ-SQL-130 (A-2): the closed set of time zones the gateway admits, built once from an embedded resource. The value that reaches a
/// database is always registry-owned text, never the caller's string instance.
/// </summary>
public static class TimeZoneRegistry
{
    public static int Count => throw new NotImplementedException();

    /// <summary>Ordinal, case-sensitive membership; a value longer than 64 characters is never a member.</summary>
    public static bool Contains(string ianaId) => throw new NotImplementedException();

    /// <summary>SQL Server: the Windows zone id. PostgreSQL and DuckDB: the registry's IANA id. Any other dialect: false.</summary>
    public static bool TryResolve(TargetSqlDialect dialect, string ianaId, out string dialectValue) => throw new NotImplementedException();

    public static TimeZoneSelfTestResult SelfTest() => throw new NotImplementedException();
}
