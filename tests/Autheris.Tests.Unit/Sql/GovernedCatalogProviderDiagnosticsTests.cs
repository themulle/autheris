using System.Data.Common;
using System.Runtime.CompilerServices;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Autheris.Tests.Unit.Databricks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.Sql;

/// <summary>A failed catalog load stays fail closed but now logs a classified, credential-free reason.</summary>
public sealed class GovernedCatalogProviderDiagnosticsTests
{
    private const string Source = "gateway_lwe";
    private const string ConnectionString = "Server=10.1.1.1,1433;Database=Secret_DB;Integrated Security=true;";
    private static readonly TableIdentifier Items = new("inventory", "inventory", "ItemInventoryByCompanyHistory");

    private sealed class NoopReader : IColumnMetadataReader
    {
        public TargetSqlDialect Dialect => TargetSqlDialect.SqlServer;

        public async IAsyncEnumerable<DbColumnMetadata> ReadAsync(DbConnection connection, TableIdentity table,
            [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private static async Task<CapturingLogger<GovernedCatalogProvider>> RunAsync(Exception failure, bool configured = true, string? mappedSource = null)
    {
        var governance = Substitute.For<IGovernanceRepository>();
        governance.GetTableMetadataAsync(Items, Arg.Any<CancellationToken>()).Returns(new TableMetadata
        {
            Table = new Table { SourceType = "SqlServer", SourceName = Source, SchemaName = "inventory", TableName = Items.TableName },
            Identifier = Items,
            Columns = new[] { new TableColumn { ColumnName = "id" } }
        });
        var gateway = new GatewayOptions { DataSources = new SqlDataSourceOptions() };
        if (configured)
        {
            gateway.DataSources.Connections[mappedSource ?? Source] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = ConnectionString };
        }

        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns<Task<DbConnection>>(_ => throw failure);
        var logger = new CapturingLogger<GovernedCatalogProvider>();
        var sut = new GovernedCatalogProvider(governance, Options.Create(gateway), factory, new[] { new NoopReader() },
            new GovernedCatalogOptions { CacheTtlSeconds = 60 }, null, logger);

        var catalog = await sut.GetCatalogAsync(Source, new[] { Items }, CancellationToken.None);
        catalog.Resolve(new SqlQualifiedName(new[] { new SqlIdentifier("inventory"), new SqlIdentifier(Items.TableName) })).ShouldBeNull();
        return logger;
    }

    [Fact]
    public async Task ClassifiedNtlmFailure_IsLoggedWithSafeMessageAndFields_FailClosed()
    {
        var failure = new SqlServerWindowsAuthException(
            SqlServerWindowsAuthFailureCategory.NtlmNegotiation,
            "SQL Server Windows Authentication (managed NTLM) failed during NTLM negotiation (step 'challenge').",
            new InvalidOperationException("Cannot generate SSPI context. Server=10.1.1.1 Password=hunter2"),
            ntlmStatus: "InvalidToken", ntlmStep: "challenge");

        var logger = await RunAsync(failure);

        var entry = logger.Entries.ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Text.ShouldContain("managed NTLM");
        entry.Text.ShouldContain("NtlmNegotiation");
        entry.Text.ShouldContain("NtlmStatus=InvalidToken");
        entry.Text.ShouldNotContain("hunter2");
        entry.Text.ShouldNotContain("Secret_DB");
    }

    [Fact]
    public async Task MissingConfiguration_IsLoggedWithDataSourceName()
    {
        var logger = await RunAsync(new InvalidOperationException("unused"), configured: false);

        logger.Entries.ShouldHaveSingleItem().Text.ShouldContain($"The data source '{Source}' has no configured connection.");
    }

    [Fact]
    public async Task UnclassifiedFailure_LogsTypeChainButNotTheDriverMessage()
    {
        var failure = new InvalidOperationException("Timeout expired. Server=10.1.1.1;Password=hunter2", new TimeoutException("inner secret"));

        var logger = await RunAsync(failure);

        var text = logger.Entries.ShouldHaveSingleItem().Text;
        text.ShouldContain("InvalidOperationException -> TimeoutException");
        text.ShouldNotContain("hunter2");
        text.ShouldNotContain("inner secret");
        text.ShouldNotContain("Secret_DB");
    }
}
