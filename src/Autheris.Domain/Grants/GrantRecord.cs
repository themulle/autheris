namespace Autheris.Domain.Grants;

using Autheris.Domain.Model;

public enum GrantRecordType
{
    Issued = 0,
    Renewed = 1,
    Revoked = 2,
    Expired = 3
}

public sealed record GrantRecord(
    string TenantId,
    long Seq,
    Guid Id,
    GrantRecordType RecordType,
    Guid? ConsentId,
    Guid? RequestId,
    ConsentGrantSource GrantSource,
    Guid? ProfileAssignmentId,
    string GranteeType,
    string GranteeRef,
    string ObjectId,
    string ScopeJson,
    DateTimeOffset? ValidFrom,
    DateTimeOffset? ValidUntil,
    string RequestedBy,
    string DecisionJson,
    DateTimeOffset DecidedAt,
    DateTimeOffset RecordedAt,
    string PrevHash,
    string RecordHash = "",
    string Signature = "",
    string KeyId = "");
