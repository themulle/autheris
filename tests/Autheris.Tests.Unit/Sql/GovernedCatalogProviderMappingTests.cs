using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql;

/// <summary>
/// #668: a dbt table of database <c>sales</c> mapped to connection <c>pgdata</c> is catalogued as <c>sales.dbo.orders</c> with
/// <c>SourceName = pgdata</c>. The governed catalog admits it for the logical name <c>sales</c> through
/// <c>WebSql:DataSourceMappings</c> and only through the resolved connection key.
/// </summary>
public sealed class GovernedCatalogProviderMappingTests
{
    private const string Logical = "sales";
    private const string Key = "pgdata";
    private static readonly TableIdentifier Orders = new(Logical, "dbo", "orders");

    private sealed class CapturingLogger : ILogger<GovernedCatalogProvider>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    /// <summary>Connections by key with their provider; the mapping of <c>sales</c> points to <paramref name="mappingTarget"/> (null = no mapping).</summary>
    private static GatewayOptions Gateway(string? mappingTarget, params (string Name, string Provider)[] connections)
    {
        var gateway = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true } };
        foreach (var (name, provider) in connections)
        {
            gateway.DataSources.Connections[name] = new DataSourceConnectionOptions { Provider = provider, ConnectionString = "x" };
        }

        if (mappingTarget is not null)
        {
            gateway.WebSql.DataSourceMappings[Logical] = mappingTarget;
        }

        return gateway;
    }

    private static readonly (string, string) Pg = (Key, "PostgreSql");

    // A catalog entry does not record its connection: its SourceName is the logical name (the identifier domain).
    private static IGovernanceRepository Governance(string sourceName)
    {
        var repo = Substitute.For<IGovernanceRepository>();
        repo.GetTableMetadataAsync(Orders, Arg.Any<CancellationToken>()).Returns(_ => new TableMetadata
        {
            Table = new Table { SourceType = "PostgreSql", SourceName = sourceName, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql },
            Identifier = Orders,
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        });
        return repo;
    }

    private static GovernedCatalogProvider Build(GatewayOptions gateway, IGovernanceRepository repo, CapturingLogger? logger = null) =>
        new(repo, Options.Create(gateway), new GovernedCatalogOptions { CacheTtlSeconds = 300 }, null, logger);

    private static TableCatalogEntry? Resolve(ITableCatalog catalog) =>
        catalog.Resolve(new SqlQualifiedName(new[] { new SqlIdentifier("dbo"), new SqlIdentifier("orders") }));

    private static async Task<TableCatalogEntry?> Catalog(GovernedCatalogProvider sut, string logical, string key) =>
        Resolve(await sut.GetCatalogAsync(new GovernedDataSource(logical, key), new[] { Orders }, CancellationToken.None));

    [Fact]
    public async Task GetCatalog_MappedLogicalName_UsesConnectionKeyAndAdmitsTable()
    {
        var sut = Build(Gateway(Key, Pg), Governance(Logical));

        (await Catalog(sut, Logical, Key)).ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GetCatalog_EntryOfOtherLogicalName_NotAdmittedThroughMapping()
    {
        var sut = Build(Gateway(Key, Pg), Governance("other"));

        (await Catalog(sut, Logical, Key)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GetCatalog_MappedConnectionWithOtherDialect_Denied()
    {
        var sut = Build(Gateway("lite", Pg, ("lite", "Sqlite")), Governance(Logical));

        (await Catalog(sut, Logical, "lite")).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GetCatalog_NoConnectionForResolvedKey_DeniedFailClosed()
    {
        var sut = Build(Gateway(Key, ("unrelated", "PostgreSql")), Governance(Logical));

        (await Catalog(sut, Logical, Key)).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [Trait("Category", "Security")]
    public async Task GetCatalog_EmptyMappingTarget_Denied(string target)
    {
        // a connection named like the logical name exists: a broken mapping must not fall back to it
        var sut = Build(Gateway(target, Pg, (Logical, "PostgreSql")), Governance(Logical));

        (await Catalog(sut, Logical, target)).ShouldBeNull();
        (await Catalog(sut, Logical, Logical)).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GetCatalog_ConnectionKeyDifferingFromTheResolver_Denied()
    {
        var sut = Build(Gateway(Key, Pg, ("other", "PostgreSql")), Governance(Logical));

        // a caller that names a key the mapping does not resolve to never gets an admission
        (await Catalog(sut, Logical, "other")).ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GetCatalog_CacheKey_IncludesConnectionKey()
    {
        var gateway = Gateway(Key, Pg, ("lite", "Sqlite"));
        var sut = Build(gateway, Governance(Logical));
        (await Catalog(sut, Logical, Key)).ShouldNotBeNull();

        // the mapping now points to a connection of another dialect: the cached admission of the former key must not be reused
        gateway.WebSql.DataSourceMappings[Logical] = "lite";

        (await Catalog(sut, Logical, "lite")).ShouldBeNull();
    }

    [Fact]
    public async Task LoadAsync_DenialReason_NamesBothNames()
    {
        var logger = new CapturingLogger();
        var sut = Build(Gateway("lite", Pg, ("lite", "Sqlite")), Governance(Logical), logger);

        (await Catalog(sut, Logical, "lite")).ShouldBeNull();

        logger.Messages.ShouldContain(m => m.Contains($"'{Logical}'") && m.Contains("'lite'"));
    }
}
