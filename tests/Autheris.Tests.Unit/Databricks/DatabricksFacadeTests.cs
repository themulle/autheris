namespace Autheris.Tests.Unit.Databricks;

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Databricks;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;
using static Autheris.Tests.Unit.Databricks.DatabricksTestSupport;

/// <summary>WP-X0j: the ADO.NET facade over the Statement Execution client, the factory branch and the Databricks catalog reader (HTTP fake, no workspace).</summary>
public sealed class DatabricksFacadeTests
{
    private static JsonElement Body(RecordedRequest r) => JsonDocument.Parse(r.Body).RootElement;

    private static (DatabricksDbConnection Connection, FakeDatabricksHandler Handler) Facade(Func<RecordedRequest, HttpResponseMessage?> routes)
    {
        var handler = new FakeDatabricksHandler(Router(routes));
        var options = Options();
        var connection = new DatabricksDbConnection(Client(handler, options), options.DataSources.Connections["dbx"].Databricks!, "dbx");
        return (connection, handler);
    }

    private static CompiledSql Compiled(string sql, params BoundParameter[] parameters) =>
        new(sql, [.. parameters], TargetSqlDialect.Databricks, SqlStatementClass.Select, [], "test");

    private static BoundParameter Bound(int ordinal, object? value, SqlParameterType type, ParameterOrigin origin = ParameterOrigin.QueryLiteral) =>
        new($":p{ordinal}", $"p{ordinal}", ordinal, value, type, origin);

    private static bool IsStatementPost(RecordedRequest r) => r.Method == HttpMethod.Post && r.Uri.AbsolutePath == "/api/2.0/sql/statements/";

    [Fact]
    public async Task Factory_Databricks_ReturnsFacade()
    {
        var options = Options();
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<IKeyVaultSecretProvider>(Vault());
        services.AddSingleton<IHostEnvironment>(Env("Production"));
        services.AddDatabricksStatementExecution();
        using var provider = services.BuildServiceProvider();
        var factory = new SqlConnectionFactory(Env("Production"), Vault(), provider);

        await using var connection = await factory.CreateOpenConnectionAsync(options.DataSources.Connections["dbx"]);

        connection.ShouldBeOfType<Autheris.Infrastructure.Persistence.MetadataGuardedConnection>()
            .Inner.ShouldBeOfType<DatabricksDbConnection>();
        connection.State.ShouldBe(ConnectionState.Open);
    }

    [Fact]
    public async Task Factory_Databricks_WithoutRestClient_FailsClosed()
    {
        var factory = new SqlConnectionFactory(Env("Production"), Vault());

        await Should.ThrowAsync<NotSupportedException>(() => factory.CreateOpenConnectionAsync(Options().DataSources.Connections["dbx"]));
    }

