namespace Autheris.Infrastructure.Persistence;

using System;
using System.Data;
using System.Data.Common;
using System.Globalization;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Shared SQL column lists, parameter binders, and type-safe readers for consent and request workflow columns.
/// </summary>
internal static class ConsentWorkflowSql
{
    public const string ConsentColumns = "c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_sent_at";
    public const string RequestColumns = "r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id";

    public static void BindConsentParameters(
        DbCommand cmd,
        ConsentGrantSource grantSource,
        long grantRecordSeq,
        Guid? profileAssignmentId,
        DateTimeOffset? expiryNoticeSentAt)
    {
        cmd.AddBound("@grantSource", grantSource.ToSqlString());
        cmd.AddBound("@grantRecordSeq", grantRecordSeq, DbType.Int64);
        cmd.AddBound("@profileAssignmentId", (object?)profileAssignmentId?.ToString() ?? DBNull.Value);
        cmd.AddBound("@expiryNoticeSentAt", expiryNoticeSentAt.HasValue ? expiryNoticeSentAt.Value.ToUniversalTime() : DBNull.Value, DbType.DateTimeOffset);
    }

    public static void BindRequestParameters(DbCommand cmd, ConsentRequest request)
    {
        var requestedValidFrom = (request.RequestedValidFrom ?? request.RequestedAt).ToUniversalTime();
        var updatedAt = (request.UpdatedAt ?? request.RequestedAt).ToUniversalTime();
        cmd.AddBound("@version", request.Version <= 0 ? 1 : request.Version, DbType.Int32);
        cmd.AddBound("@workflowMode", request.WorkflowMode.ToSqlString());
        cmd.AddBound("@externalSystemId", (object?)request.ExternalSystemId?.ToString() ?? DBNull.Value);
        cmd.AddBound("@externalReference", (object?)request.ExternalReference ?? DBNull.Value);
        cmd.AddBound("@reqValidFrom", requestedValidFrom, DbType.DateTimeOffset);
        cmd.AddBound("@updatedAt", updatedAt, DbType.DateTimeOffset);
        cmd.AddBound("@requestKind", request.RequestKind.ToSqlString());
        cmd.AddBound("@profileId", (object?)request.ProfileId?.ToString() ?? DBNull.Value);
    }

    public static ConsentGrantSource ReadGrantSource(DbDataReader reader, int ordinal) =>
        reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
            ? ConsentWorkflowSqlMapping.ParseGrantSourceOrDefault(reader.GetString(ordinal))
            : ConsentGrantSource.ConsentRequest;

    public static long ReadInt64OrZero(DbDataReader reader, int ordinal) =>
        reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
            ? Convert.ToInt64(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
            : 0L;

    public static int ReadVersion(DbDataReader reader, int ordinal) =>
        reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
            ? Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture)
            : 1;

    public static Guid? ReadOptionalGuid(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
            return null;

        var raw = reader.GetValue(ordinal);
        if (raw is Guid g) return g;
        var s = Convert.ToString(raw, CultureInfo.InvariantCulture);
        return !string.IsNullOrWhiteSpace(s) && Guid.TryParse(s, out var parsed) ? parsed : null;
    }

    public static string? ReadOptionalString(DbDataReader reader, int ordinal) =>
        reader.FieldCount > ordinal && !reader.IsDBNull(ordinal) ? reader.GetString(ordinal) : null;

    public static ConsentWorkflowMode ReadWorkflowMode(DbDataReader reader, int ordinal) =>
        reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
            ? ConsentWorkflowSqlMapping.ParseWorkflowModeOrDefault(reader.GetString(ordinal))
            : ConsentWorkflowMode.Internal;

    public static ConsentRequestKind ReadRequestKind(DbDataReader reader, int ordinal) =>
        reader.FieldCount > ordinal && !reader.IsDBNull(ordinal)
            ? ConsentWorkflowSqlMapping.ParseRequestKindOrDefault(reader.GetString(ordinal))
            : ConsentRequestKind.Consent;

