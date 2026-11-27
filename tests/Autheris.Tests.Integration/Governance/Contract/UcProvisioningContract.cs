using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Provisioning;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Contract suite for <c>IUcProvisioningStore</c> across PostgreSQL and SQL Server (issue #1122): the desired-state read returns
/// only non-revoked table consents of the tenant on the data source; the ledger is keyed per data source, securable, principal
/// external id and privilege; feedback rows are tenant scoped; every write commits atomically with its audit entries.
/// </summary>
public abstract class UcProvisioningContract : GovernanceContractBase
{
    private static readonly Sid Owner = new("S-1-5-21-910-1");
    private static readonly Sid Grantee = new("S-1-5-21-910-4");
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-27T10:00:00Z");

    private IUcProvisioningStore Repo => Store.UcProvisioning;

    private static string NewSource() => "uc" + Guid.NewGuid().ToString("N")[..10];

    private async Task<TableMetadata> SeedTableAsync(string source, string name)
    {
        var id = new TableIdentifier(source, "sales", name);
        var table = new TableMetadata
        {
            Identifier = id,
            ReplaceKeyDefinition = true,
            Table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
        var saved = await Store.TableMetadata.UpsertTableMetadataAsync(table);
        await OpenDomainSeeder.AssignToOpenDomainAsync(Store.Services, Store.TenantA, id, Owner);
        await OpenDomainSeeder.AssignToOpenDomainAsync(Store.Services, Store.TenantB, id, Owner);
        return saved;
    }

    private Task<Consent> GrantAsync(TableIdentifier table, TenantId tenant) =>
        Store.Consents.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = Grantee,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-10),
            ValidTo = DateTimeOffset.UtcNow.AddDays(10),
            TenantId = tenant
        });

    private static UcLedgerEntry Entry(string source, string table = "orders", string principalId = "8b0c4b5e-6f0e-4a63-9a5e-1b2a4f9d7c11", UcLedgerState state = UcLedgerState.Pending) => new()
    {
        DataSource = source,
        SecurableType = UcSecurableType.Table,
        FullName = "main.sales." + table,
        Principal = "analysts",
        PrincipalExternalId = principalId,
        Privilege = UcPrivileges.Select,
        State = state,
        CreatedAt = Now,
        UpdatedAt = Now
    };

    private static UcGrantFeedback Feedback(TenantId tenant, Guid consentId, string source, UcGrantState state = UcGrantState.Pending, string? reason = null) =>
        new(tenant, consentId, source, state, reason, Now, state == UcGrantState.Provisioned ? Now : null);

    private static AuditLogEntry Audit(TenantId tenant, string target, string? traceId = null) => new()
    {
        TenantId = tenant,
        OccurredAt = Now,
        EventType = AuditEventTypes.PolicyChanged,
        ActorSid = new Sid("system:uc-provisioning"),
        TargetTable = target,
        Decision = "APPLIED",
        TraceId = traceId ?? Guid.NewGuid().ToString("N"),
        DetailsJson = "{}"
    };

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        var table = await SeedTableAsync(source, "orders");
        var consentA = await GrantAsync(table.Identifier, Store.TenantA);
        await Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, consentA.Id, source, UcGrantState.Provisioned)], [], CancellationToken.None);

        (await Repo.ListTableConsentsAsync(Store.TenantA, source, DateTimeOffset.UtcNow, CancellationToken.None)).Select(c => c.Id).ShouldBe([consentA.Id]);
        (await Repo.ListTableConsentsAsync(Store.TenantB, source, DateTimeOffset.UtcNow, CancellationToken.None)).ShouldBeEmpty();
        (await Repo.ListFeedbackAsync(Store.TenantB, source, CancellationToken.None)).ShouldBeEmpty();
        (await Repo.GetFeedbackAsync(Store.TenantB, [consentA.Id], CancellationToken.None)).ShouldBeEmpty();
        (await Repo.GetFeedbackAsync(Store.TenantA, [consentA.Id], CancellationToken.None))[consentA.Id].State.ShouldBe(UcGrantState.Provisioned);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        var consentId = Guid.NewGuid();
        await Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, consentId, source, UcGrantState.Provisioned)], [], CancellationToken.None);

        await Should.ThrowAsync<ArgumentException>(() =>
            Repo.SaveFeedbackAsync(Store.TenantB, source, 1, [Feedback(Store.TenantA, consentId, source, UcGrantState.Failed)], [], CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() =>
            Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, consentId, source)], [Audit(Store.TenantB, "uc")], CancellationToken.None));

        // The same consent id under another tenant is another row.
        await Repo.SaveFeedbackAsync(Store.TenantB, source, 1, [Feedback(Store.TenantB, consentId, source, UcGrantState.Failed, "UC_FORBIDDEN")], [], CancellationToken.None);
        (await Repo.GetFeedbackAsync(Store.TenantA, [consentId], CancellationToken.None))[consentId].State.ShouldBe(UcGrantState.Provisioned);
        (await Repo.GetFeedbackAsync(Store.TenantB, [consentId], CancellationToken.None))[consentId].ReasonCode.ShouldBe("UC_FORBIDDEN");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        await Should.ThrowAsync<ArgumentException>(() => Repo.ListTableConsentsAsync(default, "dbx", DateTimeOffset.UtcNow, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ListTableConsentsAsync(TenantId.Platform, "dbx", DateTimeOffset.UtcNow, CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => Repo.ListFeedbackAsync(default, "dbx", CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => Repo.GetFeedbackAsync(TenantId.Platform, [Guid.NewGuid()], CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() => Repo.SaveFeedbackAsync(default, "dbx", 1, [], [], CancellationToken.None));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        var pending = Entry(source);
        await Repo.SaveLedgerAsync(source, 1, [pending], [Audit(Store.TenantA, "uc:" + source)], CancellationToken.None);
        var applied = pending with { State = UcLedgerState.Applied, AppliedAt = Now, VerifiedAt = Now, UpdatedAt = Now.AddMinutes(1) };
        await Repo.SaveLedgerAsync(source, 1, [applied], [], CancellationToken.None);
        await Repo.SaveLedgerAsync(source, 1, [applied], [], CancellationToken.None);

        var stored = (await Repo.ListLedgerAsync(source, CancellationToken.None)).ShouldHaveSingleItem();
        stored.Id.ShouldBe(pending.Id);
        stored.State.ShouldBe(UcLedgerState.Applied);
        stored.VerifiedAt.ShouldBe(Now);

        var consentId = Guid.NewGuid();
        await Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, consentId, source)], [], CancellationToken.None);
        await Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, consentId, source, UcGrantState.Provisioned)], [], CancellationToken.None);
        (await Repo.ListFeedbackAsync(Store.TenantA, source, CancellationToken.None)).ShouldHaveSingleItem().State.ShouldBe(UcGrantState.Provisioned);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        // The ledger has no conflicting inserts: it is keyed by privilege, not by row id, so a second entry for the same privilege
        // replaces the first (upsert by key). Concurrent writers are serialised by the fence row instead.
        var source = NewSource();
        var first = Entry(source);
        await Repo.SaveLedgerAsync(source, 1, [first], [], CancellationToken.None);
        await Repo.SaveLedgerAsync(source, 1, [Entry(source, state: UcLedgerState.RevokePending) with { Principal = "analysts-renamed" }], [], CancellationToken.None);

        var stored = (await Repo.ListLedgerAsync(source, CancellationToken.None)).ShouldHaveSingleItem();
        stored.Id.ShouldBe(first.Id);
        stored.State.ShouldBe(UcLedgerState.RevokePending);
        stored.Principal.ShouldBe("analysts-renamed");

        // Another principal external id is another privilege.
        await Repo.SaveLedgerAsync(source, 1, [Entry(source, principalId: "0f9d7c11-1b2a-4f9d-9a5e-8b0c4b5e6f0e")], [], CancellationToken.None);
        (await Repo.ListLedgerAsync(source, CancellationToken.None)).Count.ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        await Repo.SaveLedgerAsync(source, 1, [Entry(source, "zeta"), Entry(source, "alpha"), Entry(source, "mid") with { SecurableType = UcSecurableType.Schema, FullName = "main.sales", Privilege = UcPrivileges.UseSchema }], [], CancellationToken.None);

        (await Repo.ListLedgerAsync(source, CancellationToken.None)).Select(e => e.FullName).ShouldBe(["main.sales", "main.sales.alpha", "main.sales.zeta"]);
        (await Repo.ListLedgerDataSourcesAsync(CancellationToken.None)).ShouldContain(source);

        var t1 = await SeedTableAsync(source, "t1");
        var t2 = await SeedTableAsync(source, "t2");
        var c1 = await GrantAsync(t1.Identifier, Store.TenantA);
        var c2 = await GrantAsync(t2.Identifier, Store.TenantA);
        var other = await SeedTableAsync(NewSource(), "elsewhere");
        _ = await GrantAsync(other.Identifier, Store.TenantA);

        var listed = await Repo.ListTableConsentsAsync(Store.TenantA, source.ToUpperInvariant(), DateTimeOffset.UtcNow, CancellationToken.None);
        listed.Select(c => c.Id).OrderBy(id => id.ToString(), StringComparer.Ordinal).ShouldBe(new[] { c1.Id, c2.Id }.OrderBy(id => id.ToString(), StringComparer.Ordinal));
        listed.ShouldAllBe(c => c.GrantKind == PermissionGrantKind.Data && !c.IsRevoked);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        // Feedback lookups above the batch size are split into batches, never truncated.
        var source = NewSource();
        var ids = Enumerable.Range(0, UcProvisioningStore.ConsentPageSize * 2 + 3).Select(_ => Guid.NewGuid()).ToList();
        await Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, ids[0], source), Feedback(Store.TenantA, ids[^1], source)], [], CancellationToken.None);

        var found = await Repo.GetFeedbackAsync(Store.TenantA, ids, CancellationToken.None);
        found.Keys.ShouldBe(new[] { ids[0], ids[^1] }, ignoreOrder: true);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        var table = await SeedTableAsync(source, "orders");
        var kept = await GrantAsync(table.Identifier, Store.TenantA);
        var revoked = await GrantAsync(table.Identifier, Store.TenantA);
        Store.SetCaller(new CallerContext("contract-test", "1.0", "revoke"));
        var authority = Autheris.Domain.Security.ConsentRevocationAuthority.FromPrincipal(new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
            [
                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.PrimarySid, Grantee.Value),
                new System.Security.Claims.Claim("tenant_id", Store.TenantA.Value)
            ], "test")));
        await Store.Consents.RevokeConsentAsync(revoked.Id, Grantee, "revoked in contract test", authority);

        (await Repo.ListTableConsentsAsync(Store.TenantA, source, DateTimeOffset.UtcNow, CancellationToken.None)).Select(c => c.Id).ShouldBe([kept.Id]);

        var entry = Entry(source);
        await Repo.SaveLedgerAsync(source, 1, [entry], [], CancellationToken.None);
        await Repo.DeleteLedgerAsync(source, 1, [entry], [Audit(Store.TenantA, "uc:" + source)], CancellationToken.None);
        (await Repo.ListLedgerAsync(source, CancellationToken.None)).ShouldBeEmpty();
        (await Repo.ListLedgerDataSourcesAsync(CancellationToken.None)).ShouldNotContain(source);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Failed_audit_write_rolls_the_ledger_change_back()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        var entry = Entry(source);
        await Repo.SaveLedgerAsync(source, 1, [entry], [], CancellationToken.None);

        // A NUL character (PostgreSQL) in an over-long trace id (SQL Server NVARCHAR(128)) makes the audit insert fail on both engines.
        var broken = Audit(Store.TenantA, "uc:broken", traceId: "\0" + new string('x', 300));
        await Should.ThrowAsync<Exception>(() => Repo.DeleteLedgerAsync(source, 1, [entry], [broken], CancellationToken.None));
        await Should.ThrowAsync<Exception>(() => Repo.SaveLedgerAsync(source, 1, [Entry(source, "other")], [broken], CancellationToken.None));

        (await Repo.ListLedgerAsync(source, CancellationToken.None)).ShouldHaveSingleItem().FullName.ShouldBe("main.sales.orders");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Writer_with_an_older_fencing_token_is_rejected()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        await Repo.SaveLedgerAsync(source, 7, [Entry(source)], [], CancellationToken.None);
        await Repo.SaveLedgerAsync(source, 7, [Entry(source) with { State = UcLedgerState.Applied }], [], CancellationToken.None);

        await Should.ThrowAsync<Autheris.Application.State.FencingTokenRejectedException>(() =>
            Repo.DeleteLedgerAsync(source, 6, [Entry(source)], [], CancellationToken.None));
        await Should.ThrowAsync<Autheris.Application.State.FencingTokenRejectedException>(() =>
            Repo.SaveFeedbackAsync(Store.TenantA, source, 6, [Feedback(Store.TenantA, Guid.NewGuid(), source)], [], CancellationToken.None));

        (await Repo.ListLedgerAsync(source, CancellationToken.None)).ShouldHaveSingleItem().State.ShouldBe(UcLedgerState.Applied);
        await Repo.SaveLedgerAsync(source, 8, [Entry(source, "other")], [], CancellationToken.None);
        (await Repo.ListLedgerAsync(source, CancellationToken.None)).Count.ShouldBe(2);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Desired_state_read_crosses_page_boundaries_without_losing_consents()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        var granted = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            var table = await SeedTableAsync(source, "t" + i);
            granted.Add((await GrantAsync(table.Identifier, Store.TenantA)).Id);
        }

        var repository = (GovernanceRepository)Store.Services.GetService(typeof(Autheris.Application.Common.Interfaces.IGovernanceRepository))!;
        var dialect = (Autheris.Infrastructure.Persistence.Dialects.IGovernanceSqlDialect)Store.Services.GetService(typeof(Autheris.Infrastructure.Persistence.Dialects.IGovernanceSqlDialect))!;
        var paged = new UcProvisioningStore(repository, dialect, consentPageSize: 2);

        var listed = await paged.ListTableConsentsAsync(Store.TenantA, source, DateTimeOffset.UtcNow, CancellationToken.None);
        listed.Select(c => c.Id).OrderBy(id => id).ShouldBe(granted.OrderBy(id => id));
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Failed_audit_write_rolls_the_feedback_change_back()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        var consentId = Guid.NewGuid();
        await Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, consentId, source, UcGrantState.Provisioned)], [], CancellationToken.None);

        var broken = Audit(Store.TenantA, "uc:broken", traceId: "\0" + new string('x', 300));
        await Should.ThrowAsync<Exception>(() =>
            Repo.SaveFeedbackAsync(Store.TenantA, source, 1, [Feedback(Store.TenantA, consentId, source, UcGrantState.Failed, "UC_FORBIDDEN")], [broken], CancellationToken.None));

        (await Repo.GetFeedbackAsync(Store.TenantA, [consentId], CancellationToken.None))[consentId].State.ShouldBe(UcGrantState.Provisioned);
    }

    [RequiresContainerRuntimeFact]
    public async Task Ledger_rejects_over_long_fields_before_writing()
    {
        if (!Store.IsAvailable) return;

        var source = NewSource();
        await Should.ThrowAsync<ArgumentException>(() =>
            Repo.SaveLedgerAsync(source, 1, [Entry(source) with { FullName = new string('a', UcSecurableNames.MaxFullNameLength + 1) }], [], CancellationToken.None));
        await Should.ThrowAsync<ArgumentException>(() =>
            Repo.SaveLedgerAsync(source, 1, [Entry(source) with { LastErrorCode = new string('E', 65) }], [], CancellationToken.None));
        (await Repo.ListLedgerAsync(source, CancellationToken.None)).ShouldBeEmpty();
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlUcProvisioningContractTests(GovernancePostgresFixture fixture) : UcProvisioningContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "uc_provisioning_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerUcProvisioningContractTests(GovernanceSqlServerFixture fixture) : UcProvisioningContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "uc_provisioning_contract");
}
