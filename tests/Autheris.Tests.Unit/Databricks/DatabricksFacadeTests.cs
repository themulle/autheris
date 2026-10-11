namespace Autheris.Tests.Unit.Databricks;

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Net;
using System.Text.Json;
using Autheris.Application.Interfaces;
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

        connection.ShouldBeOfType<DatabricksDbConnection>();
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
        reader.GetString(0).ShouldBe("1");
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
    public async Task DatabricksCatalog_ReadsFullDataTypeAndCollation()
    {
        var json = "{\"statement_id\":\"" + StatementId + "\",\"status\":{\"state\":\"SUCCEEDED\"}," +
                   "\"manifest\":{\"format\":\"JSON_ARRAY\",\"truncated\":false,\"schema\":{\"column_count\":3,\"columns\":[" +
                   "{\"name\":\"column_name\",\"type_name\":\"STRING\",\"position\":0},{\"name\":\"full_data_type\",\"type_name\":\"STRING\",\"position\":1},{\"name\":\"collation_name\",\"type_name\":\"STRING\",\"position\":2}]}}," +
                   "\"result\":{\"chunk_index\":0,\"row_offset\":0,\"data_array\":[[\"id\",\"int\",null],[\"tenantid\",\"string\",\"UTF8_BINARY\"],[\"amount\",\"decimal(10,2)\",null]]}}";
        var (connection, handler) = Facade(r => IsStatementPost(r) ? Ok(json) : null);
        await connection.OpenAsync();
        var reader = new DatabricksColumnMetadataReader();

        var columns = new List<DbColumnMetadata>();
        await foreach (var column in reader.ReadAsync(connection, new TableIdentity("Sales", "Orders"), CancellationToken.None))
        {
            columns.Add(column);
        }

        reader.Dialect.ShouldBe(TargetSqlDialect.Databricks);
        columns.ShouldBe([
            new DbColumnMetadata("id", "int", null),
            new DbColumnMetadata("tenantid", "string", "UTF8_BINARY"),
            new DbColumnMetadata("amount", "decimal(10,2)", null)]);
        var body = Body(handler.Requests.Single(IsStatementPost));
        var statement = body.GetProperty("statement").GetString()!;
        statement.ShouldContain("information_schema.columns");
        statement.ShouldContain("full_data_type");
        statement.ShouldContain("collation_name");
        statement.ShouldNotContain("Sales");
        statement.ShouldNotContain("Orders");
        var values = body.GetProperty("parameters").EnumerateArray().Select(p => p.GetProperty("value").GetString()).ToArray();
        values.ShouldBe(["Sales", "Orders"]);
    }

    [Fact]
    public async Task DatabricksCatalog_UnknownTable_YieldsNoColumns()
    {
        var (connection, _) = Facade(r => IsStatementPost(r) ? Ok(Succeeded(StatementId, "[]")) : null);
        await connection.OpenAsync();

        var columns = new List<DbColumnMetadata>();
        await foreach (var column in new DatabricksColumnMetadataReader().ReadAsync(connection, new TableIdentity("s", "t"), CancellationToken.None))
        {
            columns.Add(column);
        }

        columns.ShouldBeEmpty();
    }
}
