namespace Autheris.Application.Sql.Policy.RowFilterKeySets;

public static class RowFilterKeySetErrorCodes
{
    /// <summary>The key query failed (connection, command, read, timeout, conversion); the statement is denied, fail closed (SEC-RF107-08).</summary>
    public const string KeySetUnavailable = "ROW_FILTER_KEY_SET_UNAVAILABLE";

    /// <summary>The fixed message of every <see cref="KeySetUnavailable"/> denial: it names neither the dependent tables nor the rule (SEC-RF107-08).</summary>
    public const string UnavailableMessage = "The row-filter key set could not be loaded.";

    /// <summary>The denial of a failed or timed-out load. The cause is attached as inner exception for logs only.</summary>
    public static Autheris.Domain.Exceptions.GatewaySecurityException Unavailable(Exception? cause = null)
        => new(UnavailableMessage, KeySetUnavailable, cause);
}
