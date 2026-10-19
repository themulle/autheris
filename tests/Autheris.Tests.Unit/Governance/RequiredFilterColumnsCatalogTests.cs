using System.Collections.Immutable;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

public sealed class RequiredFilterColumnsCatalogTests : IDisposable
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private readonly SqliteGovernanceRepository _repo;
    private readonly string _cs;

    public RequiredFilterColumnsCatalogTests()
    {
        _cs = $"Data Source=reqfil_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = _cs, JournalMode = "DELETE" }
        });
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
    }

    public void Dispose() => _repo.Dispose();

    private static TableMetadata Meta(
        TableIdentifier id,
        ImmutableArray<string> requiredFilters,
        IReadOnlyList<string>? columns = null)
    {
        var cols = columns ?? ["tenant_id", "order_id", "created_at"];
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table
            {
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                DisplayName = id.TableName,
                RequiredFilterColumns = requiredFilters
            },
            Columns = cols.Select(c => new TableColumn { ColumnName = c, DataType = "varchar" }).ToList()
        };
    }

    [Fact]
    public async Task RequiredFilterColumns_RoundTrip_Sqlite_StoresAndLoads()
    {
        var meta = Meta(Orders, ["tenant_id", "order_id"]);
        await _repo.UpsertTableMetadataAsync(meta);

        var loaded = await _repo.GetTableMetadataAsync(Orders);
        loaded.ShouldNotBeNull();
        loaded.RequiredFilterColumns.Length.ShouldBe(2);
        loaded.RequiredFilterColumns[0].ShouldBe("tenant_id");
        loaded.RequiredFilterColumns[1].ShouldBe("order_id");
    }

    [Fact]
    public async Task RequiredFilterColumns_EmptyOrNull_DefaultsToEmptyList()
    {
        var meta = Meta(Orders, ImmutableArray<string>.Empty);
        await _repo.UpsertTableMetadataAsync(meta);

        var loaded = await _repo.GetTableMetadataAsync(Orders);
        loaded.ShouldNotBeNull();
        loaded.RequiredFilterColumns.ShouldBeEmpty();
    }

    [Fact]
    public async Task RequiredFilterColumns_CasePreserved_NormalizedCaseInsensitive()
    {
        var meta = Meta(Orders, ["Tenant_Id"]);
        await _repo.UpsertTableMetadataAsync(meta);

        var loaded = await _repo.GetTableMetadataAsync(Orders);
        loaded.ShouldNotBeNull();
        loaded.RequiredFilterColumns.Length.ShouldBe(1);
        loaded.RequiredFilterColumns[0].ShouldBe("Tenant_Id");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_163_01_Catalog_MalformedJson_ThrowsAndBlocksTableAccess()
    {
        var meta = Meta(Orders, ["tenant_id"]);
        await _repo.UpsertTableMetadataAsync(meta);

        // Corrupt the json in the database directly
        using (var rawConn = new SqliteConnection(_cs))
        {
            await rawConn.OpenAsync();
            using var cmd = rawConn.CreateCommand();
            cmd.CommandText = "UPDATE TABLES SET required_filter_columns_json = '{corrupted:json[' WHERE table_name = 'orders'";
            await cmd.ExecuteNonQueryAsync();
        }

        // Create a new repository instance against the same database to ensure cache-free read
        var freshOptions = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = _cs, JournalMode = "DELETE" }
        });
        using var freshRepo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), freshOptions);
        await Should.ThrowAsync<JsonException>(() => freshRepo.GetTableMetadataAsync(Orders));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task T_SEC_163_02_Catalog_NonExistentColumnInRequiredList_FailsClosed()
    {
        // Table schema has columns: [tenant_id, order_id, created_at]
        // But required filter specifies: non_existent_column
        var meta = Meta(Orders, ["non_existent_column"]);

        await Should.ThrowAsync<CatalogValidationException>(() => _repo.UpsertTableMetadataAsync(meta));
    }
}
