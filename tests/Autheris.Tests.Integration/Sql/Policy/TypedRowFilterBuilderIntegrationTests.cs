namespace Autheris.Tests.Integration.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// PostgreSQL execution verification for <see cref="TypedRowFilterBuilder"/> and <see cref="PolicyPredicateRenderer"/>.
/// </summary>
public sealed class TypedRowFilterBuilderIntegrationTests
{
    private static readonly TableIdentity Orders = new("public", "orders");
    private readonly TypedRowFilterBuilder _builder = new();
    private readonly PolicyPredicateRenderer _renderer = new();

    private static ConsentRowFilter Filter(string column, string op, string valueJson, int group = 0) => new()
    {
        ColumnName = column,
        Operator = op,
        ValueJson = valueJson,
        FilterGroup = group
    };

    private static async Task<NpgsqlConnection> OpenAndSeedOrdersAsync(string connectionString)
    {
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE public.orders (id bigint PRIMARY KEY, region text, amount integer, secret boolean);
            INSERT INTO public.orders VALUES
                (1, 'EU', 150, false),
                (2, 'EU', 50, false),
                (3, 'APAC', 1, false),
                (4, 'US', 500, true),
                (5, 'US', 100, false),
                (6, 'DE', 999, false);
            """;
        await cmd.ExecuteNonQueryAsync();
        return connection;
    }

    private static async Task<List<long>> QueryAsync(NpgsqlConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    [RequiresContainerRuntimeFact]
    public async Task RenderedString_EqualsLegacyString_Semantically()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        var a1 = new Consent
        {
            TenantId = TestTenants.Default,
            Effect = ConsentEffect.Allow,
            RowFilters = [Filter("region", "IN", "[\"EU\",\"US\"]", 0), Filter("amount", "GTE", "100", 0), Filter("region", "EQ", "\"APAC\"", 1)]
        };
        var d1 = new Consent
        {
            TenantId = TestTenants.Default,
            Effect = ConsentEffect.Deny,
            RowFilters = [Filter("secret", "EQ", "true")]
        };
        var sut = new RowFilterSqlBuilder();

        var typed = _builder.BuildCombined([a1], [d1], Orders)!;
        var rendered = _renderer.Render(typed, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);
        var legacySql = sut.BuildCombinedRowFilter([a1], [d1], DatabaseDialect.PostgreSql)!;

        await using var connection = await OpenAndSeedOrdersAsync(db.ConnectionString);
        var viaTyped = await QueryAsync(connection, $"SELECT id FROM public.orders AS autheris_target WHERE {rendered.Sql} ORDER BY id", rendered.Parameters);
        var viaLegacy = await QueryAsync(connection, $"SELECT id FROM public.orders AS autheris_target WHERE {legacySql} ORDER BY id", new Dictionary<string, object?>());

        viaTyped.ShouldBe(viaLegacy);
        viaTyped.ShouldBe([1L, 3L, 5L]);
    }

    [RequiresContainerRuntimeTheory]
    [InlineData("it's \"quoted\"")]
    [InlineData("x' OR '1'='1")]
    [InlineData("'; DROP TABLE orders; --")]
    [InlineData("back\\slash %_ [x]")]
    [InlineData("line\nbreak")]
    [InlineData("ünï©ödé 日本語 😀")]
    public async Task HostileValue_RoundTripsExactly(string hostile)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        var json = JsonSerializer.Serialize(hostile);
        var predicate = _builder.Build([Filter("region", "EQ", json)], Orders);
        var rendered = _renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);

        rendered.Sql.ShouldNotContain(hostile.Length > 3 ? hostile[..4] : hostile);
        rendered.Parameters.Single().Value.ShouldBe(hostile);

        await using var connection = await OpenAndSeedOrdersAsync(db.ConnectionString);
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO public.orders (id, region, amount, secret) VALUES (99, @r, 1, false)";
            insert.Parameters.AddWithValue("r", hostile);
            await insert.ExecuteNonQueryAsync();
        }

        (await QueryAsync(connection, $"SELECT id FROM public.orders AS autheris_target WHERE {rendered.Sql}", rendered.Parameters)).ShouldBe([99L]);
    }
}
