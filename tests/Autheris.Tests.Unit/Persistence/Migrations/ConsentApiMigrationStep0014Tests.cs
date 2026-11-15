using System.Data;
using System.Data.Common;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Infrastructure.Persistence.Migrations;
using Autheris.Infrastructure.Persistence.Migrations.Shape;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.Migrations;

public sealed class ConsentApiMigrationStep0014Tests
{
    [Fact]
    public void Migration_0014_Passes_StepSqlLint_And_Matches_ShapeJson()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var steps = SqlMigrationStep.LoadEmbedded(engine);
            var step = steps.Single(s => s.Version == 15 && s.Name == "consent_api");
            step.Phase3Marker.ShouldBe("MIG-0015");

            StepSqlLint.Validate(step.Sql, step.GuardSql, step.Phase3Marker).ShouldBeEmpty();

            var repoRoot = FindRepoRoot();
            var checksumsPath = Path.Combine(
                repoRoot, "tests", "Autheris.Tests.Architecture", "Golden", "GovernanceSteps", $"checksums.{engine}.txt");
            var recordedLine = File.ReadAllLines(checksumsPath)
                .Single(l => l.StartsWith("0015 consent_api ", StringComparison.Ordinal));
            var recordedSha = recordedLine.Split(' ', StringSplitOptions.RemoveEmptyEntries)[2];
            step.Checksum.ShouldBe(recordedSha);

            var shape = GovernanceSchemaShapeLoader.LoadAt(
                engine,
                14,
                steps.ToDictionary(s => s.Version, s => s.ShapeDelta));

            var expectedTables = new[]
            {
                "PROFILE_ASSIGNMENT_ITEMS",
                "CONSENT_WORKFLOW_ROUTES",
                "EXTERNAL_WORKFLOW_SYSTEMS",
                "GRANT_RECORDS",
                "GRANT_RECORD_HEADS",
                "CONSENT_EVENTS",
                "CONSENT_EVENT_DELIVERIES",
                "CATALOG_OBJECT_VERSIONS",
                "CATALOG_CHANGES"
            };

