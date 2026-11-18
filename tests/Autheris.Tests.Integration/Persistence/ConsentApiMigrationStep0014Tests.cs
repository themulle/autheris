using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Tests.Integration.Governance;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Persistence;

public sealed class ConsentApiMigrationStep0014Tests
{
    [Trait("Category", "Security")]
    [Fact]
    public async Task Consents_GrantSource_Backfill_DefaultsTo_ConsentRequest_NeverDeclarative()
    {
        var step = SqlMigrationStep.LoadEmbedded("postgresql").Single(s => s.Name == "consent_api");

        // The pre-step shape lives in a dedicated schema of a throwaway clone; the unqualified step SQL resolves there only.
        await using var conn = new NpgsqlConnection(await GovernanceTestDatabase.CreateAsync("consent_api_step"));
        await conn.OpenAsync();

        await Exec(conn, """
            CREATE SCHEMA pre_step;
            SET search_path TO pre_step;
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

        foreach (var batch in step.Batches)
        {
            await Exec(conn, batch);
        }

        await using (var consent = new NpgsqlCommand("SELECT grant_source, grant_record_seq, profile_assignment_id, expiry_notice_sent_at FROM CONSENTS WHERE id = 'consent-legacy-1'", conn))
        await using (var r = await consent.ExecuteReaderAsync())
        {
            (await r.ReadAsync()).ShouldBeTrue();
            r.GetString(0).ShouldBe("CONSENT_REQUEST");
            r.GetString(0).ShouldNotBe("DECLARATIVE");
            r.GetInt64(1).ShouldBe(0L);
            (await r.IsDBNullAsync(2)).ShouldBeTrue();
            (await r.IsDBNullAsync(3)).ShouldBeTrue();
        }

        await using (var request = new NpgsqlCommand("SELECT version, workflow_mode, external_reference, requested_valid_from, request_kind FROM CONSENT_REQUESTS WHERE id = 'req-legacy-1'", conn))
        await using (var r = await request.ExecuteReaderAsync())
        {
            (await r.ReadAsync()).ShouldBeTrue();
            r.GetInt32(0).ShouldBe(1);
            r.GetString(1).ShouldBe("INTERNAL");
            r.GetString(2).ShouldBe("INC-9001");
            r.GetFieldValue<DateTime>(3).ShouldBe(new DateTime(2026, 11, 10, 8, 0, 0, DateTimeKind.Utc));
            r.GetString(4).ShouldBe("CONSENT");
        }
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
