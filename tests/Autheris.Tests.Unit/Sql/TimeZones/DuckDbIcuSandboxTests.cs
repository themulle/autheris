namespace Autheris.Tests.Unit.Sql.TimeZones;

using Autheris.Application.Olap;
using DuckDB.NET.Data;
using Shouldly;
using Xunit;

/// <summary>
/// T-1 / BC-130-7(c) / ST-130-15: the statically linked ICU of DuckDB.NET.Data.Full stays callable under the exact production sandbox
/// (autoload off, external access off, configuration locked), and the sandbox itself is unchanged by the probe.
/// </summary>
[Trait("Category", "Security")]
public sealed class DuckDbIcuSandboxTests
{
    private static DuckDBConnection OpenSandbox()
    {
        var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var setup = connection.CreateCommand();
        setup.CommandText = DuckDbOlapEngine.BuildSetupSql(System.IO.Path.GetTempPath().Replace('\\', '/'), "256MB", "256MB", 1);
        setup.ExecuteNonQuery();
        return connection;
    }

    private static object? Scalar(DuckDBConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    [Fact]
    public void T1_TimezoneConversionWorksUnderTheProductionSandbox()
    {
        using var connection = OpenSandbox();
        var result = Scalar(connection, "SELECT timezone('Europe/Berlin', timezone('UTC', TIMESTAMP '2026-03-29 01:00:00'))");
        result.ShouldBeOfType<DateTime>().ShouldBe(new DateTime(2026, 3, 29, 3, 0, 0));
        Convert.ToInt64(Scalar(connection, "SELECT count(*) FROM pg_timezone_names()")).ShouldBeGreaterThan(400);
    }

    [Fact]
    public void T1_IcuIsLoadedAndStaticallyLinked()
    {
        // duckdb_extensions() scans the extension directory, which the sandbox (enable_external_access=false) forbids: the load mode is read from
        // a throwaway connection without the sandbox (constants only), the sandbox itself proves callability in the test above.
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT loaded, install_mode FROM duckdb_extensions() WHERE extension_name = 'icu'";
        using var reader = command.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetBoolean(0).ShouldBeTrue();
        reader.GetString(1).ShouldBe("STATICALLY_LINKED");
    }

    [Fact]
    public void T1_SandboxSettingsStayLockedAndExtensionsCannotBeInstalled()
    {
        using var connection = OpenSandbox();
        Scalar(connection, "SELECT timezone('Europe/Berlin', TIMESTAMPTZ '2026-03-29 01:00:00+00')");

        Scalar(connection, "SELECT value FROM duckdb_settings() WHERE name = 'autoload_known_extensions'")!.ToString()!.ShouldBe("false", StringCompareShould.IgnoreCase);
        Scalar(connection, "SELECT value FROM duckdb_settings() WHERE name = 'autoinstall_known_extensions'")!.ToString()!.ShouldBe("false", StringCompareShould.IgnoreCase);
        Scalar(connection, "SELECT value FROM duckdb_settings() WHERE name = 'enable_external_access'")!.ToString()!.ShouldBe("false", StringCompareShould.IgnoreCase);

        Should.Throw<DuckDBException>(() => Scalar(connection, "INSTALL icu"));
        // Observed (T-1): LOAD icu is a no-op because the extension is statically linked and already loaded; any other extension cannot be loaded.
        Should.Throw<DuckDBException>(() => Scalar(connection, "LOAD httpfs"));
    }
}
