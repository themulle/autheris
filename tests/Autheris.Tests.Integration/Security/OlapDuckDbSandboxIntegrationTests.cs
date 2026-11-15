namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.IO;
using Autheris.Application.Olap;
using DuckDB.NET.Data;
using Shouldly;
using Xunit;

/// <summary>
/// T3-06: verifies DuckDB sandbox setup disables extension autoloading and autoinstalling in-engine.
/// </summary>
[Trait("Category", "Security")]
public sealed class OlapDuckDbSandboxIntegrationTests
{
    [Fact]
    public void ExtensionAutoloadSettings_AreFalse_AfterTheSandboxSetup()
    {
        using var connection = new DuckDBConnection("DataSource=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = DuckDbOlapEngine.BuildSetupSql(Path.GetTempPath().Replace('\\', '/'), "256MB", "256MB", 1);
            setup.ExecuteNonQuery();
        }

        using var read = connection.CreateCommand();
        read.CommandText = "SELECT name, value FROM duckdb_settings() WHERE name IN ('autoload_known_extensions', 'autoinstall_known_extensions') ORDER BY name";
        using var reader = read.ExecuteReader();
        var values = new List<string>();
        while (reader.Read())
        {
            values.Add(reader.GetString(1));
        }

        values.Count.ShouldBe(2);
        values.ShouldAllBe(v => string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
    }
}
