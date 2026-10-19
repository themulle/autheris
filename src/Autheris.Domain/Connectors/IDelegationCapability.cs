namespace Autheris.Domain.Connectors;

using System.Collections.Generic;

/// <summary>
/// Issue #238 (PLAN-DATA-238, WP-SC-15): Supported mechanism for identity delegation.
/// </summary>
public enum DelegationKind
{
    /// <summary>OAuth 2.0 On-Behalf-Of token delegation.</summary>
    TokenDelegation = 0,

    /// <summary>Kerberos Constrained Delegation (S4U2Proxy).</summary>
    KerberosConstrainedDelegation = 1,

    /// <summary>Identity assertion / impersonation (e.g. EXECUTE AS / SET ROLE).</summary>
    AssertedIdentity = 2
}

/// <summary>
/// Issue #238 (PLAN-DATA-238, FR-238-1.1): Connector capability interface declaring native delegation support.
/// </summary>
public interface IDelegationCapability
{
    /// <summary>The delegation mechanisms supported by this connector.</summary>
    IReadOnlySet<DelegationKind> SupportedDelegationKinds { get; }

    /// <summary>True when the underlying data source natively evaluates row-level security.</summary>
    bool SourceEnforcesRowSecurity { get; }

    /// <summary>True when the underlying data source natively evaluates column masking.</summary>
    bool SourceEnforcesMasking { get; }

    /// <summary>True if the connector supports correlation tags for audit trail linkage.</summary>
    bool SupportsCorrelationTag { get; }
}
