using System.Data.Common;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

public sealed class ConsentApiMigrationStep0014Tests
{
    [Trait("Category", "Security")]
    [Fact]
    public void Consents_GrantSource_Backfill_DefaultsTo_ConsentRequest_NeverDeclarative()
    {
        using var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();

        Exec(conn, """
            CREATE TABLE CONSENT_REQUESTS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                requester_sid TEXT NOT NULL,
                requested_grantee_type TEXT NOT NULL,
                requested_grantee_ref TEXT NOT NULL,
                business_justification TEXT NOT NULL,
                status TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                requested_valid_to TEXT NOT NULL,
                itsm_ticket_id TEXT NULL,
                tenant_id TEXT NULL,
                requester_identifiers_json TEXT NULL,
                context_json TEXT NULL
            );
            CREATE TABLE CONSENTS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                consent_request_id TEXT NULL,
                effect TEXT NOT NULL,
                grantee_type TEXT NOT NULL,
                grantee_sid TEXT NULL,
                grantee_sid_norm TEXT NULL,
                role_id TEXT NULL,
                role_name TEXT NULL,
                valid_from TEXT NOT NULL,
                valid_to TEXT NOT NULL,
                is_revoked INTEGER NOT NULL DEFAULT 0,
                tenant_id TEXT NULL
            );
            INSERT INTO CONSENT_REQUESTS (id, table_id, requester_sid, requested_grantee_type, requested_grantee_ref, business_justification, status, requested_at, requested_valid_to, itsm_ticket_id, tenant_id)
            VALUES ('req-legacy-1', 'tbl-1', 'S-1-5-1', 'User', 'S-1-5-1', 'Need read', 'PENDING', '2026-11-10T08:00:00Z', '2026-12-10T08:00:00Z', 'INC-9001', 't-1');
            INSERT INTO CONSENTS (id, table_id, consent_request_id, effect, grantee_type, grantee_sid, grantee_sid_norm, valid_from, valid_to, is_revoked, tenant_id)
            VALUES ('consent-legacy-1', 'tbl-1', 'req-legacy-1', 'Allow', 'User', 'S-1-5-1', 'S-1-5-1', '2026-11-10T08:00:00Z', '2026-12-10T08:00:00Z', 0, 't-1');
            """);

        Exec(conn, """
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN version INT NOT NULL DEFAULT 1;
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN workflow_mode VARCHAR(16) NOT NULL DEFAULT 'INTERNAL';
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN external_system_id VARCHAR(36) NULL;
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN external_reference VARCHAR(256) NULL;
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN requested_valid_from TEXT NULL;
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN updated_at TEXT NULL;
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN request_kind VARCHAR(24) NOT NULL DEFAULT 'CONSENT';
            ALTER TABLE CONSENT_REQUESTS ADD COLUMN profile_id VARCHAR(36) NULL;
            UPDATE CONSENT_REQUESTS
               SET external_reference = COALESCE(external_reference, itsm_ticket_id),
                   requested_valid_from = COALESCE(requested_valid_from, requested_at),
                   updated_at = COALESCE(updated_at, requested_at);
            ALTER TABLE CONSENTS ADD COLUMN expiry_notice_sent_at TEXT NULL;
            ALTER TABLE CONSENTS ADD COLUMN grant_record_seq BIGINT NOT NULL DEFAULT 0;
            ALTER TABLE CONSENTS ADD COLUMN grant_source VARCHAR(24) NOT NULL DEFAULT 'CONSENT_REQUEST';
            ALTER TABLE CONSENTS ADD COLUMN profile_assignment_id VARCHAR(36) NULL;
            """);

        using (var checkConsent = conn.CreateCommand())
        {
            checkConsent.CommandText = "SELECT grant_source, grant_record_seq, profile_assignment_id, expiry_notice_sent_at FROM CONSENTS WHERE id = 'consent-legacy-1'";
            using var r = checkConsent.ExecuteReader();
            r.Read().ShouldBeTrue();
            r.GetString(0).ShouldBe("CONSENT_REQUEST");
            r.GetString(0).ShouldNotBe("DECLARATIVE");
            r.GetInt64(1).ShouldBe(0L);
            r.IsDBNull(2).ShouldBeTrue();
            r.IsDBNull(3).ShouldBeTrue();
        }

        using (var checkReq = conn.CreateCommand())
        {
            checkReq.CommandText = "SELECT version, workflow_mode, external_reference, requested_valid_from, updated_at, request_kind FROM CONSENT_REQUESTS WHERE id = 'req-legacy-1'";
            using var r = checkReq.ExecuteReader();
            r.Read().ShouldBeTrue();
            r.GetInt32(0).ShouldBe(1);
            r.GetString(1).ShouldBe("INTERNAL");
            r.GetString(2).ShouldBe("INC-9001");
            r.GetString(3).ShouldBe("2026-11-10T08:00:00Z");
            r.GetString(4).ShouldBe("2026-11-10T08:00:00Z");
            r.GetString(5).ShouldBe("CONSENT");
        }
    }

