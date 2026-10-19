namespace Autheris.Application.Interfaces;

using Autheris.Domain.Connectors;

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Contract for resolving ambient delegation context (caller JWT assertion, principal, correlation).
/// </summary>
public interface IDelegationContextProvider
{
    DelegationContext? ResolveDelegationContext();
}
