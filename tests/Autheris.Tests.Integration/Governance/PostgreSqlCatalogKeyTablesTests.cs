using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>Issue #94: a missing key table makes the table unavailable (fail closed); ported from the former SQLite contract test (WP-SIMPL-11).</summary>
public sealed class PostgreSqlCatalogKeyTablesTests
{
    [Fact]
    public async Task MissingKeyTables_TableIsUnavailable()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>(), out var cs);
        var id = new TableIdentifier("sales", "dbo", "orders");
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceType = "sql", SourceName = "sales", SchemaName = "dbo", TableName = "orders", DisplayName = "orders" },
            Columns = [new TableColumn { ColumnName = "a", DataType = "integer" }, new TableColumn { ColumnName = "b", DataType = "integer" }],
            PrimaryKeyColumns = ["a"],
            Indexes = [new TableIndexDefinition("ix_b", ["b"], false)],
            ReplaceKeyDefinition = true
        });
        (await repo.GetTableMetadataAsync(id)).ShouldNotBeNull(); // caches the positive key-table probe
        GovernanceTestDatabase.ExecuteRaw(cs, "DROP TABLE TABLE_INDEX_COLUMNS CASCADE");
        repo.InvalidateMetadataCacheForTesting();
        repo.ResetKeyTableProbeForTesting(); // a positive probe is cached per instance: model a fresh instance

        await Should.ThrowAsync<TableMetadataUnavailableException>(() => repo.GetTableMetadataAsync(id));
        (await repo.GetAllTablesAsync()).ShouldNotContain(t => t.Identifier.Equals(id));
    }
}
