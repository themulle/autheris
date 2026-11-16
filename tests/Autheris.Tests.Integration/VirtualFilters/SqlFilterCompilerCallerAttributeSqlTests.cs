namespace Autheris.Tests.Integration.VirtualFilters;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Identity.Attributes;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>PLAN-GOV-129 tests 20, 22 (BC-129-05, BC-129-06): PostgreSQL execution of caller placeholders in SQL virtual filters.</summary>
[Trait("Category", "Security")]
public sealed class SqlFilterCompilerCallerAttributeSqlTests
{
    private static readonly TenantId Tenant = CallerAttributeTestSupport.Tenant;
    private static readonly ISqlEngine Engine = new FastSqlEngine();
    private static readonly PolicyPredicateRenderer Renderer = new();

    private const string Scalar = "from conf.client client where target.client_id = client.client_id and client.dept = {{caller.dept}}";
    private const string InList = "from conf.client client where target.client_id = client.client_id and client.region in ({{caller.regions}})";
    private const string NotIn = "from conf.client client where target.client_id = client.client_id and client.region not in ({{caller.regions}})";

    private static readonly IReadOnlyList<TableMetadata> Catalog =
    [
        new TableMetadata
        {
            Identifier = new TableIdentifier("lwetem_prod", "conf", "client"),
            Table = new Table { SourceName = "lwetem_prod", SchemaName = "conf", TableName = "client", SourceType = DatabaseDialect.PostgreSql.ToString(), IsActive = true },
            Columns = [new TableColumn { ColumnName = "id" }]
        }
    ];

    private static CallerAttributeSnapshot Attributes() => new(1,
    [
        CallerAttributeTestSupport.Definition("dept", "claims", CallerAttributeType.Text, CallerAttributeCardinality.Scalar, 1, 60),
        CallerAttributeTestSupport.Definition("regions", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60)
    ], []);

    private static CallerAttributeSet Set(string name, params string[] values) => new(new Dictionary<string, CallerAttributeResult>
    {
        [name] = CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String))
    });

    private static PolicyPredicate Typed(string sql) =>
        SqlFilterCompiler.CompileTyped(
            SqlFilterCompiler.Validate(new VirtualFilter { TenantId = Tenant, Name = "caller_filter", Source = "lwetem_prod", Sql = sql }, Catalog, callerAttributes: Attributes()),
            DatabaseDialect.PostgreSql,
            Engine);

    private static async Task<List<long>> RunAsync(PolicyPredicate predicate)
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return [];

        var rendered = Renderer.Render(predicate, TargetSqlDialect.PostgreSql, RowFilterAliases.Target);
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE SCHEMA conf;
                CREATE TABLE conf.client (client_id BIGINT, region TEXT, dept TEXT, level INTEGER);
                CREATE TABLE public.fact (id BIGINT, client_id BIGINT);
                INSERT INTO conf.client VALUES (1, 'EU', 'D1', 1), (2, 'US', 'D2', 2), (3, 'APAC', 'D1', 3), (4, 'LATAM', 'D3', 4);
                INSERT INTO public.fact VALUES (10, 1), (20, 2), (30, 3), (40, 4);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT id FROM public.fact AS {RowFilterAliases.Target} WHERE {rendered.Sql} ORDER BY id";
        foreach (var (name, value) in rendered.Parameters)
        {
            command.Parameters.AddWithValue(name.TrimStart('@'), value ?? DBNull.Value);
        }

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    [RequiresContainerRuntimeFact]
    public async Task Executed_Scalar_FiltersByTheCallersValue() =>
        (await RunAsync(CallerAttributeBinder.Bind(Typed(Scalar), Set("dept", "D1"), CallerAttributePadding.Default))).ShouldBe([10L, 30L]);

    [RequiresContainerRuntimeFact]
    public async Task ST_129_Executed_In_MatchesExactlyTheListedRows_IncludingPaddedDuplicates() =>
        (await RunAsync(CallerAttributeBinder.Bind(Typed(InList), Set("regions", "EU", "US", "LATAM"), CallerAttributePadding.Default))).ShouldBe([10L, 20L, 40L]);

    [RequiresContainerRuntimeFact]
    public async Task ST_129_Executed_NotIn_ExcludesExactlyTheListedRows_PaddingIsNeutral() =>
        (await RunAsync(CallerAttributeBinder.Bind(Typed(NotIn), Set("regions", "EU", "US", "LATAM"), CallerAttributePadding.Default))).ShouldBe([30L]);

    [RequiresContainerRuntimeFact]
    public async Task ST_129_Executed_NotIn_WithAMissingAttribute_MatchesNoRow_NeverEveryRow() =>
        (await RunAsync(CallerAttributeBinder.Bind(Typed(NotIn), CallerAttributeSet.Empty, CallerAttributePadding.Default))).ShouldBeEmpty();

    [RequiresContainerRuntimeTheory]
    [InlineData(Scalar)]
    [InlineData(InList)]
    [InlineData(NotIn)]
    public async Task ST_129_22_AnUnboundSlot_MatchesNoRows_ByTheNullSemantics(string sql) =>
        (await RunAsync(Typed(sql))).ShouldBeEmpty();
}
