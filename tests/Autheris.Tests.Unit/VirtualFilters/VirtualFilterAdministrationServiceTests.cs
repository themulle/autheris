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
/// cycles, referential checks, audit) and the sync plan/apply from the file repository.
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
        _service = new VirtualFilterAdministrationService(_repository, _audit);
    }

    public void Dispose() => _repository.Dispose();

    private Task AuditedAsync(string eventType) =>
        _audit.Received().RecordAuditEventAsync(Arg.Is<AuditLogEntry>(e => e.EventType == eventType && e.TenantId == Tenant), Arg.Any<CancellationToken>());

    [Fact]
    public async Task CreateFilterAndBinding_AreStoredAndAudited()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), Admin);
        await _service.SaveBindingAsync(VirtualFilterModelTests.DavidBinding(), Admin);

        var snapshot = await _repository.LoadSnapshotAsync();
        snapshot.Filters.ShouldHaveSingleItem();
        snapshot.Bindings.ShouldHaveSingleItem();
        await AuditedAsync("VIRTUAL_FILTER_CREATED");
        await AuditedAsync("FILTER_BINDING_CREATED");
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
                                       e.DetailsJson!.Contains(original.ComputeDefinitionHash()) &&
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
    public async Task Binding_OnUnknownFilter_IsRejected()
    {
        await Should.ThrowAsync<ArgumentException>(() => _service.SaveBindingAsync(VirtualFilterModelTests.DavidBinding("unbekannt"), Admin));
    }

    [Fact]
    public async Task Binding_OnFilterOfAnotherTenant_IsRejected()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { TenantId = new TenantId("other") }, Admin);

        await Should.ThrowAsync<ArgumentException>(() => _service.SaveBindingAsync(VirtualFilterModelTests.DavidBinding(), Admin));
    }

    [Fact]
    public async Task Filter_WithBindings_CannotBeDeleted()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter(), Admin);
        await _service.SaveBindingAsync(VirtualFilterModelTests.DavidBinding(), Admin);

        await Should.ThrowAsync<VirtualFilterConflictException>(() => _service.DeleteFilterAsync(Tenant, "nicht_ausgelieferte_krane", Admin));
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
        var managed = new ManagedBy("governance/access/david.yaml", "c1");
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { ManagedBy = managed }, Sync);
        await _service.SaveBindingAsync(VirtualFilterModelTests.DavidBinding() with { ManagedBy = managed }, Sync);
        var binding = (await _repository.LoadSnapshotAsync()).Bindings.Single();

        await Should.ThrowAsync<ManagedResourceLockedException>(() =>
            _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter() with { ValidToColumn = "crane.date_of_delivery" }, Admin));
        await Should.ThrowAsync<ManagedResourceLockedException>(() => _service.DeleteBindingAsync(Tenant, binding.Id, Admin));
        await Should.ThrowAsync<ManagedResourceLockedException>(() =>
            _service.SaveBindingAsync(binding with { OnUnmatched = OnUnmatched.Skip }, Admin));
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
        items.OfType<FilterBinding>().ToList());

    [Fact]
    public async Task SyncPlan_ListsCreates_ThenApplyCreatesAndAudits()
    {
        var request = Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidBinding());

        var plan = await _service.PlanSyncAsync(request);
        plan.CreateFilters.ShouldBe(["nicht_ausgelieferte_krane"]);
        plan.CreateBindings.Count.ShouldBe(1);
        plan.HasChanges.ShouldBeTrue();

        await _service.ApplySyncAsync(request, Sync);

        var snapshot = await _repository.LoadSnapshotAsync();
        snapshot.Filters.Single().ManagedBy.ShouldBe(new ManagedBy("governance/access", "c1"));
        snapshot.Bindings.Single().ManagedBy.ShouldNotBeNull();
        await AuditedAsync("VIRTUAL_FILTER_SYNC_APPLIED");
        (await _service.PlanSyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidBinding()))).HasChanges.ShouldBeFalse();
    }

    [Fact]
    public async Task SyncPlan_ListsUpdatesAndDeletes_ApplyRemovesWhatTheFilesNoLongerContain()
    {
        await _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter(), VirtualFilterModelTests.DavidFilter("zweiter_filter"), VirtualFilterModelTests.DavidBinding()), Sync);
        var next = Desired("c2", VirtualFilterModelTests.DavidFilter() with { ValidToColumn = "crane.date_of_delivery" }, VirtualFilterModelTests.DavidBinding());

        var plan = await _service.PlanSyncAsync(next);
        plan.UpdateFilters.ShouldBe(["nicht_ausgelieferte_krane"]);
        plan.DeleteFilters.ShouldBe(["zweiter_filter"]);
        plan.UpdateBindings.ShouldBeEmpty();

        await _service.ApplySyncAsync(next, Sync);
        (await _repository.LoadSnapshotAsync()).Filters.Select(f => f.Name).ShouldBe(["nicht_ausgelieferte_krane"]);
    }

    [Fact]
    public async Task SyncPlan_ReportsDrift_WhenAStoredDefinitionNoLongerMatchesItsHash()
    {
        await _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()), Sync);
        // A direct write to the database (outside Autheris) changes the definition without a matching hash.
        using (var cmd = _repository.Connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE VIRTUAL_FILTERS SET definition_hash = 'manipuliert' WHERE name = 'nicht_ausgelieferte_krane';";
            cmd.ExecuteNonQuery().ShouldBe(1);
        }

        var plan = await _service.PlanSyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()));

        plan.DriftedFilters.ShouldBe(["nicht_ausgelieferte_krane"]);
    }

    [Fact]
    public async Task Sync_DoesNotTouchUnmanagedRows_ButRejectsNameClashes()
    {
        await _service.SaveFilterAsync(VirtualFilterModelTests.DavidFilter("handgepflegt"), Admin);

        var plan = await _service.PlanSyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()));
        plan.DeleteFilters.ShouldBeEmpty();

        await Should.ThrowAsync<VirtualFilterConflictException>(() =>
            _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter("handgepflegt")), Sync));
    }

    [Fact]
    public async Task ApplySync_RequiresTheSyncActor()
    {
        await Should.ThrowAsync<ManagedResourceLockedException>(() =>
            _service.ApplySyncAsync(Desired("c1", VirtualFilterModelTests.DavidFilter()), Admin));
    }
}