    [Fact]
    public async Task Factory_Databricks_WithoutDatabricksSection_FailsClosed()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(Options()));
        services.AddDatabricksStatementExecution();
        using var provider = services.BuildServiceProvider();
        var factory = new SqlConnectionFactory(Env("Production"), Vault(), provider);

        await Should.ThrowAsync<ArgumentException>(() => factory.CreateOpenConnectionAsync(new DataSourceConnectionOptions { Provider = "databricks" }));
    }

    [Fact]
    public async Task Facade_BeginTransaction_Throws()
    {
        var (connection, handler) = Facade(_ => null);
        await connection.OpenAsync();

        Should.Throw<NotSupportedException>(() => connection.BeginTransaction());
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task DatabricksBinder_OnFacade_SendsNamedParameters()
    {
        const string evil = "x'; DROP TABLE t;--";
        var (connection, handler) = Facade(r => IsStatementPost(r) ? Ok(Succeeded(StatementId, """[["1","a"]]""")) : null);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var compiled = Compiled(
            "SELECT id, name FROM t WHERE a = :p1 AND b = :p2 AND c = :p3 AND d = :p4",
            Bound(1, evil, SqlParameterType.String, ParameterOrigin.Tenant),
            Bound(2, 7, SqlParameterType.Int32),
            Bound(3, 9_000_000_000L, SqlParameterType.Int64),
            Bound(4, null, SqlParameterType.Null));

        CompiledSqlBinders.For(TargetSqlDialect.Databricks).Bind(command, compiled, new Dictionary<string, object?>());
        await using var reader = await command.ExecuteReaderAsync();

        var post = handler.Requests.Single(IsStatementPost);
        var body = Body(post);
        body.GetProperty("statement").GetString().ShouldBe(compiled.Sql);
        body.GetProperty("statement").GetString()!.ShouldNotContain(evil);
        var parameters = body.GetProperty("parameters");
        parameters.GetArrayLength().ShouldBe(4);
        parameters[0].GetProperty("name").GetString().ShouldBe("p1");
        parameters[0].GetProperty("value").GetString().ShouldBe(evil);
        parameters[0].GetProperty("type").GetString().ShouldBe("STRING");
        parameters[1].GetProperty("value").GetString().ShouldBe("7");
        parameters[1].GetProperty("type").GetString().ShouldBe("INT");
        parameters[2].GetProperty("value").GetString().ShouldBe("9000000000");
        parameters[2].GetProperty("type").GetString().ShouldBe("BIGINT");
        parameters[3].GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Null);
        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetFieldType(0).ShouldBe(typeof(int));
        reader.GetInt32(0).ShouldBe(1);
        reader.GetFieldType(1).ShouldBe(typeof(string));
        reader.GetString(1).ShouldBe("a");
        (await reader.ReadAsync()).ShouldBeFalse();
    }

    [Fact]
    public async Task Facade_MapsScalarTypes_ToTypedRestParameters()
    {
        var (connection, handler) = Facade(r => IsStatementPost(r) ? Ok(Succeeded(StatementId, "[]")) : null);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        var compiled = Compiled(
            "SELECT 1 WHERE :p1 AND :p2 AND :p3 AND :p4 AND :p5",
            Bound(1, true, SqlParameterType.Boolean),
            Bound(2, 12.50m, SqlParameterType.Decimal),
            Bound(3, 1.5d, SqlParameterType.Double),
            Bound(4, new DateOnly(2026, 10, 11), SqlParameterType.Date),
            Bound(5, new DateTime(2026, 10, 11, 12, 30, 45, 123, DateTimeKind.Utc), SqlParameterType.Timestamp));

        CompiledSqlBinders.For(TargetSqlDialect.Databricks).Bind(command, compiled, new Dictionary<string, object?>());
        await using var reader = await command.ExecuteReaderAsync();

        var parameters = Body(handler.Requests.Single(IsStatementPost)).GetProperty("parameters");
        string Pair(int i) => parameters[i].GetProperty("type").GetString() + "|" + parameters[i].GetProperty("value").GetString();
        Pair(0).ShouldBe("BOOLEAN|true");
        Pair(1).ShouldBe("DECIMAL(4,2)|12.50");
        Pair(2).ShouldBe("DOUBLE|1.5");
        Pair(3).ShouldBe("DATE|2026-10-11");
        Pair(4).ShouldBe("TIMESTAMP|2026-10-11 12:30:45.123000");
    }

    [Fact]
    public async Task Facade_UnsupportedParameterType_IsRejectedBeforeAnyRequest()
    {
        var (connection, handler) = Facade(_ => null);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        CompiledSqlBinders.For(TargetSqlDialect.Databricks).Bind(
            command, Compiled("SELECT :p1", Bound(1, new byte[] { 1, 2 }, SqlParameterType.Binary)), new Dictionary<string, object?>());

        await Should.ThrowAsync<NotSupportedException>(() => command.ExecuteReaderAsync());
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Facade_ExecuteNonQuery_ReturnsReportedAffectedRows()
    {
        var json = "{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"SUCCEEDED\"}," +
                   "\"manifest\":{\"format\":\"JSON_ARRAY\",\"truncated\":false,\"schema\":{\"column_count\":1,\"columns\":[{\"name\":\"num_affected_rows\",\"type_name\":\"LONG\",\"position\":0}]}}," +
                   "\"result\":{\"chunk_index\":0,\"row_offset\":0,\"data_array\":[[\"3\"]]}}";
        var (connection, _) = Facade(r => IsStatementPost(r) ? Ok(json) : null);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE t SET a = 1";

        (await command.ExecuteNonQueryAsync()).ShouldBe(3);
    }

    [Fact]
    public async Task Facade_ProviderFailure_SurfacesTheSanitisedException()
    {
        var (connection, _) = Facade(r => IsStatementPost(r) ? Json(HttpStatusCode.InternalServerError, "{\"message\":\"secret-detail\"}") : null);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1";

        var ex = await Should.ThrowAsync<DatabricksProviderException>(() => command.ExecuteReaderAsync());
        ex.Message.ShouldNotContain("secret-detail");
    }

    [Fact]
    public async Task Reader_ReportsTypedFieldTypes()
    {
        var manifestColumns = string.Join(",", new[]
        {
            "{\"name\":\"flag\",\"type_name\":\"BOOLEAN\",\"position\":0}",
            "{\"name\":\"tiny\",\"type_name\":\"BYTE\",\"position\":1}",
            "{\"name\":\"small\",\"type_name\":\"SHORT\",\"position\":2}",
            "{\"name\":\"num\",\"type_name\":\"INT\",\"position\":3}",
            "{\"name\":\"big\",\"type_name\":\"LONG\",\"position\":4}",
            "{\"name\":\"flt\",\"type_name\":\"FLOAT\",\"position\":5}",
            "{\"name\":\"dbl\",\"type_name\":\"DOUBLE\",\"position\":6}",
            "{\"name\":\"dec18\",\"type_name\":\"DECIMAL\",\"type_precision\":18,\"type_scale\":2,\"position\":7}",
            "{\"name\":\"dec38\",\"type_name\":\"DECIMAL\",\"type_precision\":38,\"type_scale\":6,\"position\":8}",
            "{\"name\":\"d\",\"type_name\":\"DATE\",\"position\":9}",
            "{\"name\":\"ts\",\"type_name\":\"TIMESTAMP\",\"position\":10}",
            "{\"name\":\"ntz\",\"type_name\":\"TIMESTAMP_NTZ\",\"position\":11}",
            "{\"name\":\"txt\",\"type_name\":\"STRING\",\"position\":12}"
        });
        var rowsJson = "[" +
                       "[\"true\",\"12\",\"345\",\"42\",\"9000000000\",\"1.5\",\"2.25\",\"123.45\",\"123456789012345678901234567890.123456\",\"2026-10-11\",\"2026-10-11T12:30:45.123Z\",\"2026-10-11T12:30:45.123\",\"ok\"]," +
                       "[null,null,null,null,null,null,null,null,null,null,null,null,null]" +
                       "]";
        var json = "{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"SUCCEEDED\"}," +
                   "\"manifest\":{\"format\":\"JSON_ARRAY\",\"truncated\":false,\"schema\":{\"column_count\":13,\"columns\":[" + manifestColumns + "]}}," +
                   "\"result\":{\"chunk_index\":0,\"row_offset\":0,\"data_array\":" + rowsJson + "}}";
        var (connection, _) = Facade(r => IsStatementPost(r) ? Ok(json) : null);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM typed_table";

        await using var reader = await command.ExecuteReaderAsync();

        reader.GetFieldType(0).ShouldBe(typeof(bool));
        reader.GetFieldType(1).ShouldBe(typeof(sbyte));
        reader.GetFieldType(2).ShouldBe(typeof(short));
        reader.GetFieldType(3).ShouldBe(typeof(int));
        reader.GetFieldType(4).ShouldBe(typeof(long));
        reader.GetFieldType(5).ShouldBe(typeof(float));
        reader.GetFieldType(6).ShouldBe(typeof(double));
        reader.GetFieldType(7).ShouldBe(typeof(decimal));
        reader.GetFieldType(8).ShouldBe(typeof(string));
        reader.GetFieldType(9).ShouldBe(typeof(DateTime));
        reader.GetFieldType(10).ShouldBe(typeof(DateTimeOffset));
        reader.GetFieldType(11).ShouldBe(typeof(DateTime));
        reader.GetFieldType(12).ShouldBe(typeof(string));

        var described = SqlResultColumns.Describe(reader);
        TrinoColumnTypes.Map(described[7], []).Name.ShouldBe("decimal(18,2)");
        TrinoColumnTypes.Map(described[9], []).Name.ShouldBe("date");
        TrinoColumnTypes.Map(described[10], []).Name.ShouldBe("timestamp(3) with time zone");
        TrinoColumnTypes.Map(described[11], []).Name.ShouldBe("timestamp(3)");

        (await reader.ReadAsync()).ShouldBeTrue();
        reader.GetBoolean(0).ShouldBeTrue();
        reader.GetValue(1).ShouldBe((sbyte)12);
        reader.GetInt16(2).ShouldBe((short)345);
        reader.GetInt32(3).ShouldBe(42);
        reader.GetInt64(4).ShouldBe(9_000_000_000L);
        reader.GetFloat(5).ShouldBe(1.5f);
        reader.GetDouble(6).ShouldBe(2.25d);
        reader.GetDecimal(7).ShouldBe(123.45m);
        reader.GetString(8).ShouldBe("123456789012345678901234567890.123456");
        reader.GetDateTime(9).ShouldBe(new DateTime(2026, 10, 11, 0, 0, 0, DateTimeKind.Unspecified));
        ((DateTimeOffset)reader.GetValue(10)).ShouldBe(new DateTimeOffset(2026, 10, 11, 12, 30, 45, 123, TimeSpan.Zero));
        reader.GetDateTime(11).ShouldBe(new DateTime(2026, 10, 11, 12, 30, 45, 123, DateTimeKind.Unspecified));
        reader.GetString(12).ShouldBe("ok");

        (await reader.ReadAsync()).ShouldBeTrue();
        for (var i = 0; i < 13; i++)
        {
            reader.IsDBNull(i).ShouldBeTrue();
            reader.GetValue(i).ShouldBe(DBNull.Value);
        }

        (await reader.ReadAsync()).ShouldBeFalse();

        // ExecuteScalar returns typed scalar too
        var scalarJson = "{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"SUCCEEDED\"}," +
                         "\"manifest\":{\"format\":\"JSON_ARRAY\",\"truncated\":false,\"schema\":{\"column_count\":1,\"columns\":[{\"name\":\"total\",\"type_name\":\"DECIMAL\",\"type_precision\":18,\"type_scale\":2,\"position\":0}]}}," +
                         "\"result\":{\"chunk_index\":0,\"row_offset\":0,\"data_array\":[[\"123.45\"]]}}";
        var (scalarConn, _) = Facade(r => IsStatementPost(r) ? Ok(scalarJson) : null);
        await scalarConn.OpenAsync();
        await using var scalarCmd = scalarConn.CreateCommand();
        scalarCmd.CommandText = "SELECT SUM(amount) FROM t";
        var scalar = await scalarCmd.ExecuteScalarAsync();
        scalar.ShouldBeOfType<decimal>().ShouldBe(123.45m);
    }
}
