namespace Autheris.Domain.Audit;

public enum AuditLevel
{
    Full,
    Summarized,
    Delegated
}

public sealed record AuditPolicy(AuditLevel Level, string EventType);

public sealed record AuditExemption(string Justification);
