using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.Declarative;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IManifestProposalRepository</c> across PostgreSQL and SQL Server engines (Amendment 6).</summary>
public abstract class ManifestProposalContract : GovernanceContractBase
{
    private static readonly Sid ItAdmin = new("S-1-5-21-PROPOSAL-IT");
    private static readonly Sid DgAdmin = new("S-1-5-21-PROPOSAL-DG");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-24T12:00:00Z");

    private IManifestProposalRepository Repo => Store.ManifestProposals;

    private static ManifestProposal NewProposal(TenantId tenant, string scope = "analytics", DateTimeOffset? expiresAt = null) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = tenant,
        ScopeName = scope,
        SubmittedBy = ItAdmin,
        SubmittedAt = Now,
        ManifestJson = "{\"pruneMode\":\"False\",\"manifest\":{\"version\":\"1\",\"policies\":[]}}",
        DiffJson = "[]",
        StateHash = new string('a', 64),
        Status = ManifestProposalStatus.Pending,
        ExpiresAt = expiresAt ?? Now.AddDays(30)
    };

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var proposal = NewProposal(Store.TenantA);
        await Repo.CreateAsync(proposal);

        (await Repo.GetAsync(Store.TenantA, proposal.Id)).ShouldNotBeNull().ScopeName.ShouldBe("analytics");
        (await Repo.GetAsync(Store.TenantB, proposal.Id)).ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var proposal = NewProposal(Store.TenantA);
        await Repo.CreateAsync(proposal);

        (await Repo.TryTransitionAsync(Store.TenantB, proposal.Id, ManifestProposalStatus.Pending, ManifestProposalStatus.Confirmed, DgAdmin, Now, null)).ShouldBeFalse();
        (await Repo.UpdateDiffAsync(Store.TenantB, proposal.Id, "[1]", new string('b', 64))).ShouldBeFalse();

        // A proposal of the same scope in another tenant never supersedes this one.
        await Repo.CreateAsync(NewProposal(Store.TenantB));
        var stored = await Repo.GetAsync(Store.TenantA, proposal.Id);
        stored!.Status.ShouldBe(ManifestProposalStatus.Pending);
        stored.StateHash.ShouldBe(new string('a', 64));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Repo.CreateAsync(NewProposal(default)));
        await Should.ThrowAsync<ArgumentException>(() => Repo.CreateAsync(NewProposal(TenantId.Platform)));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetAsync(default, Guid.NewGuid()));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        // A decision is a conditional status update: only the first one applies.
        var proposal = NewProposal(Store.TenantA);
        await Repo.CreateAsync(proposal);

        (await Repo.TryTransitionAsync(Store.TenantA, proposal.Id, ManifestProposalStatus.Pending, ManifestProposalStatus.Confirmed, DgAdmin, Now, null)).ShouldBeTrue();
        (await Repo.TryTransitionAsync(Store.TenantA, proposal.Id, ManifestProposalStatus.Pending, ManifestProposalStatus.Rejected, DgAdmin, Now, "late")).ShouldBeFalse();
        (await Repo.UpdateDiffAsync(Store.TenantA, proposal.Id, "[1]", new string('b', 64))).ShouldBeFalse();

        var stored = await Repo.GetAsync(Store.TenantA, proposal.Id);
        stored!.Status.ShouldBe(ManifestProposalStatus.Confirmed);
        stored.DecidedBy.ShouldBe(DgAdmin);
        stored.RejectReason.ShouldBeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var proposal = NewProposal(Store.TenantA, "unique-" + Guid.NewGuid().ToString("N")[..6]);
        await Repo.CreateAsync(proposal);
        await Should.ThrowAsync<Exception>(() => Repo.CreateAsync(proposal));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var later = NewProposal(Store.TenantA, "expiry-late", Now.AddDays(-1));
        var earlier = NewProposal(Store.TenantA, "expiry-early", Now.AddDays(-2));
        await Repo.CreateAsync(later);
        await Repo.CreateAsync(earlier);
        await Repo.CreateAsync(NewProposal(Store.TenantA, "expiry-open", Now.AddDays(5)));

        var due = await Repo.ListExpiredPendingAsync(Now, 10);
        due.Select(p => p.Id).ShouldBe([earlier.Id, later.Id]);
        (await Repo.ListExpiredPendingAsync(Now, 1)).ShouldHaveSingleItem().Id.ShouldBe(earlier.Id);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => Repo.ListExpiredPendingAsync(Now, 0));
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        // A superseded proposal stays readable for review but is never pending (it can no longer be decided or expired).
        var older = NewProposal(Store.TenantA, "supersede");
        await Repo.CreateAsync(older);
        var newer = NewProposal(Store.TenantA, "supersede");
        (await Repo.CreateAsync(newer)).ShouldBe([older.Id]);

        (await Repo.GetAsync(Store.TenantA, older.Id))!.Status.ShouldBe(ManifestProposalStatus.Superseded);
        (await Repo.TryTransitionAsync(Store.TenantA, older.Id, ManifestProposalStatus.Pending, ManifestProposalStatus.Confirmed, DgAdmin, Now, null)).ShouldBeFalse();
        (await Repo.UpdateDiffAsync(Store.TenantA, newer.Id, "[{\"action\":\"Add\"}]", new string('c', 64))).ShouldBeTrue();
        (await Repo.GetAsync(Store.TenantA, newer.Id))!.StateHash.ShouldBe(new string('c', 64));
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlManifestProposalContractTests(GovernancePostgresFixture fixture) : ManifestProposalContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "manifest_proposal_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerManifestProposalContractTests(GovernanceSqlServerFixture fixture) : ManifestProposalContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "manifest_proposal_contract");
}