    [Fact]
    public async Task ConsentRequest_And_Consent_NewColumns_RoundTrip()
    {
        var dbName = $"consent_roundtrip_{Guid.NewGuid():N}";
        var cs = $"Data Source={dbName};Mode=Memory;Cache=Shared";
        await using var keepAlive = new SqliteConnection(cs);
        await keepAlive.OpenAsync();
        SeedRoundTripSchema(keepAlive);

        var dialect = new PostgreSqlGovernanceDialect(() =>
        {
            var c = new SqliteConnection(cs);
            c.CreateFunction<long, int>("pg_advisory_xact_lock", _ => 1);
            return c;
        });
        var repo = new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance,
            auditAnchorStore: new InMemoryAuditChainAnchorStore(),
            grantWriter: new DirectConsentGrantWriter(new GrantRecordSqlCore(dialect)));
        repo.InitializeAudit();

        var tableId = Guid.NewGuid();
        var tableIdent = new TableIdentifier("sales", "dbo", "orders");
        var tenant = new TenantId("tenant-rt");
        SeedTableRow(keepAlive, tableId, tableIdent, tenant);

        var extSysId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var reqValidFrom = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var reqUpdatedAt = new DateTimeOffset(2026, 11, 14, 12, 5, 0, TimeSpan.Zero);
        var request = new ConsentRequest
        {
            Id = Guid.NewGuid(),
            TableId = tableId,
            TableIdentifier = tableIdent,
            RequesterSid = new Sid("S-1-5-21-500"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-500",
            BusinessJustification = "Analytics reporting",
            Status = "PENDING_EXTERNAL_APPROVAL",
            RequestedAt = reqValidFrom.AddMinutes(-10),
            RequestedValidTo = reqValidFrom.AddDays(30),
            TenantId = tenant,
            Version = 3,
            WorkflowMode = ConsentWorkflowMode.External,
            ExternalSystemId = extSysId,
            ExternalReference = "EXT-REQ-99",
            RequestedValidFrom = reqValidFrom,
            UpdatedAt = reqUpdatedAt,
            RequestKind = ConsentRequestKind.ProfileAssignment,
            ProfileId = profileId
        };

        await repo.CreateConsentRequestAsync(request, CancellationToken.None);

        var fetchedReq = await repo.GetConsentRequestAsync(request.Id, CancellationToken.None);
        fetchedReq.ShouldNotBeNull();
        fetchedReq.Version.ShouldBe(3);
        fetchedReq.WorkflowMode.ShouldBe(ConsentWorkflowMode.External);
        fetchedReq.ExternalSystemId.ShouldBe(extSysId);
        fetchedReq.ExternalReference.ShouldBe("EXT-REQ-99");
        fetchedReq.RequestedValidFrom.ShouldBe(reqValidFrom);
        fetchedReq.UpdatedAt.ShouldBe(reqUpdatedAt);
        fetchedReq.RequestKind.ShouldBe(ConsentRequestKind.ProfileAssignment);
        fetchedReq.ProfileId.ShouldBe(profileId);

        var byRequester = await repo.GetRequestsByRequesterSidAsync(request.RequesterSid, null, CancellationToken.None);
        var summaryReq = byRequester.ShouldHaveSingleItem();
        summaryReq.Version.ShouldBe(3);
        summaryReq.WorkflowMode.ShouldBe(ConsentWorkflowMode.External);
        summaryReq.ExternalSystemId.ShouldBe(extSysId);
        summaryReq.ExternalReference.ShouldBe("EXT-REQ-99");
        summaryReq.RequestedValidFrom.ShouldBe(reqValidFrom);
        summaryReq.UpdatedAt.ShouldBe(reqUpdatedAt);
        summaryReq.RequestKind.ShouldBe(ConsentRequestKind.ProfileAssignment);
        summaryReq.ProfileId.ShouldBe(profileId);

        var profileAssignId = Guid.NewGuid();
        var expiryNoticeSent = new DateTimeOffset(2026, 12, 11, 12, 0, 0, TimeSpan.Zero);
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = tableId,
            TableIdentifier = tableIdent,
            TenantId = tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-500"),
            CreatedBySid = new Sid("S-1-5-21-900"),
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(14),
            GrantSource = ConsentGrantSource.Declarative,
            GrantRecordSeq = 42L,
            ProfileAssignmentId = profileAssignId,
            ExpiryNoticeSentAt = expiryNoticeSent
        };

