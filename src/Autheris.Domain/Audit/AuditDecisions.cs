namespace Autheris.Domain.Audit;

/// <summary>Values of <c>AuditLogEntry.Decision</c>.</summary>
public static class AuditDecisions
{
    public const string Allow = "ALLOW";
    public const string Deny = "DENY";
    public const string Error = "ERROR";
}
