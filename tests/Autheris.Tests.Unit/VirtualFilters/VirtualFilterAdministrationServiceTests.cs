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
        _service = new VirtualFilterAdministrationService(_repository, _audit,
            Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { MaxRemovals = 2 } }));
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
        items.OfType<AccessProfile>().ToList());

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
}
