namespace Autheris.Tests.Unit.Catalog.Services;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DeclarativeConsentApplyPruneSecurityTests
{
    private const string TenantA = "tenant-alpha";
    private const string TenantB = "tenant-beta";
    private const string AdminSid = "S-1-5-21-ADMIN";
    private const string GroupAlpha = "S-1-5-21-GROUP-ALPHA";
    private const string GroupBeta = "S-1-5-21-GROUP-BETA";

    private sealed class StubTableMetadataRepository : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
        {
            var id = Guid.NewGuid();
            return Task.FromResult<TableMetadata?>(new TableMetadata
            {
                Identifier = table,
                Table = new Table
                {
                    Id = id,
                    SourceName = table.Domain,
                    SchemaName = table.Schema,
                    TableName = table.TableName,
                    DisplayName = table.TableName,
                    IsActive = true
                },
                Columns = [new TableColumn { TableId = id, ColumnName = "id", DataType = "integer" }]
            });
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class InMemoryPruneConsentRepository : IConsentRepository, IConsentDescriptionRepository
    {
        public List<Consent> Consents { get; } = [];
        public List<AuditLogEntry> CommittedAudits { get; } = [];
        public Dictionary<TableIdentifier, long> TableEpochs { get; } = [];
        public int ReplaceCalls { get; private set; }
        public bool FailReplace { get; set; }
        public Func<Task>? OnBeforeReadAllAsync { get; set; }

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default)
        {
            Consents.Add(consent);
            TableEpochs[consent.TableIdentifier] = TableEpochs.GetValueOrDefault(consent.TableIdentifier, 1) + 1;
            return Task.FromResult(consent);
        }

        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) =>
            Task.FromResult(Consents.FirstOrDefault(c => c.Id == consentId));

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            var set = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<Consent> match = Consents.Where(c =>
                c.IsActive(atTime) &&
                c.TableIdentifier.Equals(table) &&
                c.GranteeSid is { } g &&
                set.Contains(g.Value) &&
                (tenantId is null || c.TenantId == tenantId.Value)).ToList();
            return Task.FromResult(match);
        }

        public async Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            if (OnBeforeReadAllAsync is not null)
            {
                await OnBeforeReadAllAsync().ConfigureAwait(false);
            }

            var at = atTime ?? DateTimeOffset.UtcNow;
            var sids = subjects.Select(s => s.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Consents.Where(c =>
                c.IsActive(at) &&
                (tenantId is null || c.TenantId == tenantId.Value) &&
                c.GranteeSid is { } g &&
                sids.Contains(g.Value)).ToList();
        }

        public Task ReplaceConsentsAsync(
            IReadOnlyList<Guid> revokeConsentIds,
            IReadOnlyList<Consent> create,
            Sid actor,
            string reason,
            ConsentRevocationAuthority authority,
            IReadOnlyList<AuditLogEntry>? auditEntries = null,
            CancellationToken ct = default) =>
            ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, null, null, null, ct);

        public Task ReplaceConsentsAsync(
            IReadOnlyList<Guid> revokeConsentIds,
            IReadOnlyList<Consent> create,
            Sid actor,
            string reason,
            ConsentRevocationAuthority authority,
            IReadOnlyList<AuditLogEntry>? auditEntries,
            IReadOnlySet<Guid>? prunedConsentIds,
            string? pruneReason = null,
            CancellationToken ct = default) =>
            ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, null, prunedConsentIds, pruneReason, ct);

        public Task ReplaceConsentsAsync(
            IReadOnlyList<Guid> revokeConsentIds,
            IReadOnlyList<Consent> create,
            Sid actor,
            string reason,
            ConsentRevocationAuthority authority,
            IReadOnlyList<AuditLogEntry>? auditEntries,
            IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates,
            CancellationToken ct) =>
            ReplaceConsentsAsync(revokeConsentIds, create, actor, reason, authority, auditEntries, descriptionUpdates, null, null, ct);

        public Task ReplaceConsentsAsync(
            IReadOnlyList<Guid> revokeConsentIds,
            IReadOnlyList<Consent> create,
            Sid actor,
            string reason,
            ConsentRevocationAuthority authority,
            IReadOnlyList<AuditLogEntry>? auditEntries,
            IReadOnlyList<ConsentDescriptionUpdate>? descriptionUpdates,
            IReadOnlySet<Guid>? prunedConsentIds,
            string? pruneReason = null,
            CancellationToken ct = default)
        {
            ReplaceCalls++;
            if (FailReplace)
            {
                throw new InvalidOperationException("injected fault");
            }

            var snapshot = Consents.Select(c => Clone(c)).ToList();
            var touchedTables = new HashSet<TableIdentifier>();
            var allRevoke = prunedConsentIds is { Count: > 0 }
                ? revokeConsentIds.Concat(prunedConsentIds).Distinct().ToList()
                : revokeConsentIds.Distinct().ToList();

            foreach (var id in allRevoke)
            {
                var idx = snapshot.FindIndex(c => c.Id == id);
                if (idx < 0) continue;
                var existing = snapshot[idx];
                var isPruned = prunedConsentIds?.Contains(id) == true;
                if (isPruned && existing.GrantSource != ConsentGrantSource.Declarative)
                {
                    throw new InvalidOperationException("Only declarative consents may be pruned.");
                }

                snapshot[idx] = Clone(existing, revoked: true, revokeReason: isPruned ? (pruneReason ?? "PRUNED_BY_MANIFEST") : reason);
                touchedTables.Add(existing.TableIdentifier);
            }

            foreach (var item in create)
            {
                snapshot.Add(item);
                touchedTables.Add(item.TableIdentifier);
            }

            Consents.Clear();
            Consents.AddRange(snapshot);
            if (auditEntries is not null)
            {
                CommittedAudits.AddRange(auditEntries);
            }

            foreach (var table in touchedTables)
            {
                TableEpochs[table] = TableEpochs.GetValueOrDefault(table, 1) + 1;
            }

            return Task.CompletedTask;
        }

        public Task LoadDescriptionsAsync(IReadOnlyCollection<Consent> consents, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> UpdateConsentDescriptionWithAuditAsync(Guid consentId, TenantId tenant, string? description, AuditLogEntry audit, CancellationToken ct = default) => Task.FromResult(false);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotImplementedException();

        private static Consent Clone(Consent c, bool? revoked = null, string? revokeReason = null) => new()
        {
            Id = c.Id,
            TableId = c.TableId,
            TableIdentifier = c.TableIdentifier,
            TenantId = c.TenantId,
            Effect = c.Effect,
            GranteeType = c.GranteeType,
            GranteeSid = c.GranteeSid,
            ValidFrom = c.ValidFrom,
            ValidTo = c.ValidTo,
            IsRevoked = revoked ?? c.IsRevoked,
            RevokeReason = revokeReason ?? c.RevokeReason,
            GrantSource = c.GrantSource,
            ColumnRules = c.ColumnRules,
            RowFilters = c.RowFilters,
            Description = c.Description
        };
    }

    private sealed class StubOwnershipRepository(Func<TableIdentifier, Sid, bool> isApprover) : IContainerOwnership
    {
        public Task<DataOwnerDelegation> DelegateDataOwnershipAsync(DataOwnerDelegation delegation, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DataOwner>>([]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
            Task.FromResult(isApprover(table, approverSid));
    }

    private sealed class AllowApplyRoleEvaluator : IGatewayRoleEvaluator
    {
        public bool HasRole(ClaimsPrincipal? principal, GatewayRole role, string? tenantId = null) => true;
        public bool HasRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null) => true;
        public bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<GatewayRole> roles, string? tenantId = null) => true;
        public bool HasAnyRole(ClaimsPrincipal? principal, IEnumerable<string> roleNames, string? tenantId = null) => true;
        public bool HasExactRole(ClaimsPrincipal? principal, string roleName, string? tenantId = null) => true;
        public bool HasAnyExactRole(ClaimsPrincipal? principal, IEnumerable<string> roleNames, string? tenantId = null) => true;
        public IReadOnlySet<GatewayRole> GetEffectiveRoles(ClaimsPrincipal? principal, string? tenantId = null) =>
            new HashSet<GatewayRole> { GatewayRole.GovernanceAdmin };
    }

    private sealed class StubApplyService(Func<DeclarativePruneMode, bool, Task<DeclarativeApplyResult>> handler) : IDeclarativeConsentApplyService
    {
        public Task<DeclarativeApplyResult> ApplyManifestAsync(
            ClaimsPrincipal caller,
            DeclarativeConsentManifest manifest,
            bool dryRun = false,
            SecurityPrincipalContext? callerContext = null,
            DeclarativePruneMode pruneMode = DeclarativePruneMode.False,
            CancellationToken ct = default) =>
            handler(pruneMode, dryRun);
    }

    private static ClaimsPrincipal AdminPrincipal(string tenant = TenantA, string sid = AdminSid) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim(ClaimTypes.Role, "GovernanceAdmin"),
                new Claim("tenant_id", tenant)
            ],
            "test"));

    private static ClaimsPrincipal NonAdminPrincipal(string tenant = TenantA, string sid = "S-1-5-21-OWNER") =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim("tenant_id", tenant)
            ],
            "test"));

    private static Consent SeedConsent(
        string tableName,
        ConsentGrantSource source,
        string grantee = GroupAlpha,
        string tenant = TenantA,
        GranteeType granteeType = GranteeType.Group) =>
        new()
        {
            Id = Guid.NewGuid(),
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("default", "sales", tableName),
            TenantId = new TenantId(tenant),
            Effect = ConsentEffect.Allow,
            GranteeType = granteeType,
            GranteeSid = new Sid(grantee),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(30),
            IsRevoked = false,
            Description = "test grant justification",
            GrantSource = source
        };

    private static DeclarativeConsentManifest ManifestFor(params (string Table, string Grantee)[] entries) =>
        new(
            "1",
            entries.Select(e => new DeclarativeConsentPolicy("default", "sales", e.Table, "Group", e.Grantee, "Allow", Description: "test grant justification", ValidUntil: DateTimeOffset.UtcNow.AddDays(30))).ToList());

    private static DeclarativeConsentApplyService CreateService(
        InMemoryPruneConsentRepository repo,
        IContainerOwnership? ownership = null,
        IGatewayRoleEvaluator? roles = null) =>
        new(new StubTableMetadataRepository(), repo, new NullAuditLogRepository(), dataOwnershipRepository: ownership, roleEvaluator: roles);

    [Theory]
    [InlineData("Role", false)]
    [InlineData("role", true)]
    [InlineData("ROLE", false)]
    [InlineData("3", true)]
    public async Task Security_DeclarativeApply_RoleGrantee_RejectsWholeDocumentWithoutWrites(string granteeType, bool dryRun)
    {
        var repo = new InMemoryPruneConsentRepository();
        var svc = CreateService(repo);
        var manifest = new DeclarativeConsentManifest(
            "1",
            [
                new DeclarativeConsentPolicy("default", "sales", "orders", "Group", GroupAlpha, "Allow", Description: "test grant justification", ValidUntil: DateTimeOffset.UtcNow.AddDays(30)),
                new DeclarativeConsentPolicy("default", "sales", "missing_table_is_not_a_loophole", granteeType, "analysts", "Deny", Description: "test grant justification", ValidUntil: DateTimeOffset.UtcNow.AddDays(30))
            ]);

        var ex = await Should.ThrowAsync<DeclarativeManifestValidationException>(() => svc.ApplyManifestAsync(AdminPrincipal(), manifest, dryRun));

        ex.PolicyIndex.ShouldBe(1);
        ex.Violation.ShouldBe(DeclarativeManifestViolation.GranteeType);
        ex.Message.ShouldNotContain("analysts");
        repo.ReplaceCalls.ShouldBe(0);
        repo.Consents.ShouldBeEmpty();
    }

    [Fact]
    public async Task Prune_False_KeepsUnlistedGrants()
    {
        var repo = new InMemoryPruneConsentRepository();
        var unlisted = SeedConsent("legacy_orders", ConsentGrantSource.Declarative);
        repo.Consents.Add(unlisted);
        var svc = CreateService(repo);

        var result = await svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.False);

        result.PrunedCount.ShouldBe(0);
        result.Pruned.ShouldBeNull();
        repo.Consents.Single(c => c.Id == unlisted.Id).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Prune_NeverTouches_NonDeclarativeConsents()
    {
        var repo = new InMemoryPruneConsentRepository();
        var nonDeclarativeSources = new[]
        {
            ConsentGrantSource.ConsentRequest,
            ConsentGrantSource.AccessProfile,
            ConsentGrantSource.Owner,
            ConsentGrantSource.Bulk,
            ConsentGrantSource.AccessPlan
        };

        var preserved = nonDeclarativeSources.Select((s, i) => SeedConsent($"tbl_{i}", s)).ToList();
        var declarativeKeep = SeedConsent("orders", ConsentGrantSource.Declarative);
        var declarativePrune = SeedConsent("obsolete", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([.. preserved, declarativeKeep, declarativePrune]);
        var svc = CreateService(repo);

        // First make declarativeKeep identical? No, manifest applies orders -> keep orders declarative as active? Actually obsolete (1/2 = 50% <= 50%).
        var result = await svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.True);

        result.PrunedCount.ShouldBe(1);
        result.Pruned.ShouldHaveSingleItem().ConsentId.ShouldBe(declarativePrune.Id);
        repo.Consents.Single(c => c.Id == declarativePrune.Id).IsRevoked.ShouldBeTrue();
        repo.Consents.Single(c => c.Id == declarativePrune.Id).RevokeReason.ShouldBe("PRUNED_BY_MANIFEST");

        foreach (var consent in preserved)
        {
            repo.Consents.Single(c => c.Id == consent.Id).IsRevoked.ShouldBeFalse($"Non-declarative source {consent.GrantSource} must never be pruned.");
        }
    }

    [Fact]
    public async Task Prune_NeverTouches_OtherTenant_Or_GranteeNotInManifest()
    {
        var repo = new InMemoryPruneConsentRepository();
        var otherTenant = SeedConsent("obsolete", ConsentGrantSource.Declarative, GroupAlpha, TenantB);
        var otherGrantee = SeedConsent("obsolete", ConsentGrantSource.Declarative, GroupBeta, TenantA);
        var target = SeedConsent("obsolete", ConsentGrantSource.Declarative, GroupAlpha, TenantA);
        var kept = SeedConsent("orders", ConsentGrantSource.Declarative, GroupAlpha, TenantA);
        repo.Consents.AddRange([otherTenant, otherGrantee, target, kept]);
        var svc = CreateService(repo);

        var result = await svc.ApplyManifestAsync(AdminPrincipal(TenantA), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.True);

        result.PrunedCount.ShouldBe(1);
        result.Pruned.ShouldHaveSingleItem().ConsentId.ShouldBe(target.Id);
        repo.Consents.Single(c => c.Id == target.Id).IsRevoked.ShouldBeTrue();
        repo.Consents.Single(c => c.Id == otherTenant.Id).IsRevoked.ShouldBeFalse();
        repo.Consents.Single(c => c.Id == otherGrantee.Id).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Prune_EmptyManifest_PrunesNothing()
    {
        var repo = new InMemoryPruneConsentRepository();
        var existing = SeedConsent("orders", ConsentGrantSource.Declarative);
        repo.Consents.Add(existing);
        var svc = CreateService(repo);

        var result = await svc.ApplyManifestAsync(AdminPrincipal(), new DeclarativeConsentManifest("1", []), pruneMode: DeclarativePruneMode.Force);

        result.PrunedCount.ShouldBe(0);
        result.Pruned.ShouldNotBeNull().ShouldBeEmpty();
        repo.ReplaceCalls.ShouldBe(0);
        repo.Consents.Single(c => c.Id == existing.Id).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Prune_NonAdminCallerWithoutOwnerAuthorityOnPrunedTable_DeniedAtomically()
    {
        var repo = new InMemoryPruneConsentRepository();
        var ownedTableConsent = SeedConsent("owned_old", ConsentGrantSource.Declarative);
        var unownedTableConsent = SeedConsent("unowned_old", ConsentGrantSource.Declarative);
        var kept = SeedConsent("orders", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([ownedTableConsent, unownedTableConsent, kept]);

        var ownership = new StubOwnershipRepository((t, _) => string.Equals(t.TableName, "owned_old", StringComparison.OrdinalIgnoreCase));
        var svc = CreateService(repo, ownership: ownership, roles: new AllowApplyRoleEvaluator());

        await Should.ThrowAsync<ConsentRevocationForbiddenException>(() =>
            svc.ApplyManifestAsync(NonAdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.Force));

        repo.ReplaceCalls.ShouldBe(0);
        repo.Consents.Single(c => c.Id == ownedTableConsent.Id).IsRevoked.ShouldBeFalse();
        repo.Consents.Single(c => c.Id == unownedTableConsent.Id).IsRevoked.ShouldBeFalse();
        repo.CommittedAudits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Prune_NonAdminCallerWithOwnerAuthorityOnAllPrunedTables_Succeeds()
    {
        var repo = new InMemoryPruneConsentRepository();
        var ownedOld = SeedConsent("owned_old", ConsentGrantSource.Declarative);
        var kept = SeedConsent("orders", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([ownedOld, kept]);

        var ownership = new StubOwnershipRepository((t, _) => string.Equals(t.TableName, "owned_old", StringComparison.OrdinalIgnoreCase));
        var svc = CreateService(repo, ownership: ownership, roles: new AllowApplyRoleEvaluator());

        var result = await svc.ApplyManifestAsync(NonAdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.True);

        result.PrunedCount.ShouldBe(1);
        repo.Consents.Single(c => c.Id == ownedOld.Id).IsRevoked.ShouldBeTrue();
        repo.Consents.Single(c => c.Id == kept.Id).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Prune_Above50Percent_RejectedWithoutForce_AllowedWithForce()
    {
        var repo = new InMemoryPruneConsentRepository();
        var c1 = SeedConsent("orders", ConsentGrantSource.Declarative);
        var c2 = SeedConsent("old_1", ConsentGrantSource.Declarative);
        var c3 = SeedConsent("old_2", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([c1, c2, c3]);
        var svc = CreateService(repo);

        // 2 of 3 (>50%) must reject under prune=true without mutating
        await Should.ThrowAsync<DeclarativePruneThresholdExceededException>(() =>
            svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.True));

        repo.ReplaceCalls.ShouldBe(0);
        repo.Consents.Count(c => !c.IsRevoked).ShouldBe(3);

        // dryRun=true with prune=true must enforce the same >50% gate
        await Should.ThrowAsync<DeclarativePruneThresholdExceededException>(() =>
            svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), dryRun: true, pruneMode: DeclarativePruneMode.True));

        // prune=force succeeds
        var forced = await svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.Force);
        forced.PrunedCount.ShouldBe(2);
        repo.Consents.Single(c => c.Id == c2.Id).IsRevoked.ShouldBeTrue();
        repo.Consents.Single(c => c.Id == c3.Id).IsRevoked.ShouldBeTrue();
        repo.Consents.Single(c => c.Id == c1.Id).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Prune_Above10000Cap_Rejected()
    {
        var repo = new InMemoryPruneConsentRepository();
        for (var i = 0; i <= DeclarativeConsentApplyService.MaxDeclarativePruneCount; i++)
        {
            repo.Consents.Add(SeedConsent($"old_{i}", ConsentGrantSource.Declarative));
        }

        var svc = CreateService(repo);

        await Should.ThrowAsync<DeclarativePruneCapExceededException>(() =>
            svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.Force));

        repo.ReplaceCalls.ShouldBe(0);
        repo.Consents.ShouldAllBe(c => !c.IsRevoked);
    }

    [Fact]
    public async Task DryRun_ReturnsDiffAndPrunedList_WithoutMutatingStoreOrEpochs()
    {
        var repo = new InMemoryPruneConsentRepository();
        var keep = SeedConsent("orders", ConsentGrantSource.Declarative);
        var pruneCandidate = SeedConsent("obsolete", ConsentGrantSource.Declarative);
        var foreignTenant = SeedConsent("foreign_tbl", ConsentGrantSource.Declarative, GroupAlpha, TenantB);
        repo.Consents.AddRange([keep, pruneCandidate, foreignTenant]);
        repo.TableEpochs[pruneCandidate.TableIdentifier] = 5;
        var svc = CreateService(repo);

        var result = await svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), dryRun: true, pruneMode: DeclarativePruneMode.True);

        result.DryRun.ShouldBeTrue();
        result.PrunedCount.ShouldBe(1);
        var dto = result.Pruned.ShouldHaveSingleItem();
        dto.ConsentId.ShouldBe(pruneCandidate.Id);
        dto.Grantee.ShouldBe(GroupAlpha);
        dto.Table.ShouldBe("obsolete");
        result.Pruned.ShouldNotContain(p => p.ConsentId == foreignTenant.Id);

        repo.ReplaceCalls.ShouldBe(0);
        repo.Consents.Single(c => c.Id == pruneCandidate.Id).IsRevoked.ShouldBeFalse();
        repo.TableEpochs[pruneCandidate.TableIdentifier].ShouldBe(5);
        repo.CommittedAudits.ShouldBeEmpty();
    }

    [Fact]
    public async Task Prune_SetRecomputed_IgnoresDryRunSnapshot()
    {
        var repo = new InMemoryPruneConsentRepository();
        var keep = SeedConsent("orders", ConsentGrantSource.Declarative);
        var c2 = SeedConsent("old_1", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([keep, c2]);
        var svc = CreateService(repo);
        var manifest = ManifestFor(("orders", GroupAlpha));

        var dry = await svc.ApplyManifestAsync(AdminPrincipal(), manifest, dryRun: true, pruneMode: DeclarativePruneMode.True);
        dry.PrunedCount.ShouldBe(1);

        // Concurrent change adds another declarative consent for the same grantee -> now 2/3 (>50%)
        repo.Consents.Add(SeedConsent("old_2", ConsentGrantSource.Declarative));

        await Should.ThrowAsync<DeclarativePruneThresholdExceededException>(() =>
            svc.ApplyManifestAsync(AdminPrincipal(), manifest, dryRun: false, pruneMode: DeclarativePruneMode.True));

        repo.Consents.Single(c => c.Id == c2.Id).IsRevoked.ShouldBeFalse();
    }

    [Fact]
    public async Task Prune_ConcurrentApplies_DoNotOverRevoke()
    {
        var repo = new InMemoryPruneConsentRepository();
        var t1 = SeedConsent("t1", ConsentGrantSource.Declarative);
        var t2 = SeedConsent("t2", ConsentGrantSource.Declarative);
        var t3 = SeedConsent("t3", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([t1, t2, t3]);

        var firstEnteredRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCount = 0;
        repo.OnBeforeReadAllAsync = async () =>
        {
            if (Interlocked.Increment(ref readCount) == 1)
            {
                firstEnteredRead.TrySetResult();
                await releaseFirst.Task.ConfigureAwait(false);
            }
        };

        var svc = CreateService(repo);
        var apply1 = svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("t1", GroupAlpha), ("t2", GroupAlpha)), pruneMode: DeclarativePruneMode.True);
        await firstEnteredRead.Task;

        // Second apply targets same grantee (1 of 2 remaining active after apply1 commits would be 50%, or keeping only t1 after apply1 leaves 2 active -> pruning t2 is 1/2 = 50%; keeping none/different table triggers fresh read)
        // Keeping only new table t4 would prune {t1, t2} after apply1 committed (2/2 = 100% > 50% -> rejected by second apply!)
        var apply2 = svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("t4", GroupAlpha)), pruneMode: DeclarativePruneMode.True);

        releaseFirst.TrySetResult();

        var firstResult = await apply1;
        firstResult.PrunedCount.ShouldBe(1);

        await Should.ThrowAsync<DeclarativePruneThresholdExceededException>(() => apply2);
        repo.Consents.Single(c => c.Id == t1.Id).IsRevoked.ShouldBeFalse();
        repo.Consents.Single(c => c.Id == t2.Id).IsRevoked.ShouldBeFalse();
        repo.Consents.Single(c => c.Id == t3.Id).IsRevoked.ShouldBeTrue();
    }

    [Fact]
    public async Task Prune_WritesAuditWithManifestHash_And_BumpsGrantEpoch()
    {
        var repo = new InMemoryPruneConsentRepository();
        var keep = SeedConsent("orders", ConsentGrantSource.Declarative);
        var pruneTarget = SeedConsent("obsolete", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([keep, pruneTarget]);
        repo.TableEpochs[pruneTarget.TableIdentifier] = 10;
        var svc = CreateService(repo);
        var manifest = ManifestFor(("orders", GroupAlpha));
        var expectedHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(manifest)));

        var result = await svc.ApplyManifestAsync(AdminPrincipal(), manifest, pruneMode: DeclarativePruneMode.True);

        result.PrunedCount.ShouldBe(1);
        repo.Consents.Single(c => c.Id == pruneTarget.Id).RevokeReason.ShouldBe("PRUNED_BY_MANIFEST");
        repo.TableEpochs[pruneTarget.TableIdentifier].ShouldBe(11);

        var audit = repo.CommittedAudits.ShouldHaveSingleItem();
        audit.ActorSid.Value.ShouldBe(AdminSid);
        audit.TargetTable.ShouldBe("default.sales.obsolete");
        audit.DetailsJson.ShouldNotBeNull();
        audit.DetailsJson.ShouldContain("PRUNED_BY_MANIFEST");
        audit.DetailsJson.ShouldContain(pruneTarget.Id.ToString());
        audit.DetailsJson.ShouldContain(expectedHash);
        audit.DetailsJson.ShouldNotContain("Policies");
    }

    [Fact]
    public async Task Prune_FailureMidway_RollsBackAll_NoPartialRevocation()
    {
        var repo = new InMemoryPruneConsentRepository { FailReplace = true };
        var keep = SeedConsent("orders", ConsentGrantSource.Declarative);
        var pruneTarget = SeedConsent("obsolete", ConsentGrantSource.Declarative);
        repo.Consents.AddRange([keep, pruneTarget]);
        var svc = CreateService(repo);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            svc.ApplyManifestAsync(AdminPrincipal(), ManifestFor(("orders", GroupAlpha)), pruneMode: DeclarativePruneMode.True));

        repo.Consents.Single(c => c.Id == pruneTarget.Id).IsRevoked.ShouldBeFalse();
        repo.CommittedAudits.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("FORCE ")]
    [InlineData(" true")]
    [InlineData("")]
    [InlineData("all")]
    public async Task Prune_InvalidValue_Returns400_NotTrue(string rawPrune)
    {
        var called = false;
        var stubSvc = new StubApplyService((_, _) =>
        {
            called = true;
            return Task.FromResult(new DeclarativeApplyResult(0, 0, 0, 0, false, []));
        });
        var context = BuildEndpointContext("?prune=" + Uri.EscapeDataString(rawPrune));

        var result = await GovernanceEndpoints.ApplyConsentManifestAsync(context, stubSvc, CancellationToken.None);
        await result.ExecuteAsync(context);

        called.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Prune_Endpoint_MapsThreshold409_Cap400_AndForbidden403()
    {
        var thresholdSvc = new StubApplyService((_, _) => Task.FromException<DeclarativeApplyResult>(new DeclarativePruneThresholdExceededException(3, 4)));
        var capSvc = new StubApplyService((_, _) => Task.FromException<DeclarativeApplyResult>(new DeclarativePruneCapExceededException(10001, 10000)));
        var forbidSvc = new StubApplyService((_, _) => Task.FromException<DeclarativeApplyResult>(new ConsentRevocationForbiddenException()));

        var ctx409 = BuildEndpointContext("?prune=true&dryRun=true");
        await (await GovernanceEndpoints.ApplyConsentManifestAsync(ctx409, thresholdSvc, CancellationToken.None)).ExecuteAsync(ctx409);
        ctx409.Response.StatusCode.ShouldBe(StatusCodes.Status409Conflict);

        var ctx400 = BuildEndpointContext("?prune=force");
        await (await GovernanceEndpoints.ApplyConsentManifestAsync(ctx400, capSvc, CancellationToken.None)).ExecuteAsync(ctx400);
        ctx400.Response.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        var ctx403 = BuildEndpointContext("?prune=true");
        await (await GovernanceEndpoints.ApplyConsentManifestAsync(ctx403, forbidSvc, CancellationToken.None)).ExecuteAsync(ctx403);
        ctx403.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }

    private static DefaultHttpContext BuildEndpointContext(string queryString)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance)
                .BuildServiceProvider()
        };
        context.Request.QueryString = new QueryString(queryString);
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("""{ "version": "1", "policies": [] }"""));
        context.Request.ContentType = "application/json";
        context.Response.Body = new MemoryStream();
        context.User = AdminPrincipal();
        return context;
    }
}
