namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#112 T-3.2: persistence contract of <see cref="ISemanticManifestStore"/> on the SQLite governance store.</summary>
public sealed class SemanticManifestRepositorySqliteTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    private SqliteGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source={System.IO.Path.Combine(_dir.Path, "gov.db")}",
                SeedDemoData = false,
                EnableOutboxProcessor = false
            }
        });
        return GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options, env);
    }

    internal static StoredSemanticManifest Sample(TenantId tenant, string version = "v1", string json = "{\"a\":1}", string hash = "h1") =>
        new(tenant, version, hash, 1, 2, 3, json, "admin", new DateTimeOffset(2026, 10, 18, 10, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Absent_ReturnsNull()
    {
        using var repository = NewRepository();

        (await ((ISemanticManifestStore)repository).GetAsync(TenantA, default)).ShouldBeNull();
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsEveryField()
    {
        using var repository = NewRepository();
        ISemanticManifestStore store = repository;
        var sample = Sample(TenantA);

        await store.SaveAsync(TenantA, sample, default);
        var loaded = await store.GetAsync(TenantA, default);

        loaded.ShouldBe(sample);
    }

    [Fact]
    public async Task Save_ReplacesPreviousManifestOfTheTenant()
    {
        using var repository = NewRepository();
        ISemanticManifestStore store = repository;

        await store.SaveAsync(TenantA, Sample(TenantA, "v1", "{\"a\":1}", "h1"), default);
        await store.SaveAsync(TenantA, Sample(TenantA, "v2", "{\"a\":2}", "h2"), default);

        var loaded = await store.GetAsync(TenantA, default);
        loaded!.Version.ShouldBe("v2");
        loaded.ContentHash.ShouldBe("h2");
        loaded.Json.ShouldBe("{\"a\":2}");
    }

    [Fact]
    public async Task Tenants_AreIsolated()
    {
        using var repository = NewRepository();
        ISemanticManifestStore store = repository;

        await store.SaveAsync(TenantA, Sample(TenantA), default);

        (await store.GetAsync(TenantB, default)).ShouldBeNull();
    }

    [Fact]
    public async Task Save_WithMismatchingTenantInRecord_Throws()
    {
        using var repository = NewRepository();
        ISemanticManifestStore store = repository;

        await Should.ThrowAsync<ArgumentException>(() => store.SaveAsync(TenantB, Sample(TenantA), default));
    }

    [Fact]
    public async Task ConcurrentSaves_LeaveExactlyOneConsistentRow()
    {
        using var repository = NewRepository();
        ISemanticManifestStore store = repository;

        await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            store.SaveAsync(TenantA, Sample(TenantA, $"v{i}", $"{{\"n\":{i}}}", $"h{i}"), default)));

        var loaded = await store.GetAsync(TenantA, default);
        loaded.ShouldNotBeNull();
        loaded.Json.ShouldBe($"{{\"n\":{loaded.Version[1..]}}}");
        loaded.ContentHash.ShouldBe($"h{loaded.Version[1..]}");
    }

    [Fact]
    public async Task Manifest_SurvivesStoreRecreation()
    {
        using (var first = NewRepository())
        {
            await ((ISemanticManifestStore)first).SaveAsync(TenantA, Sample(TenantA), default);
        }

        using var second = NewRepository();

        (await ((ISemanticManifestStore)second).GetAsync(TenantA, default))!.Version.ShouldBe("v1");
    }

    private static Autheris.Domain.Model.TableMetadata TableOf(string domain, string schema, string? documentationSource)
    {
        var id = Guid.NewGuid();
        return new Autheris.Domain.Model.TableMetadata
        {
            Identifier = new Autheris.Domain.Common.TableIdentifier(domain, schema, "orders"),
            Table = new Autheris.Domain.Model.Table
            {
                Id = id, SourceName = domain, SchemaName = schema, TableName = "orders", IsActive = true, DocumentationSource = documentationSource,
                DataSourceType = Autheris.Domain.Model.DataSourceType.Sql
            },
            Columns = [new Autheris.Domain.Model.TableColumn { TableId = id, ColumnName = "id", DataType = "int" }]
        };
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UserSchemaNamedLikeTenantHash_CannotBeCreated_AndTheSemanticDomainIsReserved()
    {
        using var repository = NewRepository();
        var hash = Autheris.Application.Dbt.Services.SemanticVirtualTables.TenantSchema(TenantA);

        // A real table with a tenant-hash schema name, and a semantic-domain table without the server-set marker, are refused.
        await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayInvalidQueryException>(() =>
            ((Autheris.Application.Interfaces.ITableMetadataRepository)repository).UpsertTableMetadataAsync(TableOf("sales", hash, null), default));
        await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayInvalidQueryException>(() =>
            ((Autheris.Application.Interfaces.ITableMetadataRepository)repository).UpsertTableMetadataAsync(TableOf("semantic", hash, null), default));
        await Should.ThrowAsync<Autheris.Domain.Exceptions.GatewayInvalidQueryException>(() =>
            ((Autheris.Application.Interfaces.ITableMetadataRepository)repository).UpsertTableMetadataAsync(TableOf("semantic", "my_schema", "dbt"), default));

        // The ingestion service's own shape and ordinary tables are accepted.
        await ((Autheris.Application.Interfaces.ITableMetadataRepository)repository).UpsertTableMetadataAsync(TableOf("semantic", hash, "dbt"), default);
        await ((Autheris.Application.Interfaces.ITableMetadataRepository)repository).UpsertTableMetadataAsync(TableOf("sales", "public", null), default);
    }
}
