namespace Autheris.Tests.Integration.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;

/// <summary>PostgreSQL execution of typed <see cref="PolicyPredicate"/> virtual filters with bound parameters.</summary>
public sealed class TypedVirtualFilterIntegrationTests
{
    private static readonly PolicyPredicateRenderer Renderer = new();
    private static readonly ISqlEngine Engine = new FastSqlEngine();

    private static TableMetadata Catalogued(string schema, string table, params string[] columns) => new()
    {
        Identifier = new TableIdentifier("lwetem_prod", schema, table),
        Table = new Table { SourceName = "lwetem_prod", SchemaName = schema, TableName = table, SourceType = DatabaseDialect.PostgreSql.ToString(), IsActive = true },
        Columns = (columns.Length == 0 ? ["id"] : columns).Select(c => new TableColumn { ColumnName = c }).ToList()
    };

    private static VirtualFilter SqlFilter(string sql) => new()
    {
        TenantId = new TenantId("tenant_lwe"),
        Name = "typed_sql_filter",
        Source = "lwetem_prod",
        Sql = sql
    };

    [RequiresContainerRuntimeFact]
    public async Task CompileTyped_ExecutedOnPostgreSql_FiltersWithTheBoundParameter()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        var catalog = new List<TableMetadata> { Catalogued("conf", "client"), Catalogued("md", "crane") };
        var filter = SqlFilterCompiler.Validate(
            SqlFilter("from conf.client client where target.client_id = client.client_id and client.region = 'EU' and client.level >= 2"),
            catalog);
        var rendered = Renderer.Render(
            SqlFilterCompiler.CompileTyped(filter, DatabaseDialect.PostgreSql, Engine),
            PolicyParameterNamer.ToTargetDialect(DatabaseDialect.PostgreSql),
            RowFilterAliases.Target);

        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync();
        await using (var setup = conn.CreateCommand())
        {
            setup.CommandText = """
                CREATE SCHEMA conf;
                CREATE TABLE conf.client (client_id BIGINT, region TEXT, level INTEGER);
                CREATE TABLE public.fact (id BIGINT, client_id BIGINT);
                INSERT INTO conf.client VALUES (1, 'EU', 3), (2, 'US', 3), (3, 'EU', 1);
                INSERT INTO public.fact VALUES (10, 1), (20, 2), (30, 3);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = conn.CreateCommand();
        command.CommandText = $"SELECT id FROM public.fact AS autheris_target WHERE {rendered.Sql} ORDER BY id";
        foreach (var (name, value) in rendered.Parameters)
        {
            command.Parameters.AddWithValue(name.TrimStart('@'), value ?? DBNull.Value);
        }

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        ids.ShouldBe([10L]);
    }
}
