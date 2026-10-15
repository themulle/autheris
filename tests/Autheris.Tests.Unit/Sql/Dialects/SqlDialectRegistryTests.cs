namespace Autheris.Tests.Unit.Sql.Dialects;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Sql.Dialects;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using Xunit;

public sealed class SqlDialectRegistryTests
{
    [Fact]
    public void EveryDatabaseDialect_HasProvider_AndConsistentDescriptor()
    {
        var registry = SqlDialectRegistry.Default;
        var all = registry.GetAll();

        all.Count.ShouldBe(Enum.GetValues<DatabaseDialect>().Length);

        foreach (var dialect in Enum.GetValues<DatabaseDialect>())
        {
            var provider = registry.GetProvider(dialect);
            provider.ShouldNotBeNull();
            provider.Descriptor.Dialect.ShouldBe(dialect);
            provider.Descriptor.CanonicalName.ShouldNotBeNullOrWhiteSpace();
            provider.Descriptor.Aliases.ShouldNotBeEmpty();
            provider.Descriptor.Capabilities.ShouldBe(DialectCapabilityTable.Default.Get(provider.Descriptor.Target));
            provider.Descriptor.MaxParameterCount.ShouldBeGreaterThan(0);

            registry.GetProvider(provider.Descriptor.Target).ShouldBeSameAs(provider);
            registry.TryGetByName(provider.Descriptor.CanonicalName, out var byCanonical).ShouldBeTrue();
            byCanonical.ShouldBeSameAs(provider);

            foreach (var alias in provider.Descriptor.Aliases)
            {
                registry.TryGetByName(alias, out var byAlias).ShouldBeTrue();
                byAlias.ShouldBeSameAs(provider);
                registry.TryGetByName(alias.ToUpperInvariant(), out var byUpper).ShouldBeTrue();
                byUpper.ShouldBeSameAs(provider);
            }
        }
    }