    public static DateTimeOffset? ReadOptionalTimestamp(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
            return null;

        var raw = reader.GetValue(ordinal);
        return raw switch
        {
            DateTimeOffset dto => dto,
            DateTime dt => new DateTimeOffset(DateTime.SpecifyKind(dt, dt.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : dt.Kind)),
            string s when !string.IsNullOrWhiteSpace(s) => DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => DateTimeOffset.Parse(Convert.ToString(raw, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
        };
    }

    public static Consent BuildConsent(
        Guid id,
        Guid tableId,
        TableIdentifier tableIdentifier,
        Guid? consentRequestId,
        ConsentEffect effect,
        GranteeType granteeType,
        Sid? granteeSid,
        Guid? roleId,
        string? roleName,
        DateTimeOffset validFrom,
        DateTimeOffset validTo,
        bool isRevoked,
        TenantId tenantId,
        DbDataReader reader,
        int workflowStartOrdinal,
        int renewalCount = 0,
        Sid? revokedBySid = null,
        DateTimeOffset? revokedAt = null,
        string? revokeReason = null) =>
        new()
        {
            Id = id,
            TableId = tableId,
            TableIdentifier = tableIdentifier,
            ConsentRequestId = consentRequestId,
            Effect = effect,
            GranteeType = granteeType,
            GranteeSid = granteeSid,
            RoleId = roleId,
            RoleName = roleName,
            ValidFrom = validFrom,
            ValidTo = validTo,
            IsRevoked = isRevoked,
            RevokedBySid = revokedBySid,
            RevokedAt = revokedAt,
            RevokeReason = revokeReason,
            TenantId = tenantId,
            RenewalCount = renewalCount,
            GrantSource = ReadGrantSource(reader, workflowStartOrdinal),
            GrantRecordSeq = ReadInt64OrZero(reader, workflowStartOrdinal + 1),
            ProfileAssignmentId = ReadOptionalGuid(reader, workflowStartOrdinal + 2),
            ExpiryNoticeSentAt = ReadOptionalTimestamp(reader, workflowStartOrdinal + 3)
        };

    public static ConsentRequest BuildConsentRequest(
        DbDataReader reader,
        Guid id,
        Guid tableId,
        Sid requesterSid,
        GranteeType granteeType,
        string granteeRef,
        string justification,
        string status,
        DateTimeOffset requestedAt,
        DateTimeOffset requestedValidTo,
        TableIdentifier tableIdentifier,
        TenantId tenantId,
        List<string> requesterIdentifiers,
        string? contextJson,
        IReadOnlyList<WriteRule> writeRules,
        bool includeRead,
        int workflowStartOrdinal) =>
        new()
        {
            Id = id,
            TableId = tableId,
            RequesterSid = requesterSid,
            RequestedGranteeType = granteeType,
            RequestedGranteeRef = granteeRef,
            BusinessJustification = justification,
            Status = status,
            RequestedAt = requestedAt,
            RequestedValidTo = requestedValidTo,
            TableIdentifier = tableIdentifier,
            TenantId = tenantId,
            RequesterIdentifiers = requesterIdentifiers,
            ContextJson = contextJson,
            RequestedWriteRules = writeRules,
            IncludeReadAccess = includeRead,
            Version = ReadVersion(reader, workflowStartOrdinal),
            WorkflowMode = ReadWorkflowMode(reader, workflowStartOrdinal + 1),
            ExternalSystemId = ReadOptionalGuid(reader, workflowStartOrdinal + 2),
            ExternalReference = ReadOptionalString(reader, workflowStartOrdinal + 3),
            RequestedValidFrom = ReadOptionalTimestamp(reader, workflowStartOrdinal + 4),
            UpdatedAt = ReadOptionalTimestamp(reader, workflowStartOrdinal + 5),
            RequestKind = ReadRequestKind(reader, workflowStartOrdinal + 6),
            ProfileId = ReadOptionalGuid(reader, workflowStartOrdinal + 7)
        };

    public static ConsentRequest BuildCreatedRequest(ConsentRequest request, Guid reqId, Guid tableGuid) =>
        new()
        {
            Id = reqId,
            TableId = tableGuid,
            TableIdentifier = request.TableIdentifier,
            RequesterSid = request.RequesterSid,
            RequestedGranteeType = request.RequestedGranteeType,
            RequestedGranteeRef = request.RequestedGranteeRef,
            BusinessJustification = request.BusinessJustification,
            Status = request.Status,
            RequestedAt = request.RequestedAt,
            RequestedValidTo = request.RequestedValidTo,
            TenantId = request.TenantId,
            RequesterIdentifiers = request.RequesterIdentifiers ?? new List<string>(),
            ApprovalSteps = request.ApprovalSteps ?? new List<ApprovalStep>(),
            ContextJson = request.ContextJson,
            RequestedWriteRules = request.RequestedWriteRules ?? [],
            IncludeReadAccess = request.IncludeReadAccess,
            Version = request.Version <= 0 ? 1 : request.Version,
            WorkflowMode = request.WorkflowMode,
            ExternalSystemId = request.ExternalSystemId,
            ExternalReference = request.ExternalReference,
            RequestedValidFrom = request.RequestedValidFrom ?? request.RequestedAt,
            UpdatedAt = request.UpdatedAt ?? request.RequestedAt,
            RequestKind = request.RequestKind,
            ProfileId = request.ProfileId
        };
}
