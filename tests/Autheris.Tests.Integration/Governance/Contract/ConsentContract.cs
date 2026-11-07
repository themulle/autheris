using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using FluentAssertions;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>
/// Engine-agnostic contract suite for <c>IConsentRepository</c>, <c>IConsentApprovalRepository</c>,
/// and <c>IConsentDescriptionRepository</c> (including grants, revocations, decisions, and keyset paging).
/// </summary>
public abstract class ConsentContract : GovernanceContractBase
{
    private static readonly Sid Owner1Sid = new("S-1-5-21-900-1");
    private static readonly Sid Owner2Sid = new("S-1-5-21-900-2");
    private static readonly Sid RequesterSid = new("S-1-5-21-900-3");
    private static readonly Sid GranteeSid = new("S-1-5-21-900-4");

    private async Task<TableMetadata> SeedTableAsync(
        string name = "orders",
        string sensitivity = "NORMAL",
        bool requiresFourEyes = false)
    {
        var id = new TableIdentifier("sales", "dbo", name + "_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new TableMetadata
        {
            Identifier = id,
            ReplaceKeyDefinition = true,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = id.Domain,
                SchemaName = id.Schema,
                TableName = id.TableName,
                Description = "Contract test table",
                Sensitivity = sensitivity,
                RequiresFourEyes = requiresFourEyes,
                IsActive = true
            },
            Columns =
            [
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "id",
                    DataType = "int",
                    IsSensitive = false
                },
                new TableColumn
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "secret_col",
                    DataType = "varchar",
                    IsSensitive = Table.IsSensitivityHigh(sensitivity)
                }
            ],
            PrimaryKeyColumns = ["id"]
        };

        var saved = await Store.TableMetadata.UpsertTableMetadataAsync(table);
        _ = await Store.DataOwnerAssignments.ApplyTableOwnerAssignmentsAsync(
            new DataOwnerAssignmentRequest(
                [
                    new TableOwnerAssignmentSpec(
                        id,
                        [
                            new DataOwnerSpec(Owner1Sid.Value, "owner1", "Owner One", "owner1@example.com", "PRIMARY"),
                            new DataOwnerSpec(Owner2Sid.Value, "owner2", "Owner Two", "owner2@example.com", "DELEGATE")
                        ])
                ],
                Replace: true,
                DryRun: false));
        return saved;
    }

    private Consent NewConsent(
        TableIdentifier table,
        TenantId tenant,
        Sid? granteeSid = null,
        GranteeType granteeType = GranteeType.User,
        string? roleName = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null,
        Guid? requestId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table,
            ConsentRequestId = requestId,
            Effect = ConsentEffect.Allow,
            GranteeType = granteeType,
            GranteeSid = granteeType == GranteeType.Role ? (Sid?)null : (granteeSid ?? GranteeSid),
            RoleName = granteeType == GranteeType.Role ? (roleName ?? "DataAnalyst") : null,
            ValidFrom = validFrom ?? DateTimeOffset.UtcNow.AddMinutes(-10),
            ValidTo = validTo ?? DateTimeOffset.UtcNow.AddDays(10),
            IsRevoked = false,
            TenantId = tenant,
            RowFilters =
            [
                new ConsentRowFilter
                {
                    Id = Guid.NewGuid(),
                    FilterType = RowFilterType.SimpleColumnPredicate,
                    ColumnName = "id",
                    Operator = "GT",
                    ValueType = "int",
                    ValueJson = "[\"0\"]"
                }
            ],
            ColumnRules =
            [
                new ConsentColumnRule
                {
                    Id = Guid.NewGuid(),
                    ColumnName = "secret_col",
                    AccessLevel = ColumnAccessLevel.Mask
                }
            ]
        };

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var tableA = await SeedTableAsync("tenant_a_tbl");
        var tableB = await SeedTableAsync("tenant_b_tbl");
        var now = DateTimeOffset.UtcNow;

        var consentA = await Store.Consents.CreateConsentAsync(NewConsent(tableA.Identifier, Store.TenantA, GranteeSid));
        var consentB = await Store.Consents.CreateConsentAsync(NewConsent(tableB.Identifier, Store.TenantB, GranteeSid));

        var roleConsentA = await Store.Consents.CreateConsentAsync(
            NewConsent(tableA.Identifier, Store.TenantA, granteeType: GranteeType.Role, roleName: "AnalystRole"));
        _ = await Store.Consents.CreateConsentAsync(
            NewConsent(tableB.Identifier, Store.TenantB, granteeType: GranteeType.Role, roleName: "AnalystRole"));

        var bySubjectA = await Store.Consents.GetActiveConsentsForSubjectsAsync([GranteeSid], tableA.Identifier, now, Store.TenantA);
        bySubjectA.Where(c => c.GranteeType == GranteeType.User).Select(c => c.Id).Should().ContainSingle().Which.Should().Be(consentA.Id);
        bySubjectA.Select(c => c.Id).Should().BeEquivalentTo([consentA.Id, roleConsentA.Id]);

        var crossTableRead = await Store.Consents.GetActiveConsentsForSubjectsAsync([GranteeSid], tableB.Identifier, now, Store.TenantA);
        crossTableRead.Should().BeEmpty();

        var allForTenantA = await Store.Consents.GetAllActiveConsentsForSubjectsAsync([GranteeSid], ["AnalystRole"], now, Store.TenantA);
        allForTenantA.Where(c => c.TableIdentifier.Equals(tableA.Identifier)).Select(c => c.Id).Should().BeEquivalentTo([consentA.Id, roleConsentA.Id]);
        allForTenantA.Select(c => c.Id).Should().NotContain(consentB.Id);

        var roleForTenantA = await Store.Consents.GetActiveRoleConsentsAsync(tableA.Identifier, "AnalystRole", Store.TenantA, now);
        roleForTenantA.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(roleConsentA.Id);

        var tablesForTenantA = await Store.Consents.GetTablesWithConsentsForTenantAsync(Store.TenantA);
        tablesForTenantA.Should().Contain(tableA.Identifier).And.NotContain(tableB.Identifier);

        var pageA = await Store.Consents.GetConsentPageAsync(new ConsentPageQuery(Store.TenantA));
        pageA.Items.Should().OnlyContain(c => c.TenantId == Store.TenantA);

        var sharedTicket = "INC-" + Guid.NewGuid().ToString("N")[..8];
        var reqA = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = tableA.Identifier,
            RequesterSid = RequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Tenant A request",
            RequestedValidTo = now.AddDays(5),
            ItsmTicketId = sharedTicket,
            TenantId = Store.TenantA
        });
        _ = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = tableB.Identifier,
            RequesterSid = RequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Tenant B request",
            RequestedValidTo = now.AddDays(5),
            ItsmTicketId = sharedTicket,
            TenantId = Store.TenantB
        });

        var lookedUpA = await Store.ConsentApprovals.GetConsentRequestByTicketIdAsync(sharedTicket, Store.TenantA);
        lookedUpA.Should().NotBeNull();
        lookedUpA!.Id.Should().Be(reqA.Id);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("write_iso");
        var consentA = await Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, Store.TenantA, GranteeSid));

        var audit = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            EventType = "ConsentDescriptionUpdated",
            ActorSid = Owner1Sid,
            TargetTable = table.Identifier.ToString(),
            Decision = "ALLOW",
            TenantId = Store.TenantB
        };

        var updated = await Store.ConsentDescriptions.UpdateConsentDescriptionWithAuditAsync(
            consentA.Id,
            Store.TenantB,
            "Cross-tenant hijack attempt",
            audit);
        updated.Should().BeFalse();

        Consent[] loaded = [consentA];
        await Store.ConsentDescriptions.LoadDescriptionsAsync(loaded);
        loaded[0].Description.Should().BeNull();

        var filterId = consentA.RowFilters[0].Id;
        var filterChange = await Store.ConsentApprovals.UpdateRowFilterEvaluationAsync(
            filterId,
            Store.TenantB,
            new RowFilterEvaluationSettings(RowFilterEvaluationStrategy.KeySetCache, 30, 1000));
        filterChange.Should().BeNull();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("bad_tenant");

        var pageAct = () => Store.Consents.GetConsentPageAsync(new ConsentPageQuery(default));
        await pageAct.Should().ThrowAsync<ArgumentException>();

        var createConsentAct = () => Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, default));
        await createConsentAct.Should().ThrowAsync<ArgumentException>();

        var createReqAct = () => Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table.Identifier,
            RequesterSid = RequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Missing tenant",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = default
        });
        await createReqAct.Should().ThrowAsync<ArgumentException>();

        var validConsent = await Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, Store.TenantA));
        await Store.ExecuteNonQueryAsync(
            "UPDATE CONSENTS SET tenant_id = @bad WHERE id = @id",
            ("@bad", "_reserved_corrupt"),
            ("@id", validConsent.Id.ToString()));

        var readCorruptAct = () => Store.Consents.GetConsentByIdAsync(validConsent.Id);
        await readCorruptAct.Should().ThrowAsync<StoredTenantIntegrityException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("upsert_idem");
        var consent = await Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, Store.TenantA));

        for (int i = 0; i < 2; i++)
        {
            var audit = new AuditLogEntry
            {
                Id = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow,
                EventType = "ConsentDescriptionUpdated",
                ActorSid = Owner1Sid,
                TargetTable = table.Identifier.ToString(),
                Decision = "ALLOW",
                TenantId = Store.TenantA
            };

            var ok = await Store.ConsentDescriptions.UpdateConsentDescriptionWithAuditAsync(
                consent.Id,
                Store.TenantA,
                "Idempotent description",
                audit);
            ok.Should().BeTrue();
        }

        var reloaded = await Store.Consents.GetConsentByIdAsync(consent.Id);
        reloaded.Should().NotBeNull();
        await Store.ConsentDescriptions.LoadDescriptionsAsync([reloaded!]);
        reloaded!.Description.Should().Be("Idempotent description");
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("conflict_tbl", "HIGH", requiresFourEyes: true);
        var req = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table.Identifier,
            RequesterSid = RequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Need high sensitivity access",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Store.TenantA
        });

        _ = await Store.ConsentApprovals.ApproveConsentRequestStepAsync(req.Id, Owner1Sid);

        var duplicateApprove = () => Store.ConsentApprovals.ApproveConsentRequestStepAsync(req.Id, Owner1Sid);
        await duplicateApprove.Should().ThrowAsync<InvalidOperationException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("paging_tbl");
        var expectedIds = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            var created = await Store.Consents.CreateConsentAsync(
                NewConsent(table.Identifier, Store.TenantA, new Sid($"S-1-5-21-900-10{i}")));
            expectedIds.Add(created.Id);
        }

        _ = await Store.Consents.CreateConsentAsync(
            NewConsent(table.Identifier, Store.TenantB, new Sid("S-1-5-21-900-999")));

        var collected = new List<Guid>();
        Guid? after = null;
        while (true)
        {
            var page = await Store.Consents.GetConsentPageAsync(
                new ConsentPageQuery(Store.TenantA, Table: table.Identifier, AfterId: after, PageSize: 2));
            collected.AddRange(page.Items.Select(c => c.Id));
            if (page.NextAfterId is null)
            {
                break;
            }

            after = page.NextAfterId;
        }

        collected.Should().HaveCount(5);
        collected.Should().BeInAscendingOrder();
        collected.Should().BeEquivalentTo(expectedIds);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Page_size_above_cap_is_clamped_or_rejected()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("page_cap_tbl");
        _ = await Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, Store.TenantA));

        var overCapQuery = new ConsentPageQuery(Store.TenantA, Table: table.Identifier, PageSize: ConsentPageQuery.MaxPageSize + 10_000);
        overCapQuery.EffectivePageSize.Should().Be(ConsentPageQuery.MaxPageSize);

        var zeroQuery = new ConsentPageQuery(Store.TenantA, Table: table.Identifier, PageSize: 0);
        zeroQuery.EffectivePageSize.Should().Be(1);

        var page = await Store.Consents.GetConsentPageAsync(overCapQuery);
        page.Items.Should().ContainSingle();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("soft_del_tbl");
        var now = DateTimeOffset.UtcNow;
        var consent = await Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, Store.TenantA, GranteeSid));

        Store.SetCaller(new CallerContext("contract-test", "1.0", "revoke"));
        await Store.Consents.RevokeConsentAsync(consent.Id, Owner1Sid, "Revoked in contract test");

        var activeBySubj = await Store.Consents.GetActiveConsentsForSubjectsAsync([GranteeSid], table.Identifier, now, Store.TenantA);
        activeBySubj.Should().BeEmpty();

        var allActive = await Store.Consents.GetAllActiveConsentsForSubjectsAsync([GranteeSid], null, now, Store.TenantA);
        allActive.Should().BeEmpty();

        var expiring = await Store.Consents.GetExpiringConsentsAsync(now.AddDays(30));
        expiring.Select(c => c.Id).Should().NotContain(consent.Id);

        var activePage = await Store.Consents.GetConsentPageAsync(new ConsentPageQuery(Store.TenantA, Table: table.Identifier, IncludeInactive: false));
        activePage.Items.Should().BeEmpty();

        var inactivePage = await Store.Consents.GetConsentPageAsync(new ConsentPageQuery(Store.TenantA, Table: table.Identifier, IncludeInactive: true));
        inactivePage.Items.Select(c => c.Id).Should().ContainSingle().Which.Should().Be(consent.Id);

        var byId = await Store.Consents.GetConsentByIdAsync(consent.Id);
        byId.Should().NotBeNull();
        byId!.IsRevoked.Should().BeTrue();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Revoke_unifies_grants()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("revoke_unify");
        var syncMarker = Guid.NewGuid();
        var c1 = await Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, Store.TenantA, GranteeSid, requestId: syncMarker));
        var c2 = await Store.Consents.CreateConsentAsync(NewConsent(table.Identifier, Store.TenantA, new Sid("S-1-5-21-900-55")));

        var unauthorizedSid = new Sid("S-1-5-21-999-999");
        Store.SetCaller(new CallerContext("stranger-app", "1.0", "revoke"));
        var unauthAct = () => Store.Consents.RevokeConsentAsync(c2.Id, unauthorizedSid, "Unauthorized attempt");
        await unauthAct.Should().ThrowAsync<UnauthorizedAccessException>();

        var wrongMarkerRevoked = await Store.Consents.RevokeSystemConsentAsync(c1.Id, Guid.NewGuid(), new Sid("system:sync"), "Wrong marker");
        wrongMarkerRevoked.Should().BeFalse();

        var rightMarkerRevoked = await Store.Consents.RevokeSystemConsentAsync(c1.Id, syncMarker, new Sid("system:sync"), "Sync removed");
        rightMarkerRevoked.Should().BeTrue();

        var replacement = NewConsent(table.Identifier, Store.TenantA, new Sid("S-1-5-21-900-77"));
        var epochBefore = await Store.PolicyEpochs.GetTableEpochAsync(table.Identifier);
        await Store.Consents.ReplaceConsentsAsync([c2.Id], [replacement], Owner1Sid, "Declarative replace");
        var epochAfter = await Store.PolicyEpochs.GetTableEpochAsync(table.Identifier);
        epochAfter.Should().BeGreaterThan(epochBefore);

        (await Store.Consents.GetConsentByIdAsync(c2.Id))!.IsRevoked.Should().BeTrue();
        (await Store.Consents.GetConsentByIdAsync(replacement.Id))!.IsRevoked.Should().BeFalse();
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Decision_status_guard_rejects_invalid_transition()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("status_guard");
        var req = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table.Identifier,
            RequesterSid = RequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Status guard test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Store.TenantA
        });

        _ = await Store.ConsentApprovals.RejectConsentRequestAsync(req.Id, Owner1Sid, "Denied");

        var approveRejected = () => Store.ConsentApprovals.ApproveConsentRequestStepAsync(req.Id, Owner1Sid);
        await approveRejected.Should().ThrowAsync<ConsentRequestStatusConflictException>();

        var withdrewRejected = await Store.ConsentApprovals.WithdrawConsentRequestAsync(req.Id, RequesterSid);
        withdrewRejected.Should().BeFalse();

        var reqRace = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table.Identifier,
            RequesterSid = RequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Mid-tx race test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(5),
            TenantId = Store.TenantA
        });

        Store.SetConsentStatusUpdateSeam(async (conn, tx, ct) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "UPDATE CONSENT_REQUESTS SET status = 'WITHDRAWN' WHERE id = @id";
            var p = cmd.CreateParameter();
            p.ParameterName = "@id";
            p.Value = reqRace.Id.ToString();
            cmd.Parameters.Add(p);
            await cmd.ExecuteNonQueryAsync(ct);
        });

        try
        {
            var step = new ApprovalStep
            {
                Id = Guid.NewGuid(),
                ConsentRequestId = reqRace.Id,
                StepNumber = 1,
                ApproverSid = Owner1Sid,
                Decision = "APPROVED",
                DecidedAt = DateTimeOffset.UtcNow
            };
            var raceConsent = NewConsent(table.Identifier, Store.TenantA, GranteeSid, requestId: reqRace.Id);
            var raceAct = () => Store.ConsentApprovals.ExecuteAtomicApprovalWithConsentAsync(
                reqRace.Id,
                step,
                raceConsent,
                [],
                []);
            await raceAct.Should().ThrowAsync<ConsentRequestStatusConflictException>();
            (await Store.Consents.GetConsentByIdAsync(raceConsent.Id)).Should().BeNull();
        }
        finally
        {
            Store.SetConsentStatusUpdateSeam(null);
        }
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task Four_eyes_approval_and_expiry_extension_enforced()
    {
        if (!Store.IsAvailable) return;

        var table = await SeedTableAsync("four_eyes_tbl", "HIGH", requiresFourEyes: true);
        var req = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table.Identifier,
            RequesterSid = Owner1Sid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Self-approval check",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            TenantId = Store.TenantA
        });

        var selfApprove = () => Store.ConsentApprovals.ApproveConsentRequestStepAsync(req.Id, Owner1Sid);
        await selfApprove.Should().ThrowAsync<InvalidOperationException>();

        var req2 = await Store.ConsentApprovals.CreateConsentRequestAsync(new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table.Identifier,
            RequesterSid = RequesterSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = GranteeSid.Value,
            BusinessJustification = "Valid 4-eyes flow",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            TenantId = Store.TenantA
        });

        var afterFirst = await Store.ConsentApprovals.ApproveConsentRequestStepAsync(req2.Id, Owner1Sid);
        afterFirst.Status.Should().Be("PENDING_SECOND_APPROVAL");

        var afterSecond = await Store.ConsentApprovals.ApproveConsentRequestStepAsync(req2.Id, Owner2Sid);
        afterSecond.Status.Should().Be("APPROVED");

        await Store.ConsentApprovals.ActivateConsentAsync(req2.Id, Owner2Sid);
        var active = await Store.Consents.GetActiveConsentsForSubjectsAsync([GranteeSid], table.Identifier, DateTimeOffset.UtcNow, Store.TenantA);
        active.Should().ContainSingle();

        var consent = active[0];
        var shortenAct = () => Store.Consents.ExtendConsentExpiryAsync(consent.Id, consent.ValidTo.AddHours(-1));
        await shortenAct.Should().ThrowAsync<ConsentNotExtendableException>();

        var extendedValidTo = consent.ValidTo.AddDays(10);
        await Store.Consents.ExtendConsentExpiryAsync(consent.Id, extendedValidTo);
        var afterExtend = await Store.Consents.GetConsentByIdAsync(consent.Id);
        afterExtend!.ValidTo.Should().BeCloseTo(extendedValidTo, TimeSpan.FromSeconds(1));
        afterExtend.RenewalCount.Should().Be(1);
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlConsentContractTests(GovernancePostgresFixture fixture) : ConsentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "consent_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerConsentContractTests(GovernanceSqlServerFixture fixture) : ConsentContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "consent_contract");
}
