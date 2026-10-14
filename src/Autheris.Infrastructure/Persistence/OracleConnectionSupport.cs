using System.Security;
using Oracle.ManagedDataAccess.Client;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// WP-F1: connection string rules of the Oracle runtime. The gateway only ever connects with an ordinary, non-privileged
/// application account over TCPS (outside Development). SYSDBA, SYSOPER, SYSASM, OS authentication and proxy logins are
/// rejected, so a mis-configured data source cannot give the governed path database-administrator rights.
/// </summary>
public static class OracleConnectionStringPolicy
{
    public static void Validate(string connectionString, bool requireTcps)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        OracleConnectionStringBuilder builder;
        try
        {
            builder = new OracleConnectionStringBuilder(connectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new SecurityException("The Oracle connection string is not valid.");
        }

        if (!string.IsNullOrWhiteSpace(builder.DBAPrivilege))
        {
            throw new SecurityException("Privileged Oracle logins (DBA Privilege) are not permitted.");
        }

        string user = builder.UserID?.Trim() ?? string.Empty;
        if (user.Length == 0 || user == "/")
        {
            throw new SecurityException("Oracle logins need an explicit database user; OS authentication is not permitted.");
        }

        // CR-ADG-08: a delimited user name ("SYSTEM") is the same account; strip quotes and blanks before the comparison.
        string unquoted = user.Trim('"', '\'', ' ');
        if (unquoted.Length == 0)
        {
            throw new SecurityException("Oracle logins need an explicit database user; OS authentication is not permitted.");
        }

        if (unquoted.Equals("SYS", StringComparison.OrdinalIgnoreCase) || unquoted.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityException("Built-in administrative Oracle accounts are not permitted.");
        }

        if (HasValue(builder, "Proxy User Id") || HasValue(builder, "Proxy Password"))
        {
            throw new SecurityException("Oracle proxy authentication is not permitted.");
        }

        if (requireTcps && !UsesTcps(builder.DataSource))
        {
            throw new SecurityException("Oracle connections outside Development must use TCPS.");
        }
    }

    /// <summary>True when the connection string carries a literal (non-empty) <c>Password</c>.</summary>
    public static bool HasPlaintextPassword(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        try
        {
            return !string.IsNullOrEmpty(new OracleConnectionStringBuilder(connectionString).Password);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new SecurityException("The Oracle connection string is not valid.");
        }
    }

    private static bool HasValue(OracleConnectionStringBuilder builder, string key) =>
        builder.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value?.ToString());

    private static bool UsesTcps(string? dataSource)
    {
        if (string.IsNullOrWhiteSpace(dataSource)) return false;
        string compact = new string(dataSource.Where(c => !char.IsWhiteSpace(c)).ToArray());
        if (compact.StartsWith("tcps://", StringComparison.OrdinalIgnoreCase)) return true;
        return compact.Contains("(PROTOCOL=TCPS)", StringComparison.OrdinalIgnoreCase) &&
               !compact.Contains("(PROTOCOL=TCP)", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// WP-F2: session semantics of an Oracle connection. ODP.NET pools keep <c>ALTER SESSION</c> state across rentals, so the block
/// runs on every pool rental, pins everything that changes comparison, escaping or formatting semantics and verifies the result
/// inside the database (a deviation raises, the connection is disposed and the request fails closed, INV-14, SEC-ADG-14).
/// The statement text is constant: it contains no tenant, user or request data.
/// </summary>
public static class OracleSessionInitialization
{
    public const string PinAndVerifyBlock = """
        DECLARE
          v_comp VARCHAR2(64);
        BEGIN
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_COMP = ''BINARY''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_SORT = ''BINARY''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_LANGUAGE = ''AMERICAN''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_TERRITORY = ''AMERICA''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_NUMERIC_CHARACTERS = ''.,''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_DATE_FORMAT = ''YYYY-MM-DD''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_TIMESTAMP_FORMAT = ''YYYY-MM-DD"T"HH24:MI:SS.FF6''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET NLS_TIMESTAMP_TZ_FORMAT = ''YYYY-MM-DD"T"HH24:MI:SS.FF6TZH:TZM''';
          EXECUTE IMMEDIATE 'ALTER SESSION SET TIME_ZONE = ''+00:00''';
          DBMS_SESSION.CLEAR_IDENTIFIER;
          SELECT value INTO v_comp FROM nls_session_parameters WHERE parameter = 'NLS_COMP';
          IF v_comp <> 'BINARY'
             OR SYS_CONTEXT('USERENV', 'NLS_SORT') <> 'BINARY'
             OR SYS_CONTEXT('USERENV', 'NLS_DATE_FORMAT') <> 'YYYY-MM-DD'
             OR SYS_CONTEXT('USERENV', 'NLS_TERRITORY') <> 'AMERICA' THEN
            RAISE_APPLICATION_ERROR(-20001, 'Oracle session semantics are not pinned');
          END IF;
        END;
        """;
}
