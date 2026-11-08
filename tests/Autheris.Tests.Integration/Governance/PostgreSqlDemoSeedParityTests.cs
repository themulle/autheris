namespace Autheris.Tests.Integration.Governance;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Governance;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Npgsql;
using Shouldly;
using Xunit;

/// <summary>
/// WP-SIMPL-11: the PostgreSQL demo seed carries the same content as the former SQLite demo seed (finance_items child table and
/// relation, REGEX email mask, REDACT note mask), is idempotent on a second start, and the catalog returns columns in registration
/// order (the natural-key unique index would otherwise return them alphabetically).
/// </summary>
[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlDemoSeedParityTests(GovernancePostgresFixture postgres)
{
    private static Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository CreateSeeded(string connectionString)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = connectionString, SeedDemoData = true }
        });
        return GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), options, env);
    }

    private static async Task<long> CountAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task DemoSeed_MatchesTheFormerSqliteSeed_AndSurvivesASecondStart()
    {
        var connectionString = await postgres.CreateDatabaseAsync("demo_seed");

        await using (CreateSeeded(connectionString))
        {
        }

        // A second gateway start on the same database must keep the seed instead of failing on the natural-key indexes.
        await using (CreateSeeded(connectionString))
        {
        }

        (await CountAsync(connectionString, "SELECT COUNT(*) FROM TABLES WHERE table_name = 'finance_items'")).ShouldBe(1);
        (await CountAsync(connectionString, "SELECT COUNT(*) FROM TABLES")).ShouldBe(101);
        (await CountAsync(connectionString, "SELECT COUNT(*) FROM TABLE_RELATIONS")).ShouldBe(1);
        (await CountAsync(connectionString, "SELECT COUNT(*) FROM TABLE_RELATION_KEYS")).ShouldBe(1);
        (await CountAsync(connectionString,
            "SELECT COUNT(*) FROM COLUMN_MASKING_RULES m JOIN TABLE_COLUMNS c ON c.id = m.table_column_id WHERE c.column_name = 'email' AND m.rule_type <> 'REGEX'"))
            .ShouldBe(0);
        (await CountAsync(connectionString,
            "SELECT COUNT(*) FROM COLUMN_MASKING_RULES m JOIN TABLE_COLUMNS c ON c.id = m.table_column_id WHERE c.column_name = 'sensitive_note' AND m.rule_type = 'REDACT'"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task Catalog_ReturnsColumnsInRegistrationOrder_NotAlphabetically()
    {
        var connectionString = await postgres.CreateDatabaseAsync("demo_seed_order");
        await using var repository = CreateSeeded(connectionString);

        var metadata = await repository.GetTableMetadataAsync(new TableIdentifier("finance", "dbo", "finance_table_1"));

        metadata.ShouldNotBeNull();
        metadata.Columns.Select(c => c.ColumnName).ToArray().ShouldBe(["id", "name", "amount", "email", "created_at"]);
    }
}
