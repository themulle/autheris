using System;
using System.Collections.Generic;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// WP-ARC-C-02: Validates storage and cluster state options (GovernanceDb provider, SQLite in multi-node, Garnet embedded vs Redis).
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
            failures.Add("The GovernanceDb provider currently supports only 'Sqlite', 'PostgreSql' or 'SqlServer'.");
        }

        if (isMultiNode && DataSourceProvider.Is(opts.GovernanceDb.Provider, DatabaseDialect.Sqlite))
        {
            failures.Add("Security violation (E-2): Multi-node cluster mode and more than 1 replica are not allowed with SQLite, because SQLite uses local database files per instance. Configure GovernanceDb.Provider = 'PostgreSql' or 'SqlServer' for cluster operation.");
        }

        if (isMultiNode && opts.Caching.Garnet.EnableEmbeddedServer)
        {
            failures.Add("NF-HA-02 violation: Multi-node cluster mode or more than one replica requires an external Redis/Garnet; the embedded Garnet server is local to one process and cannot provide cluster-wide state.");
        }

        if (!_environment.IsDevelopment() && isMultiNode && !opts.Caching.Redis.Enabled)
        {
            failures.Add("NF-HA-02 violation: In MultiNodeClusterMode or with more than one replica (PG-6), cluster-wide cache and epoch invalidation requires Caching.Redis.Enabled = true.");
        }

        // Nested data annotations are not evaluated; the read concurrency limit (stored procedure and SQL leases) must stay in range.
        if (opts.DataSources.MaxConcurrentReadsPerUserAndTable is < 0 or > 1000)
        {
            failures.Add("DataSources.MaxConcurrentReadsPerUserAndTable must be between 0 (disabled) and 1000.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
