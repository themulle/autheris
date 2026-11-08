using System;
using System.Collections.Generic;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// Validates storage and cluster state options (GovernanceDb provider, Redis in multi-node).
/// </summary>
public sealed class GatewayStorageOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IHostEnvironment _environment;

    public GatewayStorageOptionsValidator(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        var failures = new List<string>();
        var isMultiNode = opts.HighAvailability.MultiNodeClusterMode || opts.HighAvailability.Replicas > 1;

        if (!GatewayStartupValidator.IsSupportedGovernanceDbProvider(opts.GovernanceDb.Provider))
        {
            failures.Add("The GovernanceDb provider supports only 'PostgreSql' or 'SqlServer'.");
        }

        if (!_environment.IsDevelopment() && isMultiNode && !opts.Caching.Redis.Enabled)
        {
            failures.Add("NF-HA-02 / SEC-SIMPL-34 violation: In MultiNodeClusterMode or with more than one replica (PG-6), cluster-wide shared state requires Caching.Redis.Enabled = true. Per-process fallbacks weaken security in multi-node deployments. Affected consumers: token revocation, replay cache, rate limit, subject lockout cache, and identity/policy invalidation event bus (OD-27 deprovisioning).");
        }

        // Nested data annotations are not evaluated; the read concurrency limit (stored procedure and SQL leases) must stay in range.
        if (opts.DataSources.MaxConcurrentReadsPerUserAndTable is < 0 or > 1000)
        {
            failures.Add("DataSources.MaxConcurrentReadsPerUserAndTable must be between 0 (disabled) and 1000.");
        }

        // Nested data annotations are not evaluated: the scan cap must cover the row limit and stay bounded.
        var procedures = opts.SqlEndpoints.Procedures;
        int effectiveScanRows = procedures.EffectiveMaxScanRows;
        if (effectiveScanRows < procedures.MaxRows || effectiveScanRows is < 1 or > 100000)
        {
            failures.Add("SqlEndpoints.Procedures.MaxScanRows must be between 1 and 100000 and not below SqlEndpoints.Procedures.MaxRows.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
