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

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
