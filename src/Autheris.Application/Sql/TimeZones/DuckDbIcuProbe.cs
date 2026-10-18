namespace Autheris.Application.Sql.TimeZones;

using Autheris.Application.Olap;
using DuckDB.NET.Data;

/// <summary>Outcome of the DuckDB ICU probe (a check id, never data).</summary>
public enum DuckDbProbeResult
{
    Ok,
    ConversionMismatch,
    IcuNotStatic,
    SandboxChanged,
    Failed
}

/// <summary>
/// REQ-SQL-130 (BC-130-7): proves on an in-memory DuckDB connection that the time zone functions work under the production sandbox. The connection is
/// set up with <see cref="DuckDbOlapEngine.BuildSetupSql"/> itself (autoload and autoinstall off, no external access, configuration locked), not with a
/// copy of its statements. The ICU extension must be loaded and statically linked: an ICU that depends on autoload would pass on a developer machine and
/// fail on a locked-down node. Constants only; no user text reaches DuckDB.
/// </summary>
internal static class DuckDbIcuProbe
{
    /// <summary>The install mode DuckDB.NET.Data.Full reports for its built-in ICU (pinned from the T-1 run).</summary>
    internal const string StaticallyLinkedMode = "STATICALLY_LINKED";

    public static DuckDbProbeResult Run()
    {
        try
        {
            using (var sandbox = new DuckDBConnection("DataSource=:memory:"))
            {
                sandbox.Open();
                Execute(sandbox, DuckDbOlapEngine.BuildSetupSql(Path.GetTempPath().Replace('\\', '/'), "256MB", "256MB", 1));

                var converted = Scalar(sandbox, "SELECT timezone('Europe/Berlin', timezone('UTC', TIMESTAMP '2026-03-29 01:00:00'))");
                if (converted is not DateTime value || value != new DateTime(2026, 3, 29, 3, 0, 0))
                {
                    return DuckDbProbeResult.ConversionMismatch;
                }

                if (!SettingIsFalse(sandbox, "autoload_known_extensions") || !SettingIsFalse(sandbox, "autoinstall_known_extensions") || !SettingIsFalse(sandbox, "enable_external_access"))
                {
                    return DuckDbProbeResult.SandboxChanged;
                }
            }

            // duckdb_extensions() scans the extension directory, which the sandbox forbids; the load mode is read from a throwaway connection (constants only).
            using var plain = new DuckDBConnection("DataSource=:memory:");
            plain.Open();
            using var command = plain.CreateCommand();
            command.CommandText = "SELECT loaded, install_mode FROM duckdb_extensions() WHERE extension_name = 'icu'";
            using var reader = command.ExecuteReader();
            return reader.Read() && reader.GetBoolean(0) && string.Equals(reader.GetString(1), StaticallyLinkedMode, StringComparison.Ordinal)
                ? DuckDbProbeResult.Ok
                : DuckDbProbeResult.IcuNotStatic;
        }
        catch (Exception)
        {
            // Driver text never leaves this method (it may contain paths); any failure keeps DuckDB unavailable.
            return DuckDbProbeResult.Failed;
        }
    }

    private static void Execute(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static object? Scalar(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static bool SettingIsFalse(DuckDBConnection connection, string name) =>
        string.Equals(Scalar(connection, $"SELECT value FROM duckdb_settings() WHERE name = '{name}'")?.ToString(), "false", StringComparison.OrdinalIgnoreCase);
}
