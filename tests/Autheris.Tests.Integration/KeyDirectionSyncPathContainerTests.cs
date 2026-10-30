namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Catalog;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>I403 T-36 (slimmed to the provider plus reconciler path): a real SQL Server key with DESC columns comes back through the governed provider.</summary>
public sealed class KeyDirectionSyncPathContainerTests : IClassFixture<AstCompilerSqlServerFixture>
{
    private readonly AstCompilerSqlServerFixture _fx;

    public KeyDirectionSyncPathContainerTests(AstCompilerSqlServerFixture fixture) => _fx = fixture;

    [Fact]
    public async Task SqlServer_DescKey_ProviderAndReconciler_ReturnDescDirections()
    {
        _fx.Db.ShouldNotBeNull();
        _fx.Db.IsAvailable.ShouldBeTrue("SQL Server container is required for I403 evidence");
        var table = "KdSync" + Guid.NewGuid().ToString("N")[..8];
        await using (var conn = new SqlConnection(_fx.Db.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"CREATE TABLE dbo.{table} (Id int NOT NULL, CreatedAt datetime2 NOT NULL, CONSTRAINT pk_{table} PRIMARY KEY (CreatedAt DESC, Id))";
            await cmd.ExecuteNonQueryAsync();
        }

        var gateway = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = { ["ds"] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = _fx.Db.ConnectionString } }
            }
        };
        var provider = new GovernedCatalogProvider(
            Substitute.For<IGovernanceRepository>(), Options.Create(gateway), new SqlConnectionFactory(new DevelopmentEnvironment()),
            Array.Empty<IColumnMetadataReader>(), new GovernedCatalogOptions { CacheTtlSeconds = 60 }, TimeProvider.System, null,
            new IKeyIndexMetadataReader[] { new SqlServerKeyIndexMetadataReader() });
        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("ds", "dbo", table),
            Table = new Table { SourceName = "ds", SourceType = "SqlServer", SchemaName = "dbo", TableName = table, DataSourceType = DataSourceType.Sql },
            PrimaryKeyColumns = ["CreatedAt", "Id"],
            ReplaceKeyDefinition = true
        };

        var snapshot = await provider.ReadAsync(metadata, CancellationToken.None);
        var result = PhysicalKeyDirectionReconciler.Reconcile(metadata, null, snapshot);

        snapshot.State.ShouldBe(PhysicalKeySnapshotState.Known);
        result.PhysicalApplied.ShouldBeTrue();
        result.Metadata.PrimaryKeyDirections.ToArray().ShouldBe([KeyColumnDirection.Descending, KeyColumnDirection.Ascending]);
        result.Findings.ShouldBeEmpty();
    }
}
