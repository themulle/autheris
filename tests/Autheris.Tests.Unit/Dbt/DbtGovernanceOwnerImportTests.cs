using System.Text;
using System.Text.Json;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Governance;
using Autheris.Tests.Unit.Catalog;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Dbt;

/// <summary>Issue #393 part b: owners from the dbt governance import (I393B-SC-01..11, I393B-T-01..15) end to end on a real SQLite governance store.</summary>
public sealed class DbtGovernanceOwnerImportTests : IDisposable
{
    private const string Admin = "S-1-5-21-ADMIN-1";
    private static readonly TenantId Tenant = new("Sales");
    private readonly SqliteGovernanceRepository _repo;
    private readonly TableIdentifier _table = new("sales", "dbo", "orders_" + Guid.NewGuid().ToString("N")[..8]);

    public DbtGovernanceOwnerImportTests()
    {
        var options = Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=dbtowners_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", SeedDemoData = false } });
        _repo = GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
        SeedTable(_table);
    }

    public void Dispose() => _repo.Dispose();

    private void SeedTable(TableIdentifier id)
    {
        var table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, Description = "original", Sensitivity = "NORMAL", IsActive = true };
        _repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = id, Table = table, Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INT" }]
        }).GetAwaiter().GetResult();
    }

    private static DbtGovernanceImportCaller Caller(bool governanceAdmin = true, TenantId? tenant = null) =>
        new(new VirtualFilterActor(new Sid(Admin), IsSync: false, ["CORP\\admin", "admin@corp.example"]), tenant ?? Tenant, IsGovernanceAdmin: governanceAdmin);

    private DbtMetadataIngestionService Service(
        bool flag = true, bool ownerRepo = true, IAuditLogRepository? audit = null, IDataOwnerIdentityVerifier? verifier = null, IDataOwnerAssignmentRepository? writer = null)
    {
        var gateway = Options.Create(new GatewayOptions { Dbt = new DbtOptions { ImportDataOwners = flag } });
        return new DbtMetadataIngestionService(
            Substitute.For<IDbtProposalRepository>(), _repo, Substitute.For<ILineageGraphStore>(), null, gateway, null, null, null, null, null, null, null,
            NullLogger<DbtMetadataIngestionService>.Instance,
            audit ?? _repo, writer ?? (ownerRepo ? _repo : null), verifier);
    }

    private static string Payload(TableIdentifier t, string ownersJson, string extra = "") =>
        $$$"""{"classifications":[{"database":"{{{t.Domain}}}","schema":"{{{t.Schema}}}","table":"{{{t.TableName}}}"{{{extra}}},{{{ownersJson}}}}]}""";

    private static string OwnerObj(string sid, string name = "Ada Owner", string email = "ada@corp.example", string account = "CORP\\ada", string role = "primary") =>
        "\"owner\":" + JsonSerializer.Serialize(new Dictionary<string, string> { ["sid"] = sid, ["display_name"] = name, ["email"] = email, ["account"] = account, ["role"] = role });

    private Task<DbtGovernanceSyncResult> Import(DbtMetadataIngestionService service, string json, DbtGovernanceImportCaller? caller = null, bool dryRun = false, bool replace = false)
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return service.IngestGovernanceStreamAsync(stream, caller ?? Caller(), dryRun, replace);
    }

    private int Count(string sql)
    {
        var conn = _repo.RawConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private int OwnerAssignments() => Count("SELECT COUNT(*) FROM TABLE_OWNERS");

    private async Task<string?> DescriptionAsync(TableIdentifier t) => (await _repo.GetTableMetadataAsync(t))!.Table.Description;

    private void SeedOwnerRow(string sid, string account, string email, string name = "Existing")
    {
        var conn = _repo.RawConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO DATA_OWNERS (id, ad_sid, ad_account, display_name, email, is_active) VALUES (@id, @s, @a, @n, @e, 1)";
        foreach (var (k, v) in new[] { ("@id", Guid.NewGuid().ToString()), ("@s", sid), ("@a", account), ("@n", name), ("@e", email) })
        {
            cmd.Parameters.AddWithValue(k, v);
        }

        cmd.ExecuteNonQuery();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_FlagOff_WithOwner_RejectsWholeImport_NoWrites()
    {
        // I393B-T-01 / SC-01
        var result = await Import(Service(flag: false), Payload(_table, OwnerObj("S-1-5-21-5001"), ",\"description\":\"changed\""));

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("dbt_owner_import_disabled");
        OwnerAssignments().ShouldBe(0);
        (await DescriptionAsync(_table)).ShouldBe("original");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Import_FlagOn_MissingWriteOrAuditRepository_RejectsWholeImport(bool withWriter, bool withAudit)
    {
        // I393B-SC-01: fail closed when a dependency is not registered
        var gateway = Options.Create(new GatewayOptions { Dbt = new DbtOptions { ImportDataOwners = true } });
        var service = withAudit
            ? new DbtMetadataIngestionService(
                Substitute.For<IDbtProposalRepository>(), _repo, Substitute.For<ILineageGraphStore>(), null, gateway, null, null, null, null, null, null, null,
                NullLogger<DbtMetadataIngestionService>.Instance, _repo, withWriter ? _repo : null, null)
            : new DbtMetadataIngestionService(
                Substitute.For<IDbtProposalRepository>(), _repo, Substitute.For<ILineageGraphStore>(), null, gateway, null, null, null, null, null, null, null,
                NullLogger<DbtMetadataIngestionService>.Instance);

        var result = await Import(service, Payload(_table, OwnerObj("S-1-5-21-5002"), ",\"description\":\"changed\""));

        result.Success.ShouldBeFalse();
        OwnerAssignments().ShouldBe(0);
        (await DescriptionAsync(_table)).ShouldBe("original");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_DbtAdminOnly_WithFlagOn_RejectsOwnerPath()
    {
        // I393B-T-02 / SC-02: the service re-checks the global governance admin role
        var result = await Import(Service(), Payload(_table, OwnerObj("S-1-5-21-5003"), ",\"description\":\"changed\""), Caller(governanceAdmin: false));

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("dbt_owner_admin_required");
        OwnerAssignments().ShouldBe(0);
        (await DescriptionAsync(_table)).ShouldBe("original");
    }

    [Fact]
    public async Task Import_FlagOn_AssignsOwner_DescribeDatasetShowsDataOwner()
    {
        // Issue acceptance: an imported owner appears in describe_dataset.
        var result = await Import(Service(), Payload(_table, OwnerObj("S-1-5-21-5004", "Grace Hopper", "grace@corp.example")));

        result.Success.ShouldBeTrue();
        result.CreatedDataOwnersCount.ShouldBe(1);
        result.AssignedTableOwnersCount.ShouldBe(1);
        var gatewayOptions = new GatewayOptions();
        var catalogTables = Wp0.Tables(Wp0.Table(_table.Domain, _table.TableName, "Visible", ["id"]));
        var consents = Wp0.Consents(Wp0.Allow(_table, "S-USER", Wp0.TenantA, "id"));
        var mcp = new Autheris.Application.Mcp.Services.McpDatasetCatalog(
            catalogTables, consents, Substitute.For<Autheris.Application.Interfaces.IGatewayExecutionService>(), Substitute.For<IAuditLogRepository>(),
            options: Options.Create(gatewayOptions), dataOwnershipRepository: _repo,
            visibilityService: Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(catalogTables, consents, gatewayOptions, null));
        var description = await mcp.DescribeDatasetAsync(Wp0.User("S-USER", "tenant-a"), $"{_table.Domain}.{_table.Schema}.{_table.TableName}");

        description.DataOwner.ShouldNotBeNull();
        description.DataOwner.ShouldContain("Grace Hopper");
    }

    [Fact]
    public async Task Import_ImportedOwnerCanApprove_NonOwnerAndOtherTableCannot()
    {
        // I393B-T-13 (repository level; the consent tenant check in the approval mutations is unchanged)
        var other = new TableIdentifier("Sales", "Crm", "Other_" + Guid.NewGuid().ToString("N")[..6]);
        SeedTable(other);
        (await Import(Service(), Payload(_table, OwnerObj("S-1-5-21-5005")))).Success.ShouldBeTrue();

        (await _repo.IsAuthorizedApproverForTableAsync(_table, new Sid("S-1-5-21-5005"))).ShouldBeTrue();
        (await _repo.IsAuthorizedApproverForTableAsync(_table, new Sid("S-1-5-21-STRANGER"))).ShouldBeFalse();
        (await _repo.IsAuthorizedApproverForTableAsync(other, new Sid("S-1-5-21-5005"))).ShouldBeFalse();
    }

    [Fact]
    public async Task Import_DryRun_ReportsCountsWritesNothing()
    {
        var audit = Substitute.For<IAuditLogRepository>();
        var result = await Import(Service(audit: audit), Payload(_table, OwnerObj("S-1-5-21-5006"), ",\"description\":\"changed\""), dryRun: true);

        result.Success.ShouldBeTrue();
        result.CreatedDataOwnersCount.ShouldBe(1);
        result.AssignedTableOwnersCount.ShouldBe(1);
        OwnerAssignments().ShouldBe(0);
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(0);
        (await DescriptionAsync(_table)).ShouldBe("original");
        await audit.DidNotReceiveWithAnyArgs().RecordAuditEventsAsync(default!, default);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_InvalidOwner_WritesNoMetadata()
    {
        var result = await Import(Service(), Payload(_table, "\"owner\":{\"sid\":\"system:evil\",\"display_name\":\"X\"}", ",\"description\":\"changed\""));

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("dbt_owner_invalid");
        (await DescriptionAsync(_table)).ShouldBe("original");
        OwnerAssignments().ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("\"owner\":{\"sid\":\"s-1-5-21-admin-1\",\"display_name\":\"X\"}")]
    [InlineData("\"owner\":{\"sid\":\"S-1-5-21-5007\",\"display_name\":\"X\",\"account\":\"corp\\\\ADMIN\"}")]
    [InlineData("\"owner\":{\"sid\":\"S-1-5-21-5007\",\"display_name\":\"X\",\"email\":\"Admin@Corp.Example\"}")]
    [InlineData("\"owner\":{\"sid\":\" S-1-5-21-5007\",\"display_name\":\"X\"}")]
    [InlineData("\"owners\":[\"admin@corp.example\"]")]
    public async Task Import_SelfAssignment_ByCaseOrWhitespaceVariant_Rejects(string ownersJson)
    {
        // I393B-T-06 / SC-05
        var result = await Import(Service(), Payload(_table, ownersJson));

        result.Success.ShouldBeFalse();
        OwnerAssignments().ShouldBe(0);
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_AuditIntentFails_NoOwnerRowsWritten()
    {
        // I393B-T-03 / SC-03: the intent is written before anything is applied; its failure aborts the import
        var audit = Substitute.For<IAuditLogRepository>();
        audit.RecordAuditEventsAsync(Arg.Any<IReadOnlyList<AuditLogEntry>>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("audit down"));

        var result = await Import(Service(audit: audit), Payload(_table, OwnerObj("S-1-5-21-5008"), ",\"description\":\"changed\""));

        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("dbt_owner_audit_failed");
        OwnerAssignments().ShouldBe(0);
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(0);
        (await DescriptionAsync(_table)).ShouldBe("original");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_AuditDetails_ContainNoEmailOrDisplayName()
    {
        // I393B-T-04 / SC-03 / SC-04 (no realm: identity_verified=false)
        var result = await Import(Service(), Payload(_table, OwnerObj("S-1-5-21-5009", "Distinct Display Name", "distinct.mail@corp.example", "CORP\\distinctacct")));
        result.Success.ShouldBeTrue();

        var entries = (await _repo.GetAuditLogEntriesAsync(500)).Where(e => e.EventType is "DATA_OWNER_CREATED" or "TABLE_OWNER_ASSIGNED" or "DBT_OWNER_IMPORT_OUTCOME").ToList();
        entries.Select(e => e.EventType).Distinct().Order().ShouldBe(["DATA_OWNER_CREATED", "DBT_OWNER_IMPORT_OUTCOME", "TABLE_OWNER_ASSIGNED"]);
        foreach (var e in entries)
        {
            var text = e.DetailsJson + e.TargetTable + e.Query;
            text.ShouldNotContain("distinct.mail");
            text.ShouldNotContain("Distinct Display");
            text.ShouldNotContain("distinctacct");
            e.DetailsJson.ShouldContain("dbt-governance-import");
            e.DetailsJson.ShouldContain("\"identity_verified\":false");
        }
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(OwnerIdentityStatus.NotFoundOrInactive)]
    [InlineData(OwnerIdentityStatus.Unavailable)]
    public async Task Import_RealmConfigured_UnknownOrInactivePrincipal_OrStoreUnavailable_Rejects(OwnerIdentityStatus status)
    {
        // I393B-T-05 / SC-04
        var verifier = Substitute.For<IDataOwnerIdentityVerifier>();
        verifier.VerifyAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(status);

        var result = await Import(Service(verifier: verifier), Payload(_table, OwnerObj("S-1-5-21-5010"), ",\"description\":\"changed\""));

        result.Success.ShouldBeFalse();
        OwnerAssignments().ShouldBe(0);
        (await DescriptionAsync(_table)).ShouldBe("original");
    }

    [Fact]
    public async Task Import_RealmConfigured_VerifiedPrincipal_AuditsIdentityVerified()
    {
        var verifier = Substitute.For<IDataOwnerIdentityVerifier>();
        verifier.VerifyAsync(Arg.Any<TenantId>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(OwnerIdentityStatus.Verified);

        (await Import(Service(verifier: verifier), Payload(_table, OwnerObj("S-1-5-21-5011")))).Success.ShouldBeTrue();

        var created = (await _repo.GetAuditLogEntriesAsync(500)).First(e => e.EventType == "DATA_OWNER_CREATED");
        created.DetailsJson.ShouldContain("\"identity_verified\":true");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_ExceedsOwnerCaps_Rejects()
    {
        // I393B-T-08 / SC-06: 5,000 assignments and 1,000 new owners per import
        string Items(int items, int perItem, int offset)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < items; i++)
            {
                var owners = string.Join(",", Enumerable.Range(0, perItem).Select(j => $$"""{"sid":"S-1-5-21-{{offset + i * perItem + j}}","display_name":"N"}"""));
                sb.Append(i > 0 ? "," : "").Append($$"""{"database":"{{_table.Domain}}","schema":"{{_table.Schema}}","table":"{{_table.TableName}}","owners":[{{owners}}]}""");
            }

            return "{\"classifications\":[" + sb + "]}";
        }

        var tooManyAssignments = await Import(Service(), Items(501, 10, 100000));
        tooManyAssignments.Success.ShouldBeFalse();
        tooManyAssignments.ErrorMessage!.ShouldContain("dbt_owner_cap_exceeded");

        var tooManyNew = await Import(Service(), Items(101, 10, 200000));
        tooManyNew.Success.ShouldBeFalse();
        tooManyNew.ErrorMessage!.ShouldContain("dbt_owner_cap_exceeded");
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_ForeignTenantTable_Rejects_NoOwnerLinked()
    {
        // I393B-T-12 / SC-11
        var foreign = new TableIdentifier("OtherTenant", "Crm", "Orders_f");
        SeedTable(foreign);

        var result = await Import(Service(), Payload(foreign, OwnerObj("S-1-5-21-5012")));

        result.Success.ShouldBeFalse();
        OwnerAssignments().ShouldBe(0);
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(0);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_BareStringOwner_AmbiguousOrMissing_Rejects_NeverCreates()
    {
        // I393B-T-11 / SC-09
        var missing = await Import(Service(), Payload(_table, "\"owners\":[\"nobody@corp.example\"]"));
        missing.Success.ShouldBeFalse();
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(0);

        SeedOwnerRow("S-1-5-21-5013", "CORP\\a", "twin@corp.example");
        SeedOwnerRow("S-1-5-21-5014", "CORP\\b", "twin@corp.example");
        var ambiguous = await Import(Service(), Payload(_table, "\"owners\":[\"twin@corp.example\"]"));
        ambiguous.Success.ShouldBeFalse();
        OwnerAssignments().ShouldBe(0);
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(2);

        var unique = await Import(Service(), Payload(_table, "\"owners\":[\"S-1-5-21-5013\"]"));
        unique.Success.ShouldBeTrue();
        OwnerAssignments().ShouldBe(1);
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(2);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Replace_DeletesOnlyDbtPrefixedRows_AndOnlyForTenantAllowedTables()
    {
        // I393B-T-09 / SC-07 / SC-08: stale dbt row goes (audited, removed_all), manual rows and unlisted or foreign tables stay
        var untouched = new TableIdentifier("Sales", "Crm", "Untouched_" + Guid.NewGuid().ToString("N")[..6]);
        SeedTable(untouched);
        (await Import(Service(), Payload(_table, OwnerObj("S-1-5-21-5015")))).Success.ShouldBeTrue();
        (await Import(Service(), Payload(untouched, OwnerObj("S-1-5-21-5015")))).Success.ShouldBeTrue();
        OwnerAssignments().ShouldBe(2);

        var result = await Import(Service(), Payload(_table, "\"owners\":[]"), replace: true);

        result.Success.ShouldBeTrue();
        result.RemovedTableOwnersCount.ShouldBe(1);
        OwnerAssignments().ShouldBe(1); // the unlisted table keeps its dbt row
        var removed = (await _repo.GetAuditLogEntriesAsync(500)).Single(e => e.EventType == "TABLE_OWNER_REMOVED");
        removed.DetailsJson.ShouldContain("\"removed_all\":true");
        removed.TargetTable.ShouldBe(_table.ToString());

        var foreign = new TableIdentifier("OtherTenant", "Crm", "Orders_g");
        SeedTable(foreign);
        (await Import(Service(), Payload(foreign, "\"owners\":[]"), replace: true)).Success.ShouldBeFalse();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Import_ErrorMessages_DoNotEchoIdentifiers()
    {
        // I393B-T-15 / SC-01
        const string sentinel = "S-1-5-21-SENTINEL-Zq91";
        SeedOwnerRow("S-1-5-21-5016", "CORP\\Zq91", "zq91@corp.example");
        var payloads = new[]
        {
            Payload(_table, $"\"owner\":{{\"sid\":\"{sentinel}\",\"display_name\":\"X\",\"email\":\"not-an-email-{sentinel}\"}}"),
            Payload(_table, $"\"owner\":{{\"sid\":\"{sentinel}\",\"display_name\":\"X\",\"account\":\"CORP\\\\ZQ91\"}}"),
            Payload(_table, $"\"owner\":{{\"sid\":\"S-1-5-21-5016\",\"display_name\":\"X\",\"email\":\"changed-{sentinel.ToLowerInvariant()}@corp.example\"}}"),
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
        // I393B-T-14: the manifest sync keeps meta.owner as a suggestion
        var writer = Substitute.For<IDataOwnerAssignmentRepository>();
        var manifest = "{\"metadata\":{\"dbt_version\":\"1.8.0\"},\"nodes\":{\"model.p.m\":{\"name\":\"" + _table.TableName + "\",\"database\":\"" + _table.Domain +
                       "\",\"schema\":\"" + _table.Schema + "\",\"resource_type\":\"model\",\"meta\":{\"owner\":\"S-1-5-21-5017\",\"owner_email\":\"x@corp.example\"},\"columns\":{}}}}";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(manifest));

        await Service(writer: writer).IngestManifestStreamAsync(stream, Caller(), dryRun: false);

        await writer.DidNotReceiveWithAnyArgs().ApplyTableOwnerAssignmentsAsync(default!, default);
        OwnerAssignments().ShouldBe(0);
        Count("SELECT COUNT(*) FROM DATA_OWNERS").ShouldBe(0);
    }
}
