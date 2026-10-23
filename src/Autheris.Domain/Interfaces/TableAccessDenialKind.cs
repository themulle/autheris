namespace Autheris.Domain.Interfaces;

/// <summary>
/// Why a <see cref="TableAccessDecision"/> was denied (MISC-06). Callers that may cure a denial with an additional consent
/// (metric consents) may do so only for <see cref="ConsentMissing"/>; every other kind, including <see cref="None"/> on a denied
/// decision, is final and never matched on reason text.
/// </summary>
public enum TableAccessDenialKind
{
    /// <summary>No kind recorded (allowed decision, or a denial from an untyped source). Treated as final.</summary>
    None = 0,

    /// <summary>The consent stage found no ALLOW consent for the subject. The only kind a metric consent may cure.</summary>
    ConsentMissing = 1,

    /// <summary>An active DENY consent applies. Final.</summary>
    ConsentDeny = 2,

    /// <summary>A policy stage denied (ReBAC, Casbin, contract, virtual filter, plugin, profile, tenant, inactive table). Final.</summary>
    Policy = 3
}
