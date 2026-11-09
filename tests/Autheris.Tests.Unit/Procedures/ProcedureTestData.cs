namespace Autheris.Tests.Unit.Procedures;

using System;
using System.Collections.Generic;
using Autheris.Domain.Options;

/// <summary>Shared loader fixtures: the closed dialect admission (SEC-PROC-05) needs a SQL Server connection per data source name.</summary>
internal static class ProcedureTestData
{
    public static SqlDataSourceOptions SqlServerSources(params string[] names)
    {
        var connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in names.Length == 0 ? ["procedures"] : names)
        {
            connections[name] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=x;Database=y" };
        }

        return new SqlDataSourceOptions { Connections = connections };
    }
}
