namespace Autheris.Application.Audit;

/// <summary>What happens to the guarded operation when its audit record cannot be written.</summary>
public enum AuditFailurePolicy
{
    /// <summary>No audit, no data: the failure surfaces as <see cref="AuditUnavailableException"/> and the caller aborts. A denial still denies.</summary>
    FailClosed = 0,

    /// <summary>The failure is logged and counted, never thrown.</summary>
    BestEffort = 1
}
