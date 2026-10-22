using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

/// <summary>
/// PERS-17: two gateway replicas on one governance database. A catalog write on replica A must be visible on replica B at once,
/// not after the TTL of B's metadata cache: cache hits are validated against the table epoch.
/// </summary>
public abstract class MetadataCacheReplicaContractBase : IAsyncLifetime
{
    protected ITableMetadataRepository ReplicaA { get; private set; } = null!;
    protected ITableMetadataRepository ReplicaB { get; private set; } = null!;
    protected bool Available { get; private set; }

    /// <summary>Two repositories on the same database, or null when the engine is not available.</summary>
    protected abstract Task<(ITableMetadataRepository A, ITableMetadataRepository B)?> CreateReplicasAsync();

    protected abstract Task DisposeReplicasAsync();

    public async Task InitializeAsync()
    {
        var replicas = await CreateReplicasAsync();
        Available = replicas.HasValue;
        if (replicas is { } r)
        {
            ReplicaA = r.A;
            ReplicaB = r.B;
        }
    }

    public Task DisposeAsync() => DisposeReplicasAsync();

    private static TableMetadata Table(string name, bool active, string? maskedColumn)
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("ReplicaDomain", "dbo", name),
            Table = new Table { Id = id, SourceName = "ReplicaDomain", SchemaName = "dbo", TableName = name, Sensitivity = "LOW", IsActive = active },
            Columns = [new TableColumn { TableId = id, ColumnName = "email", DataType = "TEXT", IsSensitive = true }],
            ColumnMaskingRules = maskedColumn == null
                ? new Dictionary<string, MaskingRule>()
                : new Dictionary<string, MaskingRule> { [maskedColumn] = new MaskingRule { RuleType = "REDACT" } }
        };
    }

    private static string Name(string prefix) => prefix + "_" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task MetadataCache_WriteOnReplicaA_ReplicaBSeesMaskingRuleImmediately()
    {
        if (!Available) return;
        var name = Name("mc");
        var id = new TableIdentifier("ReplicaDomain", "dbo", name);
        await ReplicaA.UpsertTableMetadataAsync(Table(name, active: true, maskedColumn: null));
        (await ReplicaB.GetTableMetadataAsync(id))!.ColumnMaskingRules.ShouldBeEmpty(); // fills B's cache

        await ReplicaA.UpsertTableMetadataAsync(Table(name, active: true, maskedColumn: "email"));

        (await ReplicaB.GetTableMetadataAsync(id))!.ColumnMaskingRules.ShouldContainKey("email");
    }

    [Fact]
    public async Task MetadataCache_DeactivateOnA_BSeesInactive()
    {
        if (!Available) return;
        var name = Name("mc");
        var id = new TableIdentifier("ReplicaDomain", "dbo", name);
        await ReplicaA.UpsertTableMetadataAsync(Table(name, active: true, maskedColumn: null));
        (await ReplicaB.GetTableMetadataAsync(id))!.Table.IsActive.ShouldBeTrue();

        await ReplicaA.UpsertTableMetadataAsync(Table(name, active: false, maskedColumn: null));

        (await ReplicaB.GetTableMetadataAsync(id))!.Table.IsActive.ShouldBeFalse();
    }

    [Fact]
    public async Task MetadataCache_MissingTableCreatedOnA_BFindsIt()
    {
        if (!Available) return;
        var name = Name("mc");
        var id = new TableIdentifier("ReplicaDomain", "dbo", name);
        (await ReplicaB.GetTableMetadataAsync(id)).ShouldBeNull(); // a negative result is never cached

        await ReplicaA.UpsertTableMetadataAsync(Table(name, active: true, maskedColumn: null));

        (await ReplicaB.GetTableMetadataAsync(id)).ShouldNotBeNull();
    }
}
