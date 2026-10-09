namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Virtual filters, phase 2: administration rules (validation, managed rows locked outside the sync, supersedes
/// cycles, referential checks, audit) and the sync from the file repository (one transaction, guard against
/// removing many bindings).
/// </summary>
public sealed class VirtualFilterAdministrationServiceTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private static readonly VirtualFilterActor Admin = new(new Sid("S-1-5-21-ADMIN"), IsSync: false);
    private static readonly VirtualFilterActor Sync = new(new Sid("S-1-5-21-TALOS"), IsSync: true);

    private readonly SqliteGovernanceRepository _repository = new(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=vfs_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly VirtualFilterAdministrationService _service;

    public VirtualFilterAdministrationServiceTests()
    {
        var catalog = Substitute.For<ITableMetadataRepository>();
        catalog.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new System.Collections.Generic.List<TableMetadata>
        {
            MandatoryRowFilterResolverTests.Table("conf", "client", "client_id", "crane_serial_number"),
            MandatoryRowFilterResolverTests.Table("md", "crane", "serial_number", "is_delivered")
        });
        _service = new VirtualFilterAdministrationService(_repository, _audit,
            Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { MaxRemovals = 2 } }), catalog: catalog);
    }

    public void Dispose() => _repository.Dispose();

    private Task AuditedAsync(string eventType) =>
        _audit.Received().RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == eventType && e.TenantId == Tenant), Arg.Any<CancellationToken>());

    [Fact]
    public async Task CreateFilterAndProfile_AreStoredAndAudited()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), Admin);
        await _service.SaveProfileAsync(VirtualFilterModelTests.DavidProfile(), Admin);

        var snapshot = await _repository.LoadSnapshotAsync();
        snapshot.Filters.ShouldHaveSingleItem();
        snapshot.Profiles.ShouldHaveSingleItem();
        await AuditedAsync("VIRTUAL_FILTER_CREATED");
        await AuditedAsync("ACCESS_PROFILE_CREATED");
    }

    [Fact]
    public async Task UpdateFilter_IsAuditedWithHashBeforeAndAfter()
    {
        var original = VirtualFilterModelTests.DavidFilter();
        await _service.SaveFilterAsync(original, Admin);
        var changed = original with { ValidToColumn = "crane.date_of_delivery" };

        await _service.SaveFilterAsync(changed, Admin);

        await _audit.Received().RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "VIRTUAL_FILTER_UPDATED" &&
                                       e.DetailsJson.Contains(original.ComputeDefinitionHash()) &&
                                       e.DetailsJson.Contains(changed.ComputeDefinitionHash())),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidFilter_IsRejectedWithoutWriting()
    {
        await Should.ThrowAsync<ArgumentException>(() => _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter("Bad-Name"), Admin));
        (await _repository.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    public async Task Profile_BindingAnUnknownFilter_IsRejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => _service.SaveProfileAsync(VirtualFilterModelTests.DavidProfile(), Admin));
    }

    [Fact]
    public async Task Profile_BindingAFilterOfAnotherTenant_IsRejected()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { TenantId = new TenantId("other") }, Admin);

        await Should.ThrowAsync<ArgumentException>(() => _service.SaveProfileAsync(VirtualFilterModelTests.DavidProfile(), Admin));
    }

    [Fact]
    public async Task BoundFilter_CannotBeDeleted()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), Admin);
        await _service.SaveProfileAsync(VirtualFilterModelTests.DavidProfile(), Admin);

        var ex = await Should.ThrowAsync<VirtualFilterConflictException>(() => _service.DeleteFilterAsync(Tenant, "nicht_ausgelieferte_krane", Admin));
        ex.Message.ShouldContain("david");
    }

    [Fact]
    public async Task Supersedes_UnknownFilterOrCycle_IsRejected()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter("filter_a"), Admin);
        await Should.ThrowAsync<ArgumentException>(() =>
            _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter("filter_b") with { Supersedes = ["gibt_es_nicht"] }, Admin));

        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter("filter_b") with { Supersedes = ["filter_a"] }, Admin);
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter("filter_a") with { Supersedes = ["filter_b"] }, Admin));
        ex.Message.ShouldContain("cycle");
    }

    [Fact]
    public async Task ManagedRows_AreLocked_OutsideTheSync()
    {
        await _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidProfile()), Sync);

        await Should.ThrowAsync<ManagedResourceLockedException>(() =>
            _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { ValidToColumn = "crane.date_of_delivery" }, Admin));
        await Should.ThrowAsync<ManagedResourceLockedException>(() => _service.DeleteProfileAsync(Tenant, "david", Admin));
        await Should.ThrowAsync<ManagedResourceLockedException>(() =>
            _service.SaveProfileAsync(VirtualFilterModelTests.DavidProfile() with { Uncovered = UncoveredPolicy.Skip }, Admin));
    }

    [Fact]
    public async Task OnlyTheSync_MayCreateManagedRows()
    {
        await Should.ThrowAsync<ManagedResourceLockedException>(() =>
            _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { ManagedBy = new ManagedBy("x.yaml", "c1") }, Admin));
    }

    // ------------------------------------------------------------------ sync

    private static VirtualFilterSyncRequest Desired(string commit, params object[] items) => new(
        Tenant,
        new ManagedBy("governance/access", commit),
        items.OfType<VirtualFilter>().ToList(),
        items.OfType<VirtualFilterAccessProfile>().ToList());

    [Fact]
    public async Task SyncPlan_ListsCreates_ThenApplyCreatesInOneGenerationAndAudits()
    {
        var request = Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidProfile());
        var start = await _repository.GetGenerationAsync();

        var plan = await _service.PlanSyncAsync(request);
        plan.CreateFilters.ShouldBe(["nicht_ausgelieferte_krane"]);
        plan.CreateProfiles.ShouldBe(["david"]);
        plan.HasChanges.ShouldBeTrue();

        await _service.ApplySyncAsync(request, Sync);

        var snapshot = await _repository.LoadSnapshotAsync();
        snapshot.Generation.ShouldBe(start + 1);
        snapshot.Filters.Single().ManagedBy.ShouldBe(new ManagedBy("governance/access", "c1"));
        snapshot.Profiles.Single().ManagedBy.ShouldNotBeNull();
        await AuditedAsync("VIRTUAL_FILTER_SYNC_APPLIED");
        (await _service.PlanSyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidProfile()))).HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncPlan_ListsUpdatesAndDeletes_ApplyRemovesWhatTheFilesNoLongerContain()
    {
        await _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidFilter("zweiter_filter"), VirtualFilterModelTests.DavidProfile()), Sync);
        var next = Desired("c2", VirtualFilterModelTests.DavidFilter() with { ValidToColumn = "crane.date_of_delivery" }, VirtualFilterModelTests.DavidProfile());

        var plan = await _service.PlanSyncAsync(next);
        plan.UpdateFilters.ShouldBe(["nicht_ausgelieferte_krane"]);
        plan.DeleteFilters.ShouldBe(["zweiter_filter"]);
        plan.UpdateProfiles.ShouldBeEmpty();
        plan.RemovedBindings.ShouldBe(0);

        await _service.ApplySyncAsync(next, Sync);
        (await _repository.LoadSnapshotAsync()).Filters.Select(f => f.Name).ShouldBe(["nicht_ausgelieferte_krane"]);
    }

    [Fact]
    public async Task SyncPlan_ReportsDrift_AndApplyRepairsIt()
    {
        await _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()), Sync);
        // A direct write to the database (outside Autheris) leaves a stored hash that does not match the definition.
        using (var cmd = _repository.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE VIRTUAL_FILTERS SET definition_hash = 'manipuliert' WHERE name = 'nicht_ausgelieferte_krane';";
            cmd.ExecuteNonQuery().ShouldBe(1);
        }

        var plan = await _service.PlanSyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()));
        plan.DriftedFilters.ShouldBe(["nicht_ausgelieferte_krane"]);
        plan.UpdateFilters.ShouldBe(["nicht_ausgelieferte_krane"]);

        await _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()), Sync);
        (await _service.PlanSyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()))).DriftedFilters.ShouldBeEmpty();
    }

    [Fact]
    public async Task Sync_DoesNotTouchUnmanagedRows_ButRejectsNameClashes()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter("handgepflegt"), Admin);

        (await _service.PlanSyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()))).DeleteFilters.ShouldBeEmpty();
        await Should.ThrowAsync<VirtualFilterConflictException>(() =>
            _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter("handgepflegt")), Sync));
    }

    [Fact]
    public async Task Sync_InvalidContent_WritesNothing()
    {
        var start = await _repository.GetGenerationAsync();

        await Should.ThrowAsync<ArgumentException>(() =>
            _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidProfile("david", "gibt_es_nicht")), Sync));

        (await _repository.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
        (await _repository.GetGenerationAsync()).ShouldBe(start);
    }

    [Fact]
    public async Task ApplySync_RequiresTheSyncActor()
    {
        await Should.ThrowAsync<ManagedResourceLockedException>(() =>
            _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()), Admin));
    }

    [Fact]
    public async Task RemovingMoreBindingsThanAllowed_NeedsForce()
    {
        var filters = Enumerable.Range(1, 4).Select(i => VirtualFilterModelTests.DavidFilter($"filter_{i}")).ToArray();
        var profile = VirtualFilterModelTests.DavidProfile("david", filters.Select(f => f.Name).ToArray()) with
        {
            Bindings = filters.Select(f => new FilterBinding { FilterName = f.Name }).ToList()
        };
        await _service.ApplySyncAsync(Desired("c1", [.. filters, profile]), Sync);
        var shrunk = profile with { Bindings = [profile.Bindings[0]] };   // removes 3 bindings, limit is 2

        var plan = await _service.PlanSyncAsync(Desired("c2", [.. filters, shrunk]));
        plan.RemovedBindings.ShouldBe(3);
        plan.RequiresForce.ShouldBeTrue();
        await Should.ThrowAsync<VirtualFilterConflictException>(() => _service.ApplySyncAsync(Desired("c2", [.. filters, shrunk]), Sync));
        (await _repository.LoadSnapshotAsync()).Profiles.Single().Bindings.Count.ShouldBe(4);

        await _service.ApplySyncAsync(Desired("c2", [.. filters, shrunk]), Sync, force: true);
        (await _repository.LoadSnapshotAsync()).Profiles.Single().Bindings.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RemovingAllBindings_NeedsForce_EvenBelowTheLimit()
    {
        await _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidProfile()), Sync);

        var plan = await _service.PlanSyncAsync(Desired("c2"));   // an accidentally emptied folder
        plan.RemovedBindings.ShouldBe(1);
        plan.RequiresForce.ShouldBeTrue();
        await Should.ThrowAsync<VirtualFilterConflictException>(() => _service.ApplySyncAsync(Desired("c2"), Sync));
    }

    [Fact]
    public async Task SlidingWindow_SalamiSlicing_AccumulatesRemovalsAndRequiresForce()
    {
        var filters = Enumerable.Range(1, 5).Select(i => VirtualFilterModelTests.DavidFilter($"filter_{i}")).ToArray();
        var profile = VirtualFilterModelTests.DavidProfile("david", filters.Select(f => f.Name).ToArray()) with
        {
            Bindings = filters.Select(f => new FilterBinding { FilterName = f.Name }).ToList()
        };
        // Initial state with 5 bindings
        await _service.ApplySyncAsync(Desired("c1", [.. filters, profile]), Sync);

        // First removal: removes 2 bindings (5 -> 3). MaxRemovals is 2, so 2 <= 2 passes without force.
        var step1 = profile with { Bindings = profile.Bindings.Take(3).ToList() };
        var plan1 = await _service.PlanSyncAsync(Desired("c2", [.. filters, step1]));
        plan1.RequiresForce.ShouldBeFalse();
        await _service.ApplySyncAsync(Desired("c2", [.. filters, step1]), Sync);

        // Second removal in window: removes 1 binding (3 -> 2).
        // 1 alone is <= 2, but accumulated in window is 2 + 1 = 3 > 2!
        var step2 = profile with { Bindings = profile.Bindings.Take(2).ToList() };
        var plan2 = await _service.PlanSyncAsync(Desired("c3", [.. filters, step2]));
        plan2.RequiresForce.ShouldBeTrue();
        await Should.ThrowAsync<VirtualFilterConflictException>(() => _service.ApplySyncAsync(Desired("c3", [.. filters, step2]), Sync));

        // Applying with force succeeds
        await _service.ApplySyncAsync(Desired("c3", [.. filters, step2]), Sync, force: true);
        (await _repository.LoadSnapshotAsync()).Profiles.Single().Bindings.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WeakeningUncoveredPolicy_CountsAsRelaxationTowardsMaxRemovals()
    {
        // Setup 3 profiles with Uncovered = Deny
        var filter = VirtualFilterModelTests.DavidFilter("filter_1");
        var profiles = Enumerable.Range(1, 3).Select(i => new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = $"profile_{i}",
            GranteeSid = new Sid($"S-1-5-21-USER{i}"),
            Scope = "lwetem_prod.*.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings = [new FilterBinding { FilterName = filter.Name }]
        }).ToArray();

        await _service.ApplySyncAsync(Desired("c1", [filter, .. profiles]), Sync);

        // Weaken all 3 profiles from Deny to Skip (3 relaxations > limit 2)
        var weakenedProfiles = profiles.Select(p => p with { Uncovered = UncoveredPolicy.Skip }).ToArray();
        var plan = await _service.PlanSyncAsync(Desired("c2", [filter, .. weakenedProfiles]));
        plan.RequiresForce.ShouldBeTrue();
        await Should.ThrowAsync<VirtualFilterConflictException>(() => _service.ApplySyncAsync(Desired("c2", [filter, .. weakenedProfiles]), Sync));
    }

    // ------------------------------------------------------------------ sql definitions (phase 7)

    private static VirtualFilter SqlFilter(string sql) => new()
    {
        TenantId = Tenant,
        Name = "letzter_tag",
        Source = "lwetem_prod",
        Sql = sql
    };

    [Fact]
    public async Task SqlFilter_IsValidatedAndStoredWithItsTargetColumns()
    {
        await _service.SaveFilterAsync(SqlFilter("from conf.client client where target.client_id = client.client_id and target.ts > date_add('day', -1, current_timestamp)"), Admin);

        var stored = (await _repository.LoadSnapshotAsync()).Filters.ShouldHaveSingleItem();
        stored.Sql.ShouldNotBeNull();
        stored.SqlTargetColumns.ShouldBe(["client_id", "ts"], ignoreOrder: true);
        stored.StoredDefinitionHash.ShouldBe(stored.ComputeDefinitionHash());
    }

    [Fact]
    public async Task InvalidSqlFilter_IsRejected_AlsoInTheSync()
    {
        var invalid = SqlFilter("from conf.secrets s where target.client_id = s.client_id");

        await Should.ThrowAsync<ArgumentException>(() => _service.SaveFilterAsync(invalid, Admin));
        await Should.ThrowAsync<ArgumentException>(() => _service.ApplySyncAsync(Desired("c1", invalid), Sync));
        (await _repository.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
    }

    [Fact]
    public async Task SaveFilter_WithRequireApproval_RequiresDistinctApprover()
    {
        var serviceWithApproval = new VirtualFilterAdministrationService(_repository, _audit,
            Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = true } }));

        var creatorActor = new VirtualFilterActor(new Sid("S-1-5-21-CREATOR"), IsSync: false);
        var approverActor = new VirtualFilterActor(new Sid("S-1-5-21-APPROVER"), IsSync: false);

        var filter = VirtualFilterModelTests.DavidFilter();
        var saved = await serviceWithApproval.SaveFilterAsync(filter, creatorActor);
        saved.Status.ShouldBe(FilterApprovalStatus.PendingApproval);
        saved.ApprovedBy.ShouldBeNull();

        // Self-approval must fail (four-eyes principle)
        await Should.ThrowAsync<InvalidOperationException>(() =>
            serviceWithApproval.ApproveFilterAsync(filter.TenantId, filter.Name, creatorActor));

        // Distinct approver succeeds
        var approved = await serviceWithApproval.ApproveFilterAsync(filter.TenantId, filter.Name, approverActor);
        approved.Status.ShouldBe(FilterApprovalStatus.Active);
        approved.ApprovedBy.ShouldBe(approverActor.Sid);
    }

    [Fact]
    public async Task SaveFilter_WithRequireApproval_SyncActor_BypassesLocalApproval()
    {
        var serviceWithApproval = new VirtualFilterAdministrationService(_repository, _audit,
            Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = true } }));

        var result = await serviceWithApproval.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()), Sync);
        result.ShouldNotBeNull();
        (await _repository.LoadSnapshotAsync()).Filters.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task SaveFilter_StructuredFilter_ReferencingUnknownCatalogTable_IsRejected()
    {
        var filter = VirtualFilterModelTests.DavidFilter() with
        {
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("lwetem_prod", "conf", "unknown_table"),
                FromAlias = "client",
                Joins = [],
                Where = []
            }
        };

        var ex = await Should.ThrowAsync<ArgumentException>(() => _service.SaveFilterAsync(filter, Admin));
        ex.Message.ShouldContain("unknown in the catalog");
    }

    [Fact]
    public async Task SaveFilter_WhenAllowedReferenceTablesSpecified_ReferencingUnallowedTable_IsRejected()
    {
        var catalog = Substitute.For<ITableMetadataRepository>();
        catalog.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new System.Collections.Generic.List<TableMetadata>
        {
            MandatoryRowFilterResolverTests.Table("conf", "client", "client_id", "crane_serial_number"),
            MandatoryRowFilterResolverTests.Table("md", "crane", "serial_number", "is_delivered")
        });

        // Allowlist only permits conf.client, not md.crane
        var serviceWithAllowlist = new VirtualFilterAdministrationService(_repository, _audit,
            Options.Create(new GatewayOptions
            {
                VirtualFilters = new VirtualFilterOptions
                {
                    AllowedReferenceTables = ["conf.client"]
                }
            }), catalog: catalog);

        // DavidFilter joins md.crane
        var filter = VirtualFilterModelTests.DavidFilter();

        var ex = await Should.ThrowAsync<ArgumentException>(() => serviceWithAllowlist.SaveFilterAsync(filter, Admin));
        ex.Message.ShouldContain("not in the allowed reference tables list");
    }

    [Fact]
    public void StructuredFilter_WithReservedAutherisTargetAlias_IsRejectedDuringValidation()
    {
        var filter = VirtualFilterModelTests.DavidFilter() with
        {
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("lwetem_prod", "conf", "client"),
                FromAlias = "autheris_target",
                Joins = [],
                Where = []
            }
        };

        var ex = Should.Throw<ArgumentException>(() => filter.Validate());
        ex.Message.ShouldContain("reserved for the protected object");
    }
}

