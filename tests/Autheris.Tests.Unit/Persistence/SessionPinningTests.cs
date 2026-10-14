namespace Autheris.Tests.Unit.Persistence;

using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-X0b (SEC-X1-08, SEC-X1-14): default-schema validation and the fail-closed rules of the session pin.</summary>
public sealed class SessionPinningTests
{
    private const string PgConnection = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=p;Timeout=1";
    private const string OraConnection = "User Id=app;Password=x;Data Source=127.0.0.1:1/X";

    private static IHostEnvironment Dev()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        return env;
    }

    private static GatewayOptions Gateway(DataSourceConnectionOptions source, bool webSql = true) => new()
    {
        WebSql = new WebSqlOptions { Enabled = webSql, DefaultDataSourceName = "main" },
        DataSources = new SqlDataSourceOptions
        {
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase) { ["main"] = source }
        }
    };

    [Theory]
    [InlineData("app\"; DROP SCHEMA x; --")]
    [InlineData("app;")]
    [InlineData("a\"b")]
    [InlineData("a b")]
    [InlineData("a'b")]
    [InlineData("app\n")]
    [InlineData("1app")]
    [InlineData("")]
    [InlineData("a.b")]
    public void DefaultSchema_WithQuoteOrSemicolon_RejectedAtStartup(string schema)
    {
        var options = Gateway(new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = PgConnection, DefaultSchema = schema });

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDefaultSchemas(options));
        DefaultSchemaPolicy.IsValid(schema).ShouldBeFalse();
    }

    [Fact]
    public void DefaultSchema_TooLong_Rejected()
    {
        DefaultSchemaPolicy.IsValid(new string('a', 129)).ShouldBeFalse();
        DefaultSchemaPolicy.IsValid(new string('a', 128)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("public")]
    [InlineData("_app")]
    [InlineData("App$Data#1")]
    public void DefaultSchema_ValidIdentifier_Accepted(string schema)
    {
        var options = Gateway(new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = PgConnection, DefaultSchema = schema });

        Should.NotThrow(() => GatewayStartupValidator.ValidateDefaultSchemas(options));
    }

    [Theory]
    [InlineData("PostgreSql", PgConnection)]
    [InlineData("Oracle", OraConnection)]
    public void Startup_PostgreSqlOrOracleWebSqlSourceWithoutDefaultSchema_Fails(string provider, string connection)
    {
        var options = Gateway(new DataSourceConnectionOptions { Provider = provider, ConnectionString = connection });

        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateDefaultSchemas(options));
    }

    [Fact]
    public void Startup_SourcesNotReachableByWebSql_OrOtherProviders_NeedNoDefaultSchema()
    {
        Should.NotThrow(() => GatewayStartupValidator.ValidateDefaultSchemas(
            Gateway(new DataSourceConnectionOptions { Provider = "PostgreSql", ConnectionString = PgConnection }, webSql: false)));
        Should.NotThrow(() => GatewayStartupValidator.ValidateDefaultSchemas(
            Gateway(new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=x;Database=y" })));
        Should.NotThrow(() => GatewayStartupValidator.ValidateDefaultSchemas(
            Gateway(new DataSourceConnectionOptions { Provider = "Sqlite", ConnectionString = "Data Source=:memory:" })));
    }

    [Theory]
    [InlineData("PostgreSql", PgConnection, null)]
    [InlineData("Oracle", OraConnection, null)]
    [InlineData("PostgreSql", PgConnection, "bad\"schema")]
    [InlineData("Oracle", OraConnection, "bad;schema")]
    public async Task MissingDefaultSchemaAtRequestTime_FailsClosed(string provider, string connection, string? schema)
    {
        var factory = new SqlConnectionFactory(Dev());
        var options = new DataSourceConnectionOptions { Provider = provider, ConnectionString = connection, DefaultSchema = schema, RequireDefaultSchema = true };

        var ex = await Should.ThrowAsync<SqlSourceUnavailableException>(() => factory.CreateOpenConnectionAsync(options));

        ex.Message.ShouldNotContain("schema\"");
        SqlSourceUnavailableException.ErrorCode.ShouldBe("SQL_SOURCE_UNAVAILABLE");
    }

    [Fact]
    public async Task Oracle_InvalidSchemaIdentifier_Rejected()
    {
        var factory = new SqlConnectionFactory(Dev());
        var options = new DataSourceConnectionOptions { Provider = "Oracle", ConnectionString = OraConnection, DefaultSchema = "A\" OR 1=1 --" };

        await Should.ThrowAsync<SqlSourceUnavailableException>(() => factory.CreateOpenConnectionAsync(options));
    }

    [Fact]
    public void PostgreSql_SearchPathMismatch_FailsClosed()
    {
        Should.Throw<SqlSourceUnavailableException>(() => SessionPinning.EnsurePinned(false, "search_path"));
        Should.Throw<SqlSourceUnavailableException>(() => SessionPinning.EnsurePinned(null, "search_path"));
        Should.NotThrow(() => SessionPinning.EnsurePinned(true, "search_path"));
    }

    [Fact]
    public void PostgreSql_SearchPath_SchemaIsBound_NotConcatenated()
    {
        SessionPinning.PostgreSqlSearchPathPinSql.ShouldContain("format('pg_catalog, %I, pg_temp', @schema");
        SessionPinning.PostgreSqlSearchPathPinSql.ShouldNotContain("\"");
        SessionPinning.OracleSchemaPinBlock.ShouldContain("DBMS_ASSERT.ENQUOTE_NAME(DBMS_ASSERT.SCHEMA_NAME(:schema), FALSE)");
        SessionPinning.OracleSchemaPinBlock.ShouldNotContain("\"");
    }

    [Theory]
    [InlineData("MySql")]
    public async Task UnmappedProvider_FailsClosed_InsteadOfSkippingSessionSetup(string provider)
    {
        var factory = new SqlConnectionFactory(Dev());

        await Should.ThrowAsync<NotSupportedException>(() =>
            factory.CreateOpenConnectionAsync(new DataSourceConnectionOptions { Provider = provider, ConnectionString = "x" }));
    }

    [Fact]
    public async Task Databricks_WithoutItsSection_FailsClosed()
    {
        // WP-X0j maps Databricks to the REST client; a data source without the Databricks section is refused.
        var factory = new SqlConnectionFactory(Dev());

        await Should.ThrowAsync<ArgumentException>(() =>
            factory.CreateOpenConnectionAsync(new DataSourceConnectionOptions { Provider = "Databricks", ConnectionString = "x" }));
    }
}
