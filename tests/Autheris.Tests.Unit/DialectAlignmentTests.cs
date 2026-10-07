using System.Security.Claims;
using Autheris.Api.Extensions;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// D-1 (docs/plans/rls-subquery-in-strategy.md): the catalog dialect decides quoting and row filter syntax, the
/// connection provider decides the backend. Both must agree, and an unknown dialect never falls back silently.
/// </summary>
public sealed class DialectAlignmentTests
{
    [Theory]
    [InlineData("SqlServer", DatabaseDialect.SqlServer)]
    [InlineData("mssql", DatabaseDialect.SqlServer)]
    [InlineData("Microsoft SQL Server", DatabaseDialect.SqlServer)]
    [InlineData("PostgreSQL", DatabaseDialect.PostgreSql)]
    [InlineData("npgsql", DatabaseDialect.PostgreSql)]
    [InlineData("sqlite3", DatabaseDialect.Sqlite)]
    [InlineData("Oracle", DatabaseDialect.Oracle)]
    [InlineData("databricks", DatabaseDialect.Databricks)]
    [InlineData("2", DatabaseDialect.SqlServer)]
    public void ParseDialect_KnownValues_AreMapped(string sourceType, DatabaseDialect expected)
    {
        DatabaseDialectExtensions.ParseDialect(sourceType).ShouldBe(expected);
        DatabaseDialectExtensions.TryParseDialect(sourceType, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sql")]
    [InlineData("Alation")]
    [InlineData("MicrosoftPurview")]
    [InlineData("Mysql")]
    [InlineData("99")]
    [InlineData("-1")]
    public void ParseDialect_UnknownOrEmpty_FailsClosed(string? sourceType)
    {
        Should.Throw<NotSupportedException>(() => DatabaseDialectExtensions.ParseDialect(sourceType));
        DatabaseDialectExtensions.TryParseDialect(sourceType, out _).ShouldBeFalse();
    }

    [Fact]
    public void TableDialect_SqlTableWithUnknownSourceType_Throws()
    {
        var table = new Table { SourceType = "Alation", DataSourceType = DataSourceType.Sql };

        Should.Throw<NotSupportedException>(() => _ = table.Dialect);
    }

    [Theory]
    [InlineData(DataSourceType.HttpDeclarative)]
    [InlineData(DataSourceType.HttpPlugin)]
    [InlineData(DataSourceType.LakehouseDelta)]
    public void TableDialect_NonSqlTableWithUnknownSourceType_DoesNotThrow(DataSourceType dataSourceType)
    {
        // Non-SQL sources never send SQL to a database; the dialect only shapes the in-memory row filter.
        var table = new Table { SourceType = "rest", DataSourceType = dataSourceType };

        Should.NotThrow(() => _ = table.Dialect);
    }

    [Theory]
    [InlineData("Alation", "SqlServer", "SqlServer", true)]
    [InlineData("Mssql", null, "Mssql", true)]
    [InlineData("Oracle", "PostgreSQL", "PostgreSQL", true)]
    [InlineData("Alation", null, "Alation", false)]
    [InlineData(null, null, "", false)]
    public void ResolveCatalogSourceType_KeepsSupportedExistingDialect(
        string? catalogSourceType, string? existingSourceType, string expected, bool expectedSupported)
    {
        var resolved = DatabaseDialectExtensions.ResolveCatalogSourceType(catalogSourceType, existingSourceType, out var supported);

        resolved.ShouldBe(expected);
        supported.ShouldBe(expectedSupported);
    }

    private static DataSourceExecutionContext CreateContext(string sourceType)
    {
        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("erp", "dbo", "orders"),
            Table = new Table { SourceName = "erp", SchemaName = "dbo", TableName = "orders", SourceType = sourceType },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1")], "Test"));
        return new DataSourceExecutionContext("erp", metadata, principal, decision, new Dictionary<string, object?>(), ["id"]);
    }

    private static SqlDataSourceExecutor CreateExecutor(string provider, ISqlConnectionFactory factory)
    {
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["erp"] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "Server=unused" }
                }
            }
        });
        return new SqlDataSourceExecutor(factory, options);
    }

    [Theory]
    [InlineData("SqlServer", "PostgreSQL")]
    [InlineData("PostgreSql", "SqlServer")]
    [InlineData("SqlServer", "Oracle")]
    [InlineData("", "SqlServer")]
    [InlineData("   ", "PostgreSQL")]
    public async Task SqlDataSourceExecutor_CatalogDialectDiffersFromProvider_FailsBeforeConnecting(string provider, string catalogSourceType)
    {
        var factory = Substitute.For<ISqlConnectionFactory>();
        var executor = CreateExecutor(provider, factory);

        await Should.ThrowAsync<InvalidOperationException>(() => executor.ExecuteAsync(CreateContext(catalogSourceType)));

        await factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    public void GatewayOptions_UndefinedSubqueryStrategy_FailsValidation()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:RowFilters:SubqueryStrategy"] = "7"
        }).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        var services = new ServiceCollection();

        services.AddGatewayOptions(config, env);
        var sp = services.BuildServiceProvider();

        var ex = Should.Throw<OptionsValidationException>(() => _ = sp.GetRequiredService<IOptions<GatewayOptions>>().Value);
        ex.Message.ShouldContain("SubqueryStrategy");
    }

    [Theory]
    [InlineData("Exists")]
    [InlineData("InCorrelated")]
    [InlineData("In")]
    public void GatewayOptions_DefinedSubqueryStrategy_PassesValidation(string strategy)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:RowFilters:SubqueryStrategy"] = strategy
        }).Build();
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(Environments.Development);
        var services = new ServiceCollection();

        services.AddGatewayOptions(config, env);
        var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IOptions<GatewayOptions>>().Value.RowFilters.SubqueryStrategy.ToString().ShouldBe(strategy);
    }
}
