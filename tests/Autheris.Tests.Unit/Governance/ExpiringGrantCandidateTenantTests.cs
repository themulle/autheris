using System.Data;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Infrastructure.Persistence;
using Autheris.Tests.Unit.Persistence.DialectSql;
using Npgsql;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>
/// Owner grants of a catalog-wide assignment are stored under the platform tenant; the expiry sweep reads every tenant's consents and must
/// accept that bucket, while any other invalid stored tenant stays an integrity error.
/// </summary>
public sealed class ExpiringGrantCandidateTenantTests
{
    [Fact]
    public async Task ReadExpiringGrantCandidates_AcceptsPlatformTenantOwnerGrant()
    {
        using var conn = new RecordingDbConnection(() => new NpgsqlCommand());
        conn.EnqueueReader(Table(TenantId.Platform.Value));
        await using var cmd = conn.CreateCommand();

        var result = await GovernanceRepository.ReadExpiringGrantCandidatesAsync(cmd, CancellationToken.None);

        var candidate = result.ShouldHaveSingleItem();
        candidate.Consent.TenantId.ShouldBe(TenantId.Platform);
    }

    [Theory]
    [InlineData("_other")]
    [InlineData("")]
    public async Task ReadExpiringGrantCandidates_RejectsOtherInvalidStoredTenants(string stored)
    {
        using var conn = new RecordingDbConnection(() => new NpgsqlCommand());
        conn.EnqueueReader(Table(stored));
        await using var cmd = conn.CreateCommand();

        await Should.ThrowAsync<StoredTenantIntegrityException>(
            () => GovernanceRepository.ReadExpiringGrantCandidatesAsync(cmd, CancellationToken.None));
    }

    private static DataTable Table(string tenant)
    {
        var t = new DataTable();
        foreach (var name in new[]
                 {
                     "id", "table_id", "consent_request_id", "effect", "grantee_type", "grantee_sid", "valid_from",
                     "valid_to", "is_revoked", "source_name", "schema_name", "table_name", "tenant_id", "renewal_count", "grant_source",
                     "grant_record_seq", "profile_assignment_id", "expiry_notice_stage", "workflow_mode", "external_system_id"
                 })
        {
            t.Columns.Add(name, typeof(object));
        }

        t.Rows.Add(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), DBNull.Value, "Allow", "User", "S-1-5-21-1",
            "2026-01-01T00:00:00.0000000+00:00", DateTimeOffset.MaxValue.ToString("O"), 0, "sales", "dbo", "orders", tenant, 0, "OWNER", 1L,
            DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value);
        return t;
    }
}