            var registeredNames = Enum.GetValues<GovernanceTable>()
                .Select(t => t.ToSqlName())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var tableName in expectedTables)
            {
                registeredNames.ShouldContain(tableName);
                shape.Tables.Any(t => string.Equals(t.Name, tableName, StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
            }

            var consentRequests = shape.Tables.Single(t => string.Equals(t.Name, "CONSENT_REQUESTS", StringComparison.OrdinalIgnoreCase));
            consentRequests.Columns.Select(c => c.Name).ShouldContain("version");
            consentRequests.Columns.Select(c => c.Name).ShouldContain("workflow_mode");
            consentRequests.Columns.Select(c => c.Name).ShouldContain("external_system_id");
            consentRequests.Columns.Select(c => c.Name).ShouldContain("external_reference");
            consentRequests.Columns.Select(c => c.Name).ShouldContain("requested_valid_from");
            consentRequests.Columns.Select(c => c.Name).ShouldContain("updated_at");
            consentRequests.Columns.Select(c => c.Name).ShouldContain("request_kind");
            consentRequests.Columns.Select(c => c.Name).ShouldContain("profile_id");
            consentRequests.Indexes.Any(i => string.Equals(i.Name, "IX_CONSENT_REQUESTS_TENANT_UPDATED", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();

            var consents = shape.Tables.Single(t => string.Equals(t.Name, "CONSENTS", StringComparison.OrdinalIgnoreCase));
            consents.Columns.Select(c => c.Name).ShouldContain("expiry_notice_sent_at");
            consents.Columns.Select(c => c.Name).ShouldContain("grant_record_seq");
            consents.Columns.Select(c => c.Name).ShouldContain("grant_source");
            consents.Columns.Select(c => c.Name).ShouldContain("profile_assignment_id");
            consents.Indexes.Any(i => string.Equals(i.Name, "IX_CONSENTS_TENANT_GRANT_SOURCE", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
        }
    }

    [Trait("Category", "Security")]
    [Fact]
    public void Consents_GrantSource_DefaultsTo_ConsentRequest_NeverDeclarative()
    {
        foreach (var engine in new[] { "postgresql", "sqlserver" })
        {
            var step = SqlMigrationStep.LoadEmbedded(engine).Single(s => s.Version == 15 && s.Name == "consent_api");
            step.Sql.ShouldContain("grant_source");
            step.Sql.ShouldContain("'CONSENT_REQUEST'");
            step.Sql.ShouldNotContain("DECLARATIVE", Case.Insensitive);
        }

        new Consent().GrantSource.ShouldBe(ConsentGrantSource.ConsentRequest);
        ConsentGrantSource.ConsentRequest.ToSqlString().ShouldBe("CONSENT_REQUEST");
        ConsentGrantSource.Declarative.ToSqlString().ShouldBe("DECLARATIVE");
        ConsentWorkflowSqlMapping.ParseGrantSourceOrDefault(null).ShouldBe(ConsentGrantSource.ConsentRequest);
        ConsentWorkflowSqlMapping.ParseGrantSourceOrDefault("UNKNOWN_SOURCE").ShouldBe(ConsentGrantSource.ConsentRequest);
    }

    [Fact]
    public void ConsentRequest_And_Consent_NewColumns_RoundTrip()
    {
        var extSysId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profileAssignId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
        var reqValidFrom = now.AddMinutes(-5);
        var updatedAt = now.AddMinutes(5);
        var expiryNotice = now.AddDays(2);

        var reqId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        using var reqReader = new SingleRowReader([
            reqId.ToString(),
            tableId.ToString(),
            "S-1-5-21-500",
            "User",
            "S-1-5-21-500",
            "Need analytics",
            "PENDING_EXTERNAL_APPROVAL",
            now.AddMinutes(-10).ToString("O"),
            now.AddDays(30).ToString("O"),
            "sales",
            "dbo",
            "orders",
            "INC-123",
            "tenant-rt",
            "[\"S-1-5-21-500\"]",
            "{}",
            null,
            3,
            "EXTERNAL",
            extSysId.ToString(),
            "EXT-REF-99",
            reqValidFrom,
            updatedAt,
            "PROFILE_ASSIGNMENT",
            profileId.ToString()
        ]);
        reqReader.Read().ShouldBeTrue();
        var hydratedReq = ConsentSqlCore.ReadFullConsentRequestRow(reqReader);
        hydratedReq.Version.ShouldBe(3);
        hydratedReq.WorkflowMode.ShouldBe(ConsentWorkflowMode.External);
        hydratedReq.ExternalSystemId.ShouldBe(extSysId);
        hydratedReq.ExternalReference.ShouldBe("EXT-REF-99");
        hydratedReq.RequestedValidFrom.ShouldBe(reqValidFrom);
        hydratedReq.UpdatedAt.ShouldBe(updatedAt);
        hydratedReq.RequestKind.ShouldBe(ConsentRequestKind.ProfileAssignment);
        hydratedReq.ProfileId.ShouldBe(profileId);

        var consentId = Guid.NewGuid();
        using var subjectReader = new SingleRowReader([
            consentId.ToString(),
            tableId.ToString(),
            reqId.ToString(),
            "Allow",
            "User",
            "S-1-5-21-500",
            null,
            null,
            now.AddHours(-1).ToString("O"),
            now.AddDays(10).ToString("O"),
            0,
            "sales",
            "dbo",
            "orders",
            "tenant-rt",
            "DECLARATIVE",
            42L,
            profileAssignId.ToString(),
            expiryNotice
        ]);

        var list = new List<Consent>();
        ConsentSqlCore.ReadAllSubjectConsentRows(
            subjectReader,
            now,
            new HashSet<string>(["S-1-5-21-500"], StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<Guid>(),
            list);

        var hydratedConsent = list.ShouldHaveSingleItem();
        hydratedConsent.GrantSource.ShouldBe(ConsentGrantSource.Declarative);
        hydratedConsent.GrantRecordSeq.ShouldBe(42L);
        hydratedConsent.ProfileAssignmentId.ShouldBe(profileAssignId);
        hydratedConsent.ExpiryNoticeSentAt.ShouldBe(expiryNotice);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Autheris.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }

    private sealed class SingleRowReader(object?[] values) : DbDataReader
    {
        private bool _read;
        public override bool Read() => !_read && (_read = true);
        public override int FieldCount => values.Length;
        public override bool IsDBNull(int ordinal) => values[ordinal] is null or DBNull;
        public override object GetValue(int ordinal) => values[ordinal] ?? DBNull.Value;
        public override string GetString(int ordinal) => (string)values[ordinal]!;
        public override int GetInt32(int ordinal) => Convert.ToInt32(values[ordinal]);
        public override long GetInt64(int ordinal) => Convert.ToInt64(values[ordinal]);
        public override bool HasRows => true;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override int Depth => 0;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => Convert.ToBoolean(values[ordinal]);
        public override byte GetByte(int ordinal) => Convert.ToByte(values[ordinal]);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => throw new NotSupportedException();
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override string GetDataTypeName(int ordinal) => GetFieldType(ordinal).Name;
        public override DateTime GetDateTime(int ordinal) => Convert.ToDateTime(values[ordinal]);
        public override decimal GetDecimal(int ordinal) => Convert.ToDecimal(values[ordinal]);
        public override double GetDouble(int ordinal) => Convert.ToDouble(values[ordinal]);
        public override Type GetFieldType(int ordinal) => values[ordinal]?.GetType() ?? typeof(object);
        public override float GetFloat(int ordinal) => Convert.ToSingle(values[ordinal]);
        public override Guid GetGuid(int ordinal) => values[ordinal] is Guid g ? g : Guid.Parse((string)values[ordinal]!);
        public override short GetInt16(int ordinal) => Convert.ToInt16(values[ordinal]);
        public override string GetName(int ordinal) => $"col{ordinal}";
        public override int GetOrdinal(string name) => throw new NotSupportedException();
        public override int GetValues(object[] dst) { values.CopyTo(dst, 0); return values.Length; }
        public override bool NextResult() => false;
        public override System.Collections.IEnumerator GetEnumerator() => values.GetEnumerator();
    }
}
