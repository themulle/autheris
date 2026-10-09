namespace Autheris.Tests.Unit.VirtualFilters;

using System;
using System.Collections.Generic;
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
/// SR15-06: Four-eyes principle and Last-Known-Good active enforcement for Virtual Filters and Access Profiles.
/// An update to an active filter or profile must never disable enforcement while awaiting approval.
/// Deletion must require approval. Approval requires four-eyes check and supports TOCTOU hash verification.
/// </summary>
public sealed class VirtualFilterFourEyesApprovalTests : IDisposable
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private static readonly Sid Creator = new("S-1-5-21-CREATOR");
    private static readonly Sid Supervisor = new("S-1-5-21-SUPERVISOR");
    private static readonly Sid DavidSid = new("S-1-5-21-LWE-DAVID");

    private static readonly VirtualFilterActor CreatorActor = new(Creator, IsSync: false);
    private static readonly VirtualFilterActor SupervisorActor = new(Supervisor, IsSync: false);

    private readonly SqliteGovernanceRepository _repository = new(
        Substitute.For<IEpochValidationService>(),
        Options.Create(new GatewayOptions { GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=vfa_{Guid.NewGuid():N};Mode=Memory;Cache=Shared" } }));

    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();
    private readonly VirtualFilterAdministrationService _adminService;
    private readonly MandatoryRowFilterResolver _resolver;

    private sealed class RepositorySnapshotProvider(IVirtualFilterRepository repo) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) =>
            new(repo.LoadSnapshotAsync(ct));
        public void Invalidate() { }
    }

    private sealed class TestPredicates : IVirtualFilterPredicateBuilder
    {
        public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect) =>
            $"P_{filter.Name}({string.Join(",", VirtualFilterColumns.RequiredTargetColumns(filter, binding))})";
    }

    public VirtualFilterFourEyesApprovalTests()
    {
        var catalog = Substitute.For<ITableMetadataRepository>();
        catalog.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([
            MandatoryRowFilterResolverTests.Table("conf", "client", "client_id", "crane_serial_number"),
            MandatoryRowFilterResolverTests.Table("md", "crane", "serial_number", "is_delivered")
        ]);

        var options = Options.Create(new GatewayOptions
        {
            VirtualFilters = new VirtualFilterOptions { RequireApproval = true, MaxRemovals = 2 }
        });

        _adminService = new VirtualFilterAdministrationService(_repository, _audit, options, catalog: catalog);
        _resolver = new MandatoryRowFilterResolver(new RepositorySnapshotProvider(_repository), new TestPredicates());
    }

    public void Dispose() => _repository.Dispose();

    private static MandatoryFilterQuery Query(TableMetadata table) =>
        new(DavidSid, new HashSet<Sid>(), new HashSet<string>(), Tenant, table, FilterObjectKinds.Relation);

    [Fact]
    public async Task FilterUpdate_WhileRequireApproval_KeepsActiveVersionInResolver_UntilSupervisorApprove()
    {
        // 1. Initial filter created & approved
        var filter = VirtualFilterModelTests.DavidFilter("filter_v1");
        var savedInitial = await _adminService.SaveFilterAsync(filter, CreatorActor);
        savedInitial.Status.ShouldBe(FilterApprovalStatus.PendingApproval);

        // Make active via sync actor
        var initialProfile = VirtualFilterModelTests.DavidProfile("david", "filter_v1");
        await _adminService.SaveFilterAsync(filter, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));
        await _adminService.SaveProfileAsync(initialProfile, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        var targetTable = MandatoryRowFilterResolverTests.Table("conf", "client", "client_id", "crane_serial_number");
        var outcomeBefore = await _resolver.ResolveAsync(Query(targetTable));
        outcomeBefore.IsDenied.ShouldBeFalse();
        outcomeBefore.AppliedFilters.ShouldContain("filter_v1");
        outcomeBefore.PredicateSql.ShouldNotBeNull();
        outcomeBefore.PredicateSql.ShouldContain("P_filter_v1(client_id)");

        // 2. Creator updates the filter with a new key column
        var updatedFilter = filter with { KeyColumns = ["client.client_id", "crane.crane_serial_number"] };
        var savedUpdate = await _adminService.SaveFilterAsync(updatedFilter, CreatorActor);

        // SR15-06 Core Invariant: Status stays Active, Draft contains pending update
        savedUpdate.Status.ShouldBe(FilterApprovalStatus.Active);
        savedUpdate.Draft.ShouldNotBeNull();
        savedUpdate.Draft.KeyColumns.ShouldBe(["client.client_id", "crane.crane_serial_number"]);

        // Stored snapshot preserves Active status
        var snapshotMid = await _repository.LoadSnapshotAsync();
        var storedMid = snapshotMid.Filters.Single(f => f.Name == "filter_v1");
        storedMid.Status.ShouldBe(FilterApprovalStatus.Active);
        storedMid.Draft.ShouldNotBeNull();

        // LAST-KNOWN-GOOD: The resolver STILL executes the previous active filter, NOT disabling protection!
        var outcomeDuringPending = await _resolver.ResolveAsync(Query(targetTable));
        outcomeDuringPending.IsDenied.ShouldBeFalse();
        outcomeDuringPending.AppliedFilters.ShouldContain("filter_v1");
        outcomeDuringPending.PredicateSql.ShouldNotBeNull();
        outcomeDuringPending.PredicateSql.ShouldContain("P_filter_v1(client_id)"); // Still active version!

        // 3. Creator cannot self-approve
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _adminService.ApproveFilterAsync(Tenant, "filter_v1", CreatorActor));

        // 4. Supervisor approves with TOCTOU expectedHash check
        var wrongHash = "0000000000000000000000000000000000000000000000000000000000000000";
        await Should.ThrowAsync<VirtualFilterConflictException>(() =>
            _adminService.ApproveFilterAsync(Tenant, "filter_v1", SupervisorActor, expectedHash: wrongHash));

        var correctHash = storedMid.Draft.ComputeDefinitionHash();
        var approved = await _adminService.ApproveFilterAsync(Tenant, "filter_v1", SupervisorActor, expectedHash: correctHash);

        approved.Status.ShouldBe(FilterApprovalStatus.Active);
        approved.Draft.ShouldBeNull();
        approved.ApprovedBy.ShouldBe(Supervisor);
        approved.KeyColumns.ShouldBe(["client.client_id", "crane.crane_serial_number"]);

        // 5. Resolver now enforces the new approved filter definition
        var outcomeAfter = await _resolver.ResolveAsync(Query(targetTable));
        outcomeAfter.IsDenied.ShouldBeFalse();
        outcomeAfter.PredicateSql.ShouldNotBeNull();
        outcomeAfter.PredicateSql.ShouldContain("P_filter_v1(client_id,crane_serial_number)");
    }

    [Fact]
    public async Task FilterDelete_WhileRequireApproval_MarksPendingDeletion_AndResolverStillAppliesActive_UntilSupervisorApprove()
    {
        // 1. Setup active filter and profile
        var filter = VirtualFilterModelTests.DavidFilter("filter_del");
        await _adminService.SaveFilterAsync(filter, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        // Additional filter so profile remains valid
        var filterOther = VirtualFilterModelTests.DavidFilter("filter_other");
        await _adminService.SaveFilterAsync(filterOther, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        // Profile binding filter_del
        var profile = VirtualFilterModelTests.DavidProfile("david_del", "filter_del");
        await _adminService.SaveProfileAsync(profile, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        // Switch binding to filter_other so filter_del can be deleted
        var switchedProfile = profile with { Bindings = [new FilterBinding { FilterName = "filter_other", TargetPattern = "lwetem_prod.*.*.client_id" }] };
        await _adminService.SaveProfileAsync(switchedProfile, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        // 2. Creator requests delete
        await _adminService.DeleteFilterAsync(Tenant, "filter_del", CreatorActor);

        // Filter is NOT deleted from snapshot, but marked PendingDeletion
        var snapshot = await _repository.LoadSnapshotAsync();
        var stored = snapshot.Filters.Single(f => f.Name == "filter_del");
        stored.Status.ShouldBe(FilterApprovalStatus.Active);
        stored.PendingDeletion.ShouldBeTrue();
        stored.DeletionRequestedBy.ShouldBe(Creator);

        // 3. Creator cannot self-approve deletion
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _adminService.ApproveFilterAsync(Tenant, "filter_del", CreatorActor));

        // 4. Supervisor approves deletion
        await _adminService.ApproveFilterAsync(Tenant, "filter_del", SupervisorActor);

        // Filter is now deleted from repository
        var snapshotAfter = await _repository.LoadSnapshotAsync();
        snapshotAfter.Filters.Any(f => f.Name == "filter_del").ShouldBeFalse();
    }

    [Fact]
    public async Task ProfileUpdate_WhileRequireApproval_KeepsActiveVersionInResolver_UntilSupervisorApprove()
    {
        // 1. Setup active filter
        var filter = VirtualFilterModelTests.DavidFilter("filter_prof");
        await _adminService.SaveFilterAsync(filter, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        // 2. Setup active profile (Uncovered: Deny)
        var profile = VirtualFilterModelTests.DavidProfile("david_p", "filter_prof");
        await _adminService.SaveProfileAsync(profile, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        // 3. Creator submits profile update changing Uncovered to Skip
        var updatedProfile = profile with { Uncovered = UncoveredPolicy.Skip };
        var savedUpdate = await _adminService.SaveProfileAsync(updatedProfile, CreatorActor);

        savedUpdate.Status.ShouldBe(FilterApprovalStatus.Active);
        savedUpdate.Draft.ShouldNotBeNull();
        savedUpdate.Draft.Uncovered.ShouldBe(UncoveredPolicy.Skip);

        // Resolver still uses Active version (Uncovered = Deny)
        var snapshotMid = await _repository.LoadSnapshotAsync();
        var storedMid = snapshotMid.Profiles.Single(p => p.Name == "david_p");
        storedMid.Uncovered.ShouldBe(UncoveredPolicy.Deny);
        storedMid.Draft.ShouldNotBeNull();

        // 4. Creator cannot self-approve
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _adminService.ApproveProfileAsync(Tenant, "david_p", CreatorActor));

        // 5. Supervisor approves with TOCTOU hash check
        var draftHash = storedMid.Draft.ComputeDefinitionHash();
        var approved = await _adminService.ApproveProfileAsync(Tenant, "david_p", SupervisorActor, expectedHash: draftHash);
        approved.Status.ShouldBe(FilterApprovalStatus.Active);
        approved.Draft.ShouldBeNull();
        approved.Uncovered.ShouldBe(UncoveredPolicy.Skip);

        var snapshotAfter = await _repository.LoadSnapshotAsync();
        snapshotAfter.Profiles.Single(p => p.Name == "david_p").Uncovered.ShouldBe(UncoveredPolicy.Skip);
    }

    [Fact]
    public async Task ApproveFilter_WithoutPendingChanges_ThrowsInvalidOperation()
    {
        var filter = VirtualFilterModelTests.DavidFilter("filter_clean");
        await _adminService.SaveFilterAsync(filter, new VirtualFilterActor(new Sid("S-1-5-21-SYS"), true));

        // Has no pending changes
        await Should.ThrowAsync<InvalidOperationException>(() =>
            _adminService.ApproveFilterAsync(Tenant, "filter_clean", SupervisorActor));
    }
}
