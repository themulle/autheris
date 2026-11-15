namespace Autheris.Tests.Integration.Masking;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Masking;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// PostgreSQL execution of the <see cref="MaskSemanticsCorpus"/> via <see cref="MaskSpecRenderer"/>.
/// </summary>
[Trait("Category", "Security")]
public sealed class MaskSemanticsCorpusSqlTests
{
    public static IEnumerable<object[]> Ids() => MaskSemanticsCorpus.Cases.Select(c => new object[] { c.Id });

    public static IEnumerable<object[]> NullRuleIndexes() => Enumerable.Range(0, MaskSemanticsCorpus.NullRules.Count).Select(i => new object[] { i });

    [RequiresContainerRuntimeTheory]
    [MemberData(nameof(Ids))]
    public async Task SqlEngine_ProducesTheCorpusOutput(string id)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var c = MaskSemanticsCorpus.Get(id);

        var actual = await RunSqlAsync(db.ConnectionString, c.Rule, c.Clear, c.DataType);
        Normalize(actual).ShouldBe(Normalize(c.Expected));
    }

    [RequiresContainerRuntimeTheory]
    [MemberData(nameof(NullRuleIndexes))]
    public async Task NullInput_StaysNull_InSqlEngine(int index)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;
        var rule = MaskSemanticsCorpus.NullRules[index];

        var sql = (await RunSqlAsync(db.ConnectionString, rule, null, rule.RuleType == "GEO_JITTER" ? "real" : "text"))?.ToString();
        (sql is null || sql.All(ch => ch is '*' or '@')).ShouldBeTrue($"SQL mask of NULL revealed '{sql}'");
    }

    private static async Task<object?> RunSqlAsync(string connectionString, MaskingRule rule, object? clear, string dataType)
    {
        var sqlDataType = dataType.Equals("datetime", StringComparison.OrdinalIgnoreCase) ? "timestamp" : dataType;
        var spec = SqlDataMaskingProvider.BuildUnkeyedMaskSpec(rule, "v", DatabaseDialect.PostgreSql);
        var rendered = new MaskSpecRenderer().Render(spec, "v", sqlDataType, TargetSqlDialect.PostgreSql, name => "@" + name);

        var pgCast = dataType.ToLowerInvariant() switch
        {
            "integer" => "bigint",
            "real" => "double precision",
            "datetime" => "timestamp",
            _ => "text"
        };

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {rendered.Sql} AS m FROM (SELECT CAST(@clear_value AS {pgCast}) AS \"v\") AS t";
        command.Parameters.AddWithValue("clear_value", clear switch
        {
            null => DBNull.Value,
            DateTime d => DateTime.SpecifyKind(d, DateTimeKind.Unspecified),
            _ => clear
        });
        foreach (var (name, value) in rendered.Parameters)
        {
            command.Parameters.AddWithValue(name.TrimStart('@', ':'), value ?? DBNull.Value);
        }

        var scalar = await command.ExecuteScalarAsync();
        return scalar is DBNull ? null : scalar;
    }

    private static object? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        double d => Math.Round(d, 6).ToString("R", CultureInfo.InvariantCulture),
        float f => Math.Round(f, 6).ToString("R", CultureInfo.InvariantCulture),
        decimal m => Math.Round((double)m, 6).ToString("R", CultureInfo.InvariantCulture),
        _ => value
    };
}
