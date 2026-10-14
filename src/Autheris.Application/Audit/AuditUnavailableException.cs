namespace Autheris.Application.Audit;

/// <summary>
/// Thrown when a required audit record cannot be written (store down, pipeline faulted or saturated, or the audit hash chain is
/// latched as violated). Callers abort the guarded operation: "no audit, no data". Derives from
/// <see cref="InvalidOperationException"/> so existing fail-closed handlers keep working.
/// </summary>
public sealed class AuditUnavailableException : InvalidOperationException
{
    public AuditUnavailableException()
        : base("The audit store is unavailable (fail-closed).")
    {
    }

    public AuditUnavailableException(string message)
        : base(message)
    {
    }

    public AuditUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
