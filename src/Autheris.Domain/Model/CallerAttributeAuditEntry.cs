namespace Autheris.Domain.Model;

/// <summary>
/// One caller attribute of a decision as the statement audit records it (REQ-GOV-129 A-11, BC-129-15): the registry id (or <c>unregistered</c>), the
/// outcome and a stable reason code. Never a name of unregistered input, a subject key or a value; never visible to the caller.
/// </summary>
public sealed record CallerAttributeAuditEntry(string Id, string Outcome, string? Reason);
