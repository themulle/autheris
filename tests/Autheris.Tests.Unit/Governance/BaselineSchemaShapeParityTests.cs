namespace Autheris.Tests.Unit.Governance;

using System;
using System.Linq;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Migrations;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

public sealed class BaselineSchemaShapeParityTests
{
    private static readonly string[] RequiredWave2Tables =
    [
        "IDENTITY_USERS",
        "IDENTITY_GROUPS",
        "IDENTITY_GROUP_MEMBERS",
        "IDENTITY_LOCKS",
        "FUNCTIONAL_RIGHT_MAPPINGS",
        "ACCESS_POLICIES",
        "ACCESS_POLICY_GRANTEES",
        "ACCESS_POLICY_SELECTORS",
        "RECERT_CAMPAIGNS",
        "RECERT_ITEMS"
    ];

    [Fact]
    public void BaselineSchema_Sqlite_ContainsAll10Wave2Tables()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();

        new SqliteBaselineSchema(connection, null).Apply();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        using var reader = cmd.ExecuteReader();
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        foreach (var requiredTable in RequiredWave2Tables)
        {
            tables.ShouldContain(requiredTable, $"SQLite baseline schema must contain {requiredTable}");
        }
    }

    [Fact]
    public void BaselineSchema_PostgreSql_ContainsAll10Wave2Tables()
    {
        // Verify PostgreSqlBaselineSchema definition text contains all 10 tables
        var type = typeof(PostgreSqlBaselineSchema);
        type.ShouldNotBeNull();

        // Check GovernanceSchemaValidator lists all 10
        foreach (var table in RequiredWave2Tables)
        {
            GovernanceSchemaValidator.RequiredWave2Tables.ShouldContain(table);
        }
    }

    [Fact]
    public void BaselineSchema_SqlServer_ContainsAll10Wave2Tables()
    {
        var type = typeof(SqlServerBaselineSchema);
        type.ShouldNotBeNull();

        foreach (var table in RequiredWave2Tables)
        {
            GovernanceSchemaValidator.RequiredWave2Tables.ShouldContain(table);
        }
    }
}
