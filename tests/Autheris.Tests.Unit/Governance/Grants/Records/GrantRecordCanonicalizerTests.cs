using System.Text;
using System.Text.Json;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Grants.Records;

public sealed class GrantRecordCanonicalizerTests
{
    [Fact]
    public void Canonical_Json_IsStable_SortedKeys_UtcIso8601()
    {
        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var consentId = Guid.Parse("22222222-3333-4444-5555-666666666666");
        var requestId = Guid.Parse("33333333-4444-5555-6666-777777777777");
        var profileAssignmentId = Guid.Parse("44444444-5555-6666-7777-888888888888");
        var validFromUtc = new DateTimeOffset(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
        var validUntilUtc = new DateTimeOffset(2026, 12, 14, 10, 0, 0, TimeSpan.Zero);
        var decidedUtc = new DateTimeOffset(2026, 11, 14, 9, 59, 0, TimeSpan.Zero);
        var recordedUtc = new DateTimeOffset(2026, 11, 14, 10, 0, 1, TimeSpan.Zero);

        var recordUtc = new GrantRecord(
            TenantId: "tenant-alpha",
            Seq: 7,
            Id: id,
            RecordType: GrantRecordType.Issued,
            ConsentId: consentId,
            RequestId: requestId,
            GrantSource: ConsentGrantSource.ConsentRequest,
            ProfileAssignmentId: profileAssignmentId,
            GranteeType: "User",
            GranteeRef: "S-1-5-21-1001",
            ObjectId: "sales.dbo.orders",
            ScopeJson: "{\"columns\":[\"id\",\"total\"],\"access\":\"READ\"}",
            ValidFrom: validFromUtc,
            ValidUntil: validUntilUtc,
            RequestedBy: "alice@example.com",
            DecisionJson: "{\"decision\":\"APPROVED\",\"approver\":\"bob@example.com\"}",
            DecidedAt: decidedUtc,
            RecordedAt: recordedUtc,
            PrevHash: new string('0', 64),
            RecordHash: new string('a', 64),
            Signature: "sig-ignored-in-canonical-payload",
            KeyId: "k1");

        var recordOffset = recordUtc with
        {
            ValidFrom = validFromUtc.ToOffset(TimeSpan.FromHours(2)),
            ValidUntil = validUntilUtc.ToOffset(TimeSpan.FromHours(-5)),
            DecidedAt = decidedUtc.ToOffset(TimeSpan.FromHours(3)),
            RecordedAt = recordedUtc.ToOffset(TimeSpan.FromHours(-4)),
            ScopeJson = "{  \"access\" : \"READ\", \"columns\" : [ \"id\", \"total\" ] }",
            DecisionJson = "{ \"approver\": \"bob@example.com\", \"decision\": \"APPROVED\" }",
            RecordHash = new string('b', 64),
            Signature = "different-signature",
            KeyId = "k2"
        };

        var bytesA = GrantRecordCanonicalizer.CanonicalizeUtf8(recordUtc);
        var bytesB = GrantRecordCanonicalizer.CanonicalizeUtf8(recordOffset);
        bytesA.ShouldBe(bytesB);

        var canonical = Encoding.UTF8.GetString(bytesA);
        canonical.ShouldNotContain("\n");
        canonical.ShouldNotContain("\r");
        canonical.ShouldNotContain("sig-ignored-in-canonical-payload");
        canonical.ShouldContain("\"decided_at\":\"2026-11-14T09:59:00.0000000Z\"");
        canonical.ShouldContain("\"recorded_at\":\"2026-11-14T10:00:01.0000000Z\"");
        canonical.ShouldContain("\"valid_from\":\"2026-11-14T10:00:00.0000000Z\"");
        canonical.ShouldContain("\"valid_until\":\"2026-12-14T10:00:00.0000000Z\"");

        using var doc = JsonDocument.Parse(canonical);
        var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        var sortedKeys = keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();
        keys.ShouldBe(sortedKeys);
        keys.ShouldBe([
            "consent_id",
            "decided_at",
            "decision_json",
            "grant_source",
            "grantee_ref",
            "grantee_type",
            "id",
            "object_id",
            "prev_hash",
            "profile_assignment_id",
            "record_type",
            "recorded_at",
            "request_id",
            "requested_by",
            "scope_json",
            "seq",
            "tenant_id",
            "valid_from",
            "valid_until"
        ]);
    }
}
