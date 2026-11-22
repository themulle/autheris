namespace Autheris.Tests.Unit.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security.Totp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed class AccessPlanningAtomicityTests
{
    private const string AdminSid = "S-1-5-21-ADMIN-500";
    private const string Tenant = "tenant-a";
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ApplyAccessAsync_MultiGrantPlan_PersistsAllGrantsAndAuditAtomically()
    {
        var consentRepo = new AtomicTrackingConsentRepository();
        var auditRepo = new InMemoryAuditLogRepository();
        var tableRepo = new MutableTableRepo(OrdersTable());
        var store = new InMemoryAccessPlanStore(new FakeTimeProvider(FixedNow));
        var totp = new TotpVerificationService();
        var totpSecrets = new InMemoryTotpSecretStore();
        var service = CreateService(consentRepo, auditRepo, tableRepo, store, totp, totpSecrets);

        var request = new AdminPlanAccessRequest(
            "sales.public.orders",
            [
                new PrincipalAccessGrant("S-1-5-21-ALICE", new Dictionary<string, string> { ["id"] = "clear" }, null, FixedNow.AddDays(14)),
                new PrincipalAccessGrant("S-1-5-21-BOB", new Dictionary<string, string> { ["id"] = "clear" }, null, FixedNow.AddDays(14))
            ],
            "Team report");

        var plan = await service.PlanAccessAsync(request, AdminSid, Tenant);
        var token = await ConfirmAsync(service, totp, totpSecrets, plan.PlanId);

        var result = await service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminSid, new TenantId(Tenant));

        result.Success.ShouldBeTrue();
        result.AppliedGrantCount.ShouldBe(2);
        consentRepo.ReplaceCalls.ShouldBe(1);
        consentRepo.DirectCreateCalls.ShouldBe(0);
        consentRepo.Consents.Count.ShouldBe(2);
        consentRepo.ReplaceAuditEntries.Count.ShouldBe(1);
        consentRepo.ReplaceAuditEntries[0].DetailsJson.ShouldContain("\"appliedGrantCount\":2");
    }

    [Fact]
    public async Task ApplyAccessAsync_WhenConsentWriteFailsMidWay_LeavesZeroGrantsAndZeroAudit()
    {
        var consentRepo = new AtomicTrackingConsentRepository { FailOnSecondDirectCreate = true, FailReplace = true };
        var auditRepo = new InMemoryAuditLogRepository();
        var tableRepo = new MutableTableRepo(OrdersTable());
        var store = new InMemoryAccessPlanStore(new FakeTimeProvider(FixedNow));
        var totp = new TotpVerificationService();
        var totpSecrets = new InMemoryTotpSecretStore();
        var service = CreateService(consentRepo, auditRepo, tableRepo, store, totp, totpSecrets);

        var request = new AdminPlanAccessRequest(
            "sales.public.orders",
            [
                new PrincipalAccessGrant("S-1-5-21-ALICE", new Dictionary<string, string> { ["id"] = "clear" }, null, FixedNow.AddDays(14)),
                new PrincipalAccessGrant("S-1-5-21-BOB", new Dictionary<string, string> { ["id"] = "clear" }, null, FixedNow.AddDays(14))
            ],
            "Team report");

        var plan = await service.PlanAccessAsync(request, AdminSid, Tenant);
        var token = await ConfirmAsync(service, totp, totpSecrets, plan.PlanId);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminSid, new TenantId(Tenant)));

        consentRepo.Consents.ShouldBeEmpty();
        consentRepo.ReplaceAuditEntries.ShouldBeEmpty();
        auditRepo.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task ApplyAccessAsync_WhenPreValidationFails_DoesNotConsumeConfirmationToken()
    {
        var consentRepo = new AtomicTrackingConsentRepository();
        var auditRepo = new InMemoryAuditLogRepository();
        var orders = OrdersTable();
        var tableRepo = new MutableTableRepo(orders);
        var store = new InMemoryAccessPlanStore(new FakeTimeProvider(FixedNow));
        var totp = new TotpVerificationService();
        var totpSecrets = new InMemoryTotpSecretStore();
        var service = CreateService(consentRepo, auditRepo, tableRepo, store, totp, totpSecrets);

        var request = new AdminPlanAccessRequest(
            "sales.public.orders",
            [new PrincipalAccessGrant("S-1-5-21-ALICE", new Dictionary<string, string> { ["id"] = "clear" }, null, FixedNow.AddDays(14))],
            "Team report");

        var plan = await service.PlanAccessAsync(request, AdminSid, Tenant);
        var token = await ConfirmAsync(service, totp, totpSecrets, plan.PlanId);

        // Simulate missing table at apply time (or pre-validation failure before persistence).
        tableRepo.Current = null;
        await Should.ThrowAsync<SecurityException>(() =>
            service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminSid, new TenantId(Tenant)));

        service.ConsumedTokenCount.ShouldBe(0);

        // Once metadata is available again, the unconsumed token still succeeds once.
        tableRepo.Current = orders;
        var applied = await service.ApplyAccessAsync(new AdminApplyAccessRequest(plan.PlanId, token), AdminSid, new TenantId(Tenant));
        applied.Success.ShouldBeTrue();
        consentRepo.Consents.Count.ShouldBe(1);
    }

    private static AccessPlanningService CreateService(
        IConsentRepository consentRepo,
        IAuditLogRepository auditRepo,
        ITableMetadataRepository tableRepo,
        IAccessPlanStore store,
        TotpVerificationService totp,
        InMemoryTotpSecretStore totpSecrets) =>
        new(
            global::Autheris.Tests.Unit.TestDoubles.AuditWriterTestServices.Over(auditRepo),
            tableRepo,
            consentRepo,
            totp,
            totpSecrets,
            timeProvider: new FakeTimeProvider(FixedNow),
            accessPlanStore: store,
            planOptions: Options.Create(new AccessPlanOptions()));

    private static async Task<string> ConfirmAsync(
        AccessPlanningService service,
        TotpVerificationService totp,
        InMemoryTotpSecretStore secrets,
        string planId)
    {
        var enrollment = totp.GenerateEnrollment(AdminSid, "admin@autheris.local");
        await secrets.SetSecretAsync(AdminSid, enrollment.SecretBase32);
        var code = totp.GenerateTotpCode(enrollment.SecretBase32);
        return (await service.ConfirmPlanAsync(planId, code, AdminSid)).ConfirmationToken;
    }

    private static TableMetadata OrdersTable()
    {
        var id = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { Id = id, SchemaName = "public", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "int" }],
            PrimaryKeyColumns = ["id"]
        };
    }

    private sealed class MutableTableRepo(TableMetadata? initial) : ITableMetadataRepository
    {
        public TableMetadata? Current { get; set; } = initial;
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(Current);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>(Current is null ? [] : [Current]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AtomicTrackingConsentRepository : IConsentRepository, IConsentDescriptionRepository
    {
        public bool FailReplace { get; set; }
        public bool FailOnSecondDirectCreate { get; set; }
        public int ReplaceCalls { get; private set; }
        public int DirectCreateCalls { get; private set; }
        public List<Consent> Consents { get; } = [];
        public List<AuditLogEntry> ReplaceAuditEntries { get; } = [];

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
        {
            DirectCreateCalls++;
            if (FailOnSecondDirectCreate && DirectCreateCalls >= 2)
            {
                throw new InvalidOperationException("Simulated mid-way failure on second grant.");
            }
            Consents.Add(consent);
            return Task.FromResult(consent);
        }

        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default)
            => ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, null, null, null, ct);

        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlySet<Guid>? prunedConsentIds, string? pruneReason = null, CancellationToken ct = default)
            => ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, null, prunedConsentIds, pruneReason, ct);

        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, CancellationToken ct)
            => ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, descriptionUpdates, null, null, ct);

        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries, IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates, IReadOnlySet<Guid>? prunedConsentIds, string? pruneReason = null, CancellationToken ct = default)
        {
            ReplaceCalls++;
            if (FailReplace)
            {
                throw new InvalidOperationException("Simulated atomic transaction rollback.");
            }

            Consents.AddRange(create);
            if (auditEntries is not null)
            {
                ReplaceAuditEntries.AddRange(auditEntries);
            }
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
