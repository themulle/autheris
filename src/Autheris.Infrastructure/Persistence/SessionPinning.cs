using Autheris.Application.Interfaces;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// WP-X0b (SEC-X1-08): session pins applied by <see cref="SqlConnectionFactory"/> on every rental. All statements are constant;
/// the default schema is a bound value that the database quotes itself (<c>format('%I')</c>, <c>DBMS_ASSERT.ENQUOTE_NAME</c>).
/// Every pin is verified inside the database; a mismatch makes the request fail closed.
/// </summary>
public static class SessionPinning
{
    /// <summary>Sets <c>search_path = pg_catalog, "schema", pg_temp</c> for the session and returns whether the server holds exactly that value.</summary>
    public const string PostgreSqlSearchPathPinSql =
        "SELECT set_config('search_path', format('pg_catalog, %I, pg_temp', @schema::text), false) = format('pg_catalog, %I, pg_temp', @schema::text)";

    /// <summary>
    /// <c>SCHEMA_NAME</c> raises for a schema that does not exist; the verification compares the session's CURRENT_SCHEMA with the
    /// bound value. Any deviation raises and the connection is disposed.
    /// </summary>
    public const string OracleSchemaPinBlock = """
        BEGIN
          EXECUTE IMMEDIATE 'ALTER SESSION SET CURRENT_SCHEMA = ' || DBMS_ASSERT.ENQUOTE_NAME(DBMS_ASSERT.SCHEMA_NAME(:schema), FALSE);
          IF SYS_CONTEXT('USERENV', 'CURRENT_SCHEMA') <> :schema THEN
            RAISE_APPLICATION_ERROR(-20002, 'Oracle current schema is not pinned');
          END IF;
        END;
        """;

    /// <summary>
    /// ANSI_WARNINGS, ANSI_NULLS, ANSI_PADDING, CONCAT_NULL_YIELDS_NULL, ARITHABORT and QUOTED_IDENTIFIER on, NUMERIC_ROUNDABORT off.
    /// SqlClient resets pooled sessions with sp_reset_connection, so this runs on every rental.
    /// </summary>
    public const string SqlServerAnsiPinSql =
        "SET ANSI_WARNINGS ON; SET ANSI_NULLS ON; SET ANSI_PADDING ON; SET CONCAT_NULL_YIELDS_NULL ON; SET ARITHABORT ON; SET QUOTED_IDENTIFIER ON; SET NUMERIC_ROUNDABORT OFF;";

    /// <summary>@@OPTIONS bits: ANSI_WARNINGS 8, ANSI_PADDING 16, ANSI_NULLS 32, ARITHABORT 64, QUOTED_IDENTIFIER 256, CONCAT_NULL_YIELDS_NULL 4096, NUMERIC_ROUNDABORT 8192.</summary>
    public const string SqlServerAnsiVerifySql =
        "SELECT CASE WHEN (@@OPTIONS & 4472) = 4472 AND (@@OPTIONS & 8192) = 0 THEN 1 ELSE 0 END";

    /// <summary>Fails closed unless the database confirmed the pin. The message names the pin, never a configured value.</summary>
    public static void EnsurePinned(bool? verified, string what)
    {
#if DEBUG
        if (TrinoSqlEngine.CompilerTestSeams.IsDisabled(TrinoSqlEngine.SecurityControl.SessionPinReadBack))
        {
            return;
        }
#endif
        if (verified != true)
        {
            throw new SqlSourceUnavailableException($"The session pin '{what}' could not be verified.");
        }
    }
}
