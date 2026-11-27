namespace Autheris.Domain.Options;

using System.Collections.Generic;

/// <summary>What the Unity Catalog reconciler does for a provisioning-managed Databricks data source.</summary>
public enum DatabricksProvisioningMode
{
    /// <summary>
    /// No new privileges are applied. Privileges that Autheris applied earlier are still revoked until none is left, so turning
    /// provisioning off never leaves them behind.
    /// </summary>
    Off = 0,

    /// <summary>The reconciler computes, records and audits its plan but never changes Unity Catalog.</summary>
    DryRun = 1,

    /// <summary>The reconciler applies and revokes privileges in Unity Catalog.</summary>
    Enforce = 2
}

/// <summary>How the reconciler treats privileges on managed securables that Autheris did not apply.</summary>
public enum DatabricksDriftMode
{
    /// <summary>Foreign privileges are reported and never removed.</summary>
    Report = 0,

    /// <summary>Foreign privileges on managed securables are removed (opt-in per data source). Reserved: rejected at startup until available.</summary>
    Revert = 1
}

/// <summary>
/// Provisioning of approved grants as Unity Catalog privileges for one Databricks data source. Only consents of
/// <see cref="Tenant"/> on tables inside <see cref="AllowedSchemas"/> are provisioned; the gateway enforcement is unchanged and
/// never depends on the provisioning state.
/// </summary>
public sealed class DatabricksProvisioningOptions
{
    /// <summary>Off (revoke only), DryRun (plan and audit, no changes) or Enforce (apply and revoke).</summary>
    public DatabricksProvisioningMode Mode { get; init; } = DatabricksProvisioningMode.Off;

    /// <summary>The single tenant whose consents are provisioned on this data source; consents of other tenants are not.</summary>
    public string? Tenant { get; init; }

    /// <summary>
    /// The schemas (<c>catalog.schema</c>) whose tables may be provisioned. The catalog must be the data source catalog. The
    /// provisioning principal needs <c>MANAGE</c> on exactly these schemas; securables outside them are never changed.
    /// </summary>
    public IReadOnlyList<string> AllowedSchemas { get; init; } = [];

    /// <summary>How foreign privileges on managed securables are handled; only Report (the default) is accepted today.</summary>
    public DatabricksDriftMode DriftMode { get; init; } = DatabricksDriftMode.Report;

    /// <summary>Seconds between two reconcile cycles (10 to 300).</summary>
    public int ReconcileIntervalSeconds { get; init; } = 60;

    /// <summary>
    /// The dedicated provisioning principal. Only OAuth M2M with a client secret reference is accepted; it must differ from the
    /// principal that runs queries.
    /// </summary>
    public DatabricksAuthOptions Auth { get; init; } = new();
}
