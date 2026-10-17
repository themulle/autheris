namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

public static class RowFilterKeySetErrorCodes
{
    /// <summary>The key query failed (connection, command, read, timeout, conversion); the statement is denied, fail closed (SEC-RF107-08).</summary>
    public const string KeySetUnavailable = "ROW_FILTER_KEY_SET_UNAVAILABLE";
}
