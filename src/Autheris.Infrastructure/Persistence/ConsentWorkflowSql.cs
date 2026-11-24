namespace Autheris.Infrastructure.Persistence;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

/// <summary>
/// Shared SQL column lists, parameter binders, and type-safe readers for consent and request workflow columns.
/// </summary>
internal static class ConsentWorkflowSql
{
    public const string DataGrantPredicate = " AND c.grant_kind = 'DATA'";

    public const string ConsentColumns = "c.grant_source, c.grant_record_seq, c.profile_assignment_id, c.expiry_notice_stage, c.grant_kind, c.approver_capacity, c.self_grant, c.target_columns";
    public const string RequestColumns = "r.version, r.workflow_mode, r.external_system_id, r.external_reference, r.requested_valid_from, r.updated_at, r.request_kind, r.profile_id, r.grant_kind, r.target_columns, r.container_kind, r.container_id, r.approver_capacity, r.self_grant, r.bundle_id, r.decided_by, r.decided_at";
    public const int RequestColumnCount = 17;

    public static void BindConsentParameters(
        DbCommand cmd,
        ConsentGrantSource grantSource,
        long grantRecordSeq,
        Guid? profileAssignmentId,
        int expiryNoticeStage,
        PermissionGrantKind grantKind = PermissionGrantKind.Data,
        ApproverCapacity? approverCapacity = null,
        bool selfGrant = false,
        IReadOnlyList<string>? targetColumns = null)
    {
        cmd.AddBound("@grantSource", grantSource.ToSqlString());
        cmd.AddBound("@grantRecordSeq", grantRecordSeq, DbType.Int64);
        cmd.AddBound("@profileAssignmentId", (object?)profileAssignmentId?.ToString() ?? DBNull.Value);
        cmd.AddBound("@expiryNoticeStage", expiryNoticeStage, DbType.Int32);
        cmd.AddBound("@grantKind", grantKind.ToSqlString());
        cmd.AddBound("@approverCapacity", approverCapacity.HasValue ? (object)approverCapacity.Value.ToSqlString() : DBNull.Value);
        cmd.AddBound("@selfGrant", selfGrant, DbType.Boolean);
        cmd.AddBound("@targetColumns", SerializeTargetColumns(targetColumns) ?? (object)DBNull.Value);
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
        cmd.AddBound("@grantKind", request.GrantKind.ToSqlString());
        cmd.AddBound("@targetColumns", SerializeTargetColumns(request.TargetColumns) ?? (object)DBNull.Value);
        cmd.AddBound("@containerKind", request.ContainerKind.HasValue ? (object)request.ContainerKind.Value.ToSqlString() : DBNull.Value);
        cmd.AddBound("@containerId", request.ContainerId.HasValue ? (object)request.ContainerId.Value.ToString("D") : DBNull.Value);
        cmd.AddBound("@approverCapacity", request.ApproverCapacity.HasValue ? (object)request.ApproverCapacity.Value.ToSqlString() : DBNull.Value);
        cmd.AddBound("@selfGrant", request.SelfGrant, DbType.Boolean);
        cmd.AddBound("@bundleId", request.BundleId.HasValue ? (object)request.BundleId.Value.ToString("D") : DBNull.Value);
        cmd.AddBound("@decidedBy", request.DecidedBy.HasValue ? (object)request.DecidedBy.Value.Value : DBNull.Value);
        cmd.AddBound("@decidedAt", request.DecidedAt.HasValue ? request.DecidedAt.Value.ToUniversalTime() : DBNull.Value, DbType.DateTimeOffset);
    }

    public static string? SerializeTargetColumns(IReadOnlyList<string>? targetColumns) =>
        targetColumns is { Count: > 0 } ? JsonSerializer.Serialize(targetColumns) : null;

    public static PermissionGrantKind ReadGrantKind(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal)
        {
            return PermissionGrantKind.Data;
        }

        if (reader.IsDBNull(ordinal))
        {
            return PermissionGrantKind.Unknown;
        }

        return ConsentWorkflowSqlMapping.ParseGrantKindOrUnknown(Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture));
    }

    public static ApproverCapacity? ReadApproverCapacity(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        return ConsentWorkflowSqlMapping.TryParseApproverCapacity(raw, out var parsed) ? parsed : null;
    }

    public static bool ReadBooleanOrFalse(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return false;
        }

        var raw = reader.GetValue(ordinal);
        return raw switch
        {
            bool b => b,
            _ => Convert.ToInt32(raw, CultureInfo.InvariantCulture) != 0
        };
    }

    public static IReadOnlyList<string>? ReadTargetColumns(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = reader.GetString(ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var parsed = JsonSerializer.Deserialize<List<string>>(raw);
        return parsed is { Count: > 0 } ? parsed : null;
    }

    public static ContainerKind? ReadContainerKind(DbDataReader reader, int ordinal)
    {
        if (reader.FieldCount <= ordinal || reader.IsDBNull(ordinal))
        {
            return null;
        }

        var raw = Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        return ConsentWorkflowSqlMapping.TryParseContainerKind(raw, out var parsed) ? parsed : null;
    }

    public static Sid? ReadOptionalSid(DbDataReader reader, int ordinal)
    {
        var raw = ReadOptionalString(reader, ordinal);
        return !string.IsNullOrWhiteSpace(raw) ? new Sid(raw) : (Sid?)null;
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
            : ConsentRequestKind.Data;

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
            ExpiryNoticeStage = (int)ConsentWorkflowSql.ReadInt64OrZero(reader, workflowStartOrdinal + 3),
            GrantKind = ReadGrantKind(reader, workflowStartOrdinal + 4),
            ApproverCapacity = ReadApproverCapacity(reader, workflowStartOrdinal + 5),
            SelfGrant = ReadBooleanOrFalse(reader, workflowStartOrdinal + 6),
            TargetColumns = ReadTargetColumns(reader, workflowStartOrdinal + 7)
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
            ProfileId = ReadOptionalGuid(reader, workflowStartOrdinal + 7),
            GrantKind = ReadGrantKind(reader, workflowStartOrdinal + 8),
            TargetColumns = ReadTargetColumns(reader, workflowStartOrdinal + 9),
            ContainerKind = ReadContainerKind(reader, workflowStartOrdinal + 10),
            ContainerId = ReadOptionalGuid(reader, workflowStartOrdinal + 11),
            ApproverCapacity = ReadApproverCapacity(reader, workflowStartOrdinal + 12),
            SelfGrant = ReadBooleanOrFalse(reader, workflowStartOrdinal + 13),
            BundleId = ReadOptionalGuid(reader, workflowStartOrdinal + 14),
            DecidedBy = ReadOptionalSid(reader, workflowStartOrdinal + 15),
            DecidedAt = ReadOptionalTimestamp(reader, workflowStartOrdinal + 16)
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
            GrantKind = request.GrantKind,
            ProfileId = request.ProfileId,
            TargetColumns = request.TargetColumns?.ToArray(),
            ContainerKind = request.ContainerKind,
            ContainerId = request.ContainerId,
            ApproverCapacity = request.ApproverCapacity,
            SelfGrant = request.SelfGrant,
            BundleId = request.BundleId,
            DecidedBy = request.DecidedBy,
            DecidedAt = request.DecidedAt
        };
}