        await repo.CreateConsentAsync(consent, CancellationToken.None);

        var fetchedConsent = await repo.GetConsentByIdAsync(consent.Id, CancellationToken.None);
        fetchedConsent.ShouldNotBeNull();
        fetchedConsent.GrantSource.ShouldBe(ConsentGrantSource.Declarative);
        fetchedConsent.GrantRecordSeq.ShouldBe(42L);
        fetchedConsent.ProfileAssignmentId.ShouldBe(profileAssignId);
        fetchedConsent.ExpiryNoticeSentAt.ShouldBe(expiryNoticeSent);

        var allActive = await repo.GetAllActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-500")], null, DateTimeOffset.UtcNow, tenant, CancellationToken.None);
        var allRow = allActive.ShouldHaveSingleItem();
        allRow.GrantSource.ShouldBe(ConsentGrantSource.Declarative);
        allRow.GrantRecordSeq.ShouldBe(42L);
        allRow.ProfileAssignmentId.ShouldBe(profileAssignId);
        allRow.ExpiryNoticeSentAt.ShouldBe(expiryNoticeSent);
    }

    private static void SeedRoundTripSchema(SqliteConnection conn)
    {
        Exec(conn, """
            CREATE TABLE TABLES (
                id TEXT PRIMARY KEY,
                source_name TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                tenant_id TEXT NULL,
                requires_four_eyes INTEGER NOT NULL DEFAULT 0,
                sensitivity TEXT NOT NULL DEFAULT 'INTERNAL'
            );
            CREATE TABLE CONSENT_REQUESTS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                requester_sid TEXT NOT NULL,
                requested_grantee_type TEXT NOT NULL,
                requested_grantee_ref TEXT NOT NULL,
                business_justification TEXT NOT NULL,
                status TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                requested_valid_to TEXT NOT NULL,
                itsm_ticket_id TEXT NULL,
                tenant_id TEXT NULL,
                requester_identifiers_json TEXT NULL,
                context_json TEXT NULL,
                requested_write_rules_json TEXT NULL,
                version INT NOT NULL DEFAULT 1,
                workflow_mode TEXT NOT NULL DEFAULT 'INTERNAL',
                external_system_id TEXT NULL,
                external_reference TEXT NULL,
                requested_valid_from TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                request_kind TEXT NOT NULL DEFAULT 'CONSENT',
                profile_id TEXT NULL
            );
            CREATE TABLE APPROVAL_STEPS (
                id TEXT PRIMARY KEY,
                consent_request_id TEXT NOT NULL,
                step_number INTEGER NOT NULL,
                approver_sid TEXT NOT NULL,
                decision TEXT NOT NULL,
                rejection_reason TEXT NULL,
                decided_at TEXT NULL
            );
            CREATE TABLE CONSENTS (
                id TEXT PRIMARY KEY,
                table_id TEXT NOT NULL,
                consent_request_id TEXT NULL,
                effect TEXT NOT NULL,
                grantee_type TEXT NOT NULL,
                grantee_sid TEXT NULL,
                grantee_sid_norm TEXT NULL,
                role_id TEXT NULL,
                role_name TEXT NULL,
                valid_from TEXT NOT NULL,
                valid_to TEXT NOT NULL,
                is_revoked INTEGER NOT NULL DEFAULT 0,
                revoked_by_sid TEXT NULL,
                revoked_at TEXT NULL,
                revoke_reason TEXT NULL,
                tenant_id TEXT NULL,
                description TEXT NULL,
                renewal_count INTEGER NOT NULL DEFAULT 0,
                grant_source TEXT NOT NULL DEFAULT 'CONSENT_REQUEST',
                grant_record_seq INTEGER NOT NULL DEFAULT 0,
                profile_assignment_id TEXT NULL,
                expiry_notice_sent_at TEXT NULL
            );
            CREATE TABLE CONSENT_COLUMN_RULES (
                id TEXT PRIMARY KEY,
                consent_id TEXT NOT NULL,
                table_column_id TEXT NOT NULL,
                column_name TEXT NOT NULL,
                access_level INTEGER NOT NULL,
                applicable_scopes TEXT NULL
            );
            CREATE TABLE CONSENT_ROW_FILTERS (
                id TEXT PRIMARY KEY,
                consent_id TEXT NOT NULL,
                filter_group TEXT NOT NULL,
                table_column_id TEXT NULL,
                column_name TEXT NULL,
                operator TEXT NULL,
                value_type TEXT NULL,
                value_json TEXT NULL,
                value_source TEXT NULL,
                user_attribute TEXT NULL,
                filter_type TEXT NULL,
                dependent_table TEXT NULL,
                dependent_table_alias TEXT NULL,
                foreign_key_column TEXT NULL,
                primary_key_column TEXT NULL,
                subquery_predicate_json TEXT NULL,
                target_temporal_column TEXT NULL,
                dependent_valid_from_column TEXT NULL,
                dependent_valid_to_column TEXT NULL,
                target_table_alias TEXT NULL,
                additional_hops_json TEXT NULL,
                key_set_strategy TEXT NULL,
                key_set_duration_seconds INTEGER NULL,
                key_set_max_keys INTEGER NULL
            );
            CREATE TABLE CONSENT_WRITE_RULES (
                id TEXT PRIMARY KEY,
                tenant_id TEXT NOT NULL,
                consent_id TEXT NOT NULL,
                operation INTEGER NOT NULL,
                row_scope INTEGER NOT NULL,
                row_filter_json TEXT NULL,
                max_rows_per_statement INTEGER NULL,
                columns_json TEXT NOT NULL
            );
            CREATE TABLE POLICY_EPOCHS (
                table_id TEXT PRIMARY KEY,
                domain TEXT NOT NULL,
                schema_name TEXT NOT NULL,
                table_name TEXT NOT NULL,
                epoch INTEGER NOT NULL,
                change_version INTEGER NOT NULL DEFAULT 0,
                updated_at TEXT NOT NULL
            );
            CREATE TABLE AUDIT_LOG_ENTRIES (
                id TEXT PRIMARY KEY,
                seq INTEGER NOT NULL,
                occurred_at TEXT NOT NULL,
                event_type TEXT NOT NULL,
                actor_sid TEXT NOT NULL,
                target_table TEXT NOT NULL,
                target_column TEXT NULL,
                decision TEXT NULL,
                trace_id TEXT NULL,
                details_json TEXT NULL,
                prev_hash TEXT NOT NULL,
                entry_hash TEXT NOT NULL,
                signature TEXT NULL,
                tenant_id TEXT NULL
            );
            """);
    }

    private static void SeedTableRow(SqliteConnection conn, Guid tableId, TableIdentifier ident, TenantId tenant)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO TABLES (id, source_name, schema_name, table_name, tenant_id) VALUES (@id, @d, @s, @t, @ten)";
        cmd.Parameters.AddWithValue("@id", tableId.ToString());
        cmd.Parameters.AddWithValue("@d", ident.Domain);
        cmd.Parameters.AddWithValue("@s", ident.Schema);
        cmd.Parameters.AddWithValue("@t", ident.TableName);
        cmd.Parameters.AddWithValue("@ten", tenant.Value);
        cmd.ExecuteNonQuery();
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private sealed class DirectConsentGrantWriter(IGrantRecordStore store) : IGrantWriter
    {
        public async Task<GrantRecord> InsertAndGrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default)
        {
            var seq = consent.GrantRecordSeq > 0 ? consent.GrantRecordSeq : 1L;
            await store.InsertConsentRowAsync(transaction, consent, seq, ct).ConfigureAwait(false);
            return new GrantRecord(input.TenantId, seq, Guid.NewGuid(), GrantRecordType.Issued, consent.Id, input.RequestId, consent.GrantSource, consent.ProfileAssignmentId, input.GranteeType ?? "User", input.GranteeRef ?? "u", input.ObjectId ?? "t", input.ScopeJson, input.ValidFrom, input.ValidUntil, input.RequestedBy, input.DecisionJson, input.DecidedAt, input.DecidedAt, GrantRecordChain.GenesisHash);
        }

        public Task<GrantRecord> GrantAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RevokeAsync(DbTransaction transaction, Consent consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordRejectionAsync(DbTransaction transaction, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<GrantRecord> RecordExpiryAsync(DbTransaction transaction, Consent? consent, GrantRecordInput input, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