    [Theory]
    [InlineData(DatabaseDialect.SqlServer, TargetSqlDialect.SqlServer, true, true, true, "[orders]", "@p0")]
    [InlineData(DatabaseDialect.PostgreSql, TargetSqlDialect.PostgreSql, true, true, true, "\"orders\"", "@p0")]
    [InlineData(DatabaseDialect.Sqlite, TargetSqlDialect.Sqlite, true, true, true, "\"orders\"", "@p0")]
    [InlineData(DatabaseDialect.Oracle, TargetSqlDialect.Oracle, true, false, false, "\"orders\"", ":p0")]
    [InlineData(DatabaseDialect.Databricks, TargetSqlDialect.Databricks, false, false, false, "`orders`", "@p0")]
    public void Provider_ExposesExpectedCapabilitiesQuotingAndParameterFormatting(
        DatabaseDialect dialect,
        TargetSqlDialect expectedTarget,
        bool expectedExecutable,
        bool expectedTreeCompile,
        bool expectedCdc,
        string expectedQuoted,
        string expectedParam0)
    {
        var provider = SqlDialectRegistry.Default.GetProvider(dialect);

        provider.Descriptor.Target.ShouldBe(expectedTarget);
        provider.Descriptor.IsExecutable.ShouldBe(expectedExecutable);
        provider.Descriptor.SupportsTreeCompile.ShouldBe(expectedTreeCompile);
        provider.Descriptor.SupportsCdc.ShouldBe(expectedCdc);
        provider.QuoteIdentifier("orders").ShouldBe(expectedQuoted);
        provider.FormatParameterName(0).ShouldBe(expectedParam0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("mysql")]
    [InlineData("cockroachdb")]
    [InlineData("SqlServer, Sqlite")]
    [InlineData("99")]
    public void TryGetByName_UnknownOrMalformedDialect_FailsClosed(string? name)
    {
        SqlDialectRegistry.Default.TryGetByName(name, out var provider).ShouldBeFalse();
        provider.ShouldBeNull();
    }

    [Fact]
    public void GetProvider_UnmappedDialect_ThrowsNotSupportedException()
    {
        Should.Throw<NotSupportedException>(() => SqlDialectRegistry.Default.GetProvider((DatabaseDialect)99));
        Should.Throw<NotSupportedException>(() => SqlDialectRegistry.Default.GetProvider(TargetSqlDialect.Snowflake));
    }

    [Fact]
    public void AddAutherisSqlDialects_RegistersProvidersAndRegistryInDi()
    {
        var services = new ServiceCollection();
        services.AddAutherisSqlDialects();

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<ISqlDialectRegistry>();
        var providers = sp.GetServices<ISqlDialectProvider>().ToList();

        providers.Count.ShouldBe(5);
        registry.GetAll().Count.ShouldBe(5);
        registry.TryGetByName("postgres", out var pg).ShouldBeTrue();
        pg!.Descriptor.Dialect.ShouldBe(DatabaseDialect.PostgreSql);
    }

    [Fact]
    public async Task CustomRegistry_ControlsCatalogSchemaModelAndStartupValidator()
    {
        var customOracle = new CustomDialectProvider(
            new SqlDialectDescriptor(
                Dialect: DatabaseDialect.Oracle,
                Target: TargetSqlDialect.Oracle,
                CanonicalName: "Oracle",
                Aliases: ["oracle"],
                Capabilities: DialectCapabilityTable.Default.Get(TargetSqlDialect.Oracle),
                IsExecutable: false,
                SupportsTreeCompile: false,
                SupportsCdc: false,
                MaxParameterCount: 1000));
        var customDatabricks = new CustomDialectProvider(
            new SqlDialectDescriptor(
                Dialect: DatabaseDialect.Databricks,
                Target: TargetSqlDialect.Databricks,
                CanonicalName: "Databricks",
                Aliases: ["databricks"],
                Capabilities: DialectCapabilityTable.Default.Get(TargetSqlDialect.Databricks),
                IsExecutable: true,
                SupportsTreeCompile: true,
                SupportsCdc: false,
                MaxParameterCount: 1000));

        var registry = new SqlDialectRegistry([
            SqlServerDialectProvider.Instance,
            PostgreSqlDialectProvider.Instance,
            SqliteDialectProvider.Instance,
            customOracle,
            customDatabricks
        ]);

        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();
        var oracleTable = new TableMetadata
        {
            Identifier = new TableIdentifier("erp", "app", "invoices"),
            Table = new Table { SourceName = "erp", SchemaName = "app", TableName = "invoices", SourceType = "Oracle", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var dbxTable = new TableMetadata
        {
            Identifier = new TableIdentifier("lake", "gold", "events"),
            Table = new Table { SourceName = "lake", SchemaName = "gold", TableName = "events", SourceType = "Databricks", DataSourceType = DataSourceType.Sql, IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        metaRepo.GetAllTablesAsync(Arg.Any<System.Threading.CancellationToken>()).Returns([oracleTable, dbxTable]);
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<System.Threading.CancellationToken>()).Returns([]);

        var model = await CatalogSchemaModel.BuildAsync(metaRepo, relRepo, logger: null, dialectRegistry: registry);
        model.Tables.Select(t => t.Identifier).ShouldBe([dbxTable.Identifier]);

        var denied = GatewayStartupValidator.ListDeniedTables(new PolicyLintInput([oracleTable, dbxTable], [], [], []), dialectRegistry: registry);
        denied.ShouldContain(d => d.Table == "erp.app.invoices" && d.Code == GatewayStartupValidator.DeniedDialectNotAdmitted);
        denied.ShouldNotContain(d => d.Table == "lake.gold.events");
    }

    private sealed class CustomDialectProvider(SqlDialectDescriptor descriptor) : ISqlDialectProvider
    {
        public SqlDialectDescriptor Descriptor { get; } = descriptor;

        public string QuoteIdentifier(string identifier) => Descriptor.Dialect.QuoteIdentifier(identifier);

        public string FormatParameterName(int index) => $"@p{index}";
    }
}
