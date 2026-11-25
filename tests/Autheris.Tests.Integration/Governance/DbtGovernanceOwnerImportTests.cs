using System.Text;
using System.Text.Json;
using Autheris.Tests.Unit.Catalog;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Modules.Dbt;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

/// <summary>
/// Owner keys of the dbt governance import end to end on a real governance store (#1009 W3-12b, D13): per-table owners no longer exist, so
/// an item with <c>owner</c>, <c>owners</c>, <c>group</c> or <c>groups</c> rejects the whole import before any write, for every caller; the owner
/// shown for a table comes from its container only.
/// </summary>
public sealed class DbtGovernanceOwnerImportTests : IDisposable
{
    private const string Admin = "S-1-5-21-ADMIN-1";
    private static readonly TenantId Tenant = new("Sales");
    private readonly PostgreSqlGovernanceRepository _repo;
    private readonly TableIdentifier _table = new("sales", "dbo", "orders_" + Guid.NewGuid().ToString("N")[..8]);

    public DbtGovernanceOwnerImportTests()
    {
        var options = Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { SeedDemoData = false } });
        _repo = GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>(), options);
        SeedTable(_table);
    }

    public void Dispose() => _repo.Dispose();

    private void SeedTable(TableIdentifier id)
    {
        var table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, Description = "original", IsActive = true };
        _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        }).GetAwaiter().GetResult();
    }

    private static DbtGovernanceImportCaller Caller(bool governanceAdmin = true) =>
        new(new VirtualFilterActor(new Sid(Admin), IsSync: false, ["CORP\\admin", "admin@corp.example"]), Tenant, IsGovernanceAdmin: governanceAdmin);

    private DbtMetadataIngestionService Service(IAuditLogRepository? audit = null) =>
        new(
            Substitute.For<IDbtProposalRepository>(), _repo, Substitute.For<ILineageGraphStore>(), null, Options.Create(new GatewayOptions()), null, null, null, null, null, null,
            NullLogger<DbtMetadataIngestionService>.Instance,
            audit ?? _repo);

    private static string Payload(TableIdentifier t, string ownersJson, string extra = "") =>
        $$$"""{"classifications":[{"database":"{{{t.Domain}}}","schema":"{{{t.Schema}}}","table":"{{{t.TableName}}}"{{{extra}}}{{{(ownersJson.Length == 0 ? "" : "," + ownersJson)}}}}]}""";

    private Task<DbtGovernanceSyncResult> Import(DbtMetadataIngestionService service, string json, DbtGovernanceImportCaller? caller = null, bool dryRun = false, bool replace = false)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return service.IngestGovernanceStreamAsync(stream, caller ?? Caller(), dryRun, replace);
    }

    private async Task<string?> DescriptionAsync(TableIdentifier t) => (await _repo.GetTableMetadataAsync(t))!.Table.Description;

    private bool TableExists(string name)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT to_regclass(@t) IS NOT NULL";
        cmd.Parameters.AddWithValue("@t", name.ToLowerInvariant());
        return (bool)cmd.ExecuteScalar()!;
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("\"owner\":{\"sid\":\"S-1-5-21-5001\",\"display_name\":\"Ada\"}", true)]
    [InlineData("\"owners\":[\"ada@corp.example\"]", true)]
    [InlineData("\"owners\":[]", true)]
    [InlineData("\"group\":\"S-1-5-21-GRP\"", true)]
    [InlineData("\"groups\":[\"S-1-5-21-GRP\"]", false)]
    [InlineData("\"owner\":{\"sid\":\"S-1-5-21-5001\",\"display_name\":\"Ada\"}", false)]
    public async Task Import_WithOwnerKeys_RejectsWholeImport_NoWrites(string ownersJson, bool governanceAdmin)
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var result = await Import(Service(audit), Payload(_table, ownersJson, ",\"description\":\"changed\""), Caller(governanceAdmin));

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("dbt_owner_import_disabled");
        (await DescriptionAsync(_table)).ShouldBe("original");
        await audit.DidNotReceiveWithAnyArgs().RecordAuditEventsAsync(default!, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_DryRunAndReplace_WithOwnerKeys_AreRejectedAsWell()
    {
        var owners = "\"owners\":[\"S-1-5-21-5002\"]";
        (await Import(Service(), Payload(_table, owners, ",\"description\":\"changed\""), dryRun: true)).Success.ShouldBeFalse();
        (await Import(Service(), Payload(_table, owners, ",\"description\":\"changed\""), replace: true)).Success.ShouldBeFalse();
        (await DescriptionAsync(_table)).ShouldBe("original");
    }

    [Fact]
    public async Task Import_WithoutOwnerKeys_IsApplied()
    {
        var result = await Import(Service(), Payload(_table, string.Empty, ",\"description\":\"changed\""));

        result.Success.ShouldBeTrue();
        (await DescriptionAsync(_table)).ShouldBe("changed");
    }

    [Fact]
    public async Task DescribeDataset_ShowsTheContainerOwnerGroupOnly()
    {
        var gatewayOptions = new GatewayOptions();
        var catalogTables = Wp0.Tables(Wp0.Table(_table.Domain, _table.TableName, "Visible", ["id"]));
        var consents = Wp0.Consents(Wp0.Allow(_table, "S-USER", Wp0.TenantA, "id"));

        // The real container ownership is wired: the table belongs to a container whose owner group is the only owner.
        var stored = (await _repo.GetTableMetadataAsync(_table)).ShouldNotBeNull();
        var ownerTables = new Autheris.Tests.Common.Fakes.InMemoryTableMetadataRepository(stored with { OwnerTenantId = Wp0.TenantA.Value });
        var domains = new SingleContainerDomains(new ContainerDefaults(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), new Sid(ContainerOwnerGroup), Autheris.Domain.Security.ProtectionClass.Internal, CatalogVisibilityMode.OnRequest));
        var ownership = new Autheris.Application.Governance.Ownership.ContainerOwnership(ownerTables, domains);
        (await DescribeAsync(catalogTables, consents, gatewayOptions, ownership)).DataOwner.ShouldBe(ContainerOwnerGroup);

        // Without a container owner group the table simply has no owner.
        var unassigned = new Autheris.Application.Governance.Ownership.ContainerOwnership(ownerTables, new SingleContainerDomains(ContainerDefaults.Unassigned));
        (await DescribeAsync(catalogTables, consents, gatewayOptions, unassigned)).DataOwner.ShouldBeNull();
    }

    private const string ContainerOwnerGroup = "S-1-5-21-CONTAINER-OWNERS";

    private Task<McpDatasetDescription> DescribeAsync(
        ITableMetadataRepository catalogTables, IConsentRepository consents, GatewayOptions gatewayOptions,
        Autheris.Application.Governance.Ownership.IContainerOwnership ownership)
    {
        var mcp = new Autheris.Application.Mcp.Services.McpDatasetCatalog(
            catalogTables, consents, Substitute.For<Autheris.Application.Common.Interfaces.IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(gatewayOptions), dataOwnershipRepository: ownership,
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(catalogTables, consents, gatewayOptions, null));
        return mcp.DescribeDatasetAsync(Wp0.User("S-USER", "tenant-a"), $"{_table.Domain}.{_table.Schema}.{_table.TableName}");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_ErrorMessages_DoNotEchoIdentifiers()
    {
        // I393B-T-15 / SC-01: the rejection names a governed code, never an identifier value.
        const string sentinel = "S-1-5-21-SENTINEL-Zq91";
        var payloads = new[]
        {
            Payload(_table, $"\"owner\":{{\"sid\":\"{sentinel}\",\"display_name\":\"X\",\"email\":\"not-an-email-{sentinel}\"}}"),
            Payload(_table, $"\"owner\":{{\"sid\":\"{sentinel}\",\"display_name\":\"X\",\"account\":\"CORP\\\\ZQ91\"}}"),
            Payload(_table, $"\"owners\":[\"{sentinel}\"]"),
            Payload(_table, $"\"owner\":{{\"sid\":\"{sentinel}\",\"group\":\"g\"}}")
        };

        foreach (var payload in payloads)
        {
            var result = await Import(Service(), payload);
            result.Success.ShouldBeFalse();
            var text = result.ErrorMessage + string.Concat(result.Warnings);
            text.ShouldNotContain(sentinel, Case.Insensitive);
            text.ShouldNotContain("zq91", Case.Insensitive);
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task ManifestSync_MetaOwner_WritesNoTableOwners()
    {
        // I393B-T-14: the manifest sync keeps meta.owner as a suggestion; the per-table owner tables are gone (step 0045).
        var manifest = "{\"metadata\":{\"dbt_version\":\"1.8.0\"},\"nodes\":{\"model.p.m\":{\"name\":\"" + _table.TableName + "\",\"database\":\"" + _table.Domain +
                       "\",\"schema\":\"" + _table.Schema + "\",\"resource_type\":\"model\",\"meta\":{\"owner\":\"S-1-5-21-5017\",\"owner_email\":\"x@corp.example\"},\"columns\":{}}}}";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));

        await Service().IngestManifestStreamAsync(stream, Caller(), dryRun: false);

        TableExists("TABLE_OWNERS").ShouldBeFalse();
        TableExists("DATA_OWNERS").ShouldBeFalse();
        TableExists("DATA_OWNER_DELEGATIONS").ShouldBeFalse();
    }

    /// <summary>Every table of the tenant belongs to one container.</summary>
    private sealed class SingleContainerDomains(ContainerDefaults container) : IGovernanceDomainRepository
    {
        public Task<ContainerDefaults> GetContainerForTableAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) => Task.FromResult(container);

        public Task<IReadOnlyDictionary<Guid, ContainerDefaults>> GetContainersForTablesAsync(TenantId tenant, IReadOnlyCollection<Guid> tableIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ContainerDefaults>>(tableIds.ToDictionary(id => id, _ => container));

        public Task<GovernanceDomain?> GetAsync(TenantId tenant, Guid domainId, CancellationToken ct = default) => Task.FromResult<GovernanceDomain?>(null);
        public Task<IReadOnlyList<GovernanceDomain>> ListAsync(TenantId tenant, int limit = 100, int offset = 0, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GovernanceDomainUpsertResult> UpsertAsync(GovernanceDomain domain, int expectedVersion, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(TenantId tenant, Guid domainId, int expectedVersion, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GovernanceDomain> EnsureUnassignedAsync(TenantId tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AssignTablesAsync(TenantId tenant, Guid domainId, IReadOnlyList<Guid> tableIds, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(ObjectProtection? Table, IReadOnlyDictionary<string, ObjectProtection> Columns)> GetProtectionAsync(TenantId tenant, Guid tableId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetProtectionAsync(TenantId tenant, Guid tableId, string column, ObjectProtection protection, Sid actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task SetTableProtectionAsync(TenantId tenant, Guid tableId, ObjectProtection protection, IReadOnlyList<string>? clearColumns, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
