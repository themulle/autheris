namespace Autheris.Application.DataSources;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Data;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Thread-safe, zero-allocation hierarchical options resolver (BL-05).
/// Combines global defaults, safety ceilings, and per-source overrides into precomputed, immutable profiles.
/// Hot path lookups are lock-free O(1) against a <see cref="FrozenDictionary{TKey,TValue}"/>.
/// </summary>
public sealed class HierarchicalDataSourceOptionsResolver : IDataSourceOptionsResolver, IDisposable
{
    private readonly IOptionsMonitor<DataSourceConfigurationOptions> _dataSourceOptions;
    private readonly IOptionsMonitor<GatewayOptions>? _gatewayOptions;
    private readonly IOptionsMonitor<TelemetryOptions>? _telemetryOptions;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _syncRoot = new();

    private volatile FrozenDictionary<string, EffectiveDataSourceProfile> _profiles = FrozenDictionary<string, EffectiveDataSourceProfile>.Empty;
    private volatile EffectiveDataSourceProfile _defaultProfile = default!;

    public HierarchicalDataSourceOptionsResolver(
        IOptionsMonitor<DataSourceConfigurationOptions> dataSourceOptions,
        IOptionsMonitor<GatewayOptions>? gatewayOptions = null,
        IOptionsMonitor<TelemetryOptions>? telemetryOptions = null)
    {
        _dataSourceOptions = dataSourceOptions ?? throw new ArgumentNullException(nameof(dataSourceOptions));
        _gatewayOptions = gatewayOptions;
        _telemetryOptions = telemetryOptions;

        Rebuild();

        var subConfig = _dataSourceOptions.OnChange(_ => Rebuild());
        if (subConfig != null) _subscriptions.Add(subConfig);

        if (_gatewayOptions != null)
        {
            var subGateway = _gatewayOptions.OnChange(_ => Rebuild());
            if (subGateway != null) _subscriptions.Add(subGateway);
        }
        if (_telemetryOptions != null)
        {
            var subTelemetry = _telemetryOptions.OnChange(_ => Rebuild());
            if (subTelemetry != null) _subscriptions.Add(subTelemetry);
        }
    }

    /// <inheritdoc />
    public EffectiveDataSourceProfile Resolve(string? dataSourceName)
    {
        if (string.IsNullOrWhiteSpace(dataSourceName))
        {
            return _defaultProfile;
        }

        if (_profiles.TryGetValue(dataSourceName, out var profile))
        {
            return profile;
        }

        return _defaultProfile;
    }

    /// <inheritdoc />
    public bool TryResolve(string? dataSourceName, out EffectiveDataSourceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(dataSourceName))
        {
            profile = _defaultProfile;
            return false;
        }

        if (_profiles.TryGetValue(dataSourceName, out var resolved))
        {
            profile = resolved;
            return true;
        }

        profile = _defaultProfile;
        return false;
    }

    /// <inheritdoc />
    public bool IsTransportAllowed(string? dataSourceName, DataSourceTransportFlags transport)
    {
        var profile = Resolve(dataSourceName);
        return (profile.AllowedTransports & transport) == transport;
    }

    private void Rebuild()
    {
        lock (_syncRoot)
        {
            var config = _dataSourceOptions.CurrentValue ?? new DataSourceConfigurationOptions();
            var gateway = _gatewayOptions?.CurrentValue;
            var telemetry = _telemetryOptions?.CurrentValue;
            var ceilings = config.Ceilings ?? new DataSourceSafetyCeilingsOptions();

            var defaultProfile = BuildProfile(
                dataSourceName: "default",
                profileOptions: config.Defaults,
                fallbackDefaults: config.Defaults,
                connectionOptions: null,
                gateway: gateway,
                telemetry: telemetry,
                ceilings: ceilings,
                isGlobalDefault: true);

            var dict = new Dictionary<string, EffectiveDataSourceProfile>(StringComparer.OrdinalIgnoreCase);

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (config.Profiles != null)
            {
                foreach (var name in config.Profiles.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }

            if (gateway?.DataSources?.Connections != null)
            {
                foreach (var name in gateway.DataSources.Connections.Keys)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }

            foreach (var name in names)
            {
                DataSourceProfileOptions? profileOptions = null;
                config.Profiles?.TryGetValue(name, out profileOptions);

                DataSourceConnectionOptions? connOptions = null;
                gateway?.DataSources?.Connections?.TryGetValue(name, out connOptions);

                var profile = BuildProfile(
                    dataSourceName: name,
                    profileOptions: profileOptions,
                    fallbackDefaults: config.Defaults,
                    connectionOptions: connOptions,
                    gateway: gateway,
                    telemetry: telemetry,
                    ceilings: ceilings,
                    isGlobalDefault: false);

                dict[name] = profile;
            }

            _defaultProfile = defaultProfile;
            _profiles = dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static EffectiveDataSourceProfile BuildProfile(
        string dataSourceName,
        DataSourceProfileOptions? profileOptions,
        DataSourceProfileOptions fallbackDefaults,
        DataSourceConnectionOptions? connectionOptions,
        GatewayOptions? gateway,
        TelemetryOptions? telemetry,
        DataSourceSafetyCeilingsOptions ceilings,
        bool isGlobalDefault)
    {
        // -------------------------------------------------------------------------
        // SEC-DS05-02: Dual administrative opt-in required for ceiling relaxation
        // -------------------------------------------------------------------------
        bool relaxed = (profileOptions?.AllowCeilingRelaxation == true) && ceilings.AllowGlobalCeilingRelaxation;

        // 1. Cost & Quota
        double rawCostMultiplier = profileOptions?.QueryCostMultiplier
            ?? fallbackDefaults.QueryCostMultiplier
            ?? 1.0;
        double costMultiplier = Math.Clamp(rawCostMultiplier, 0.01, 1000.0);

        int rawComplexity = profileOptions?.MaxAllowedComplexity
            ?? fallbackDefaults.MaxAllowedComplexity
            ?? 1000;
        int complexity = relaxed ? rawComplexity : Math.Min(rawComplexity, ceilings.MaxAllowedComplexityCeiling);

        long rawBudget = profileOptions?.MaxAggregateRowBudget
            ?? fallbackDefaults.MaxAggregateRowBudget
            ?? 100_000L;
        long budget = relaxed ? rawBudget : Math.Min(rawBudget, ceilings.MaxAggregateRowBudgetCeiling);

        int rawQueryTimeout = profileOptions?.QueryTimeoutSeconds
            ?? fallbackDefaults.QueryTimeoutSeconds
            ?? connectionOptions?.CommandTimeoutSeconds
            ?? 30;
        int queryTimeoutSec = relaxed ? rawQueryTimeout : Math.Min(rawQueryTimeout, ceilings.QueryTimeoutCeilingSeconds);
        TimeSpan queryTimeout = TimeSpan.FromSeconds(Math.Max(1, queryTimeoutSec));

        // 2. Logging & Telemetry
        string rawLogLevel = profileOptions?.MinimumLogLevel
            ?? fallbackDefaults.MinimumLogLevel
            ?? telemetry?.DataSourceDefaults?.MinimumLogLevel
            ?? "Information";

        if (!Enum.TryParse<LogLevel>(rawLogLevel, ignoreCase: true, out var parsedLogLevel))
        {
            parsedLogLevel = LogLevel.Information;
        }

        // SEC-DS05-06: Telemetry redaction ceiling precedence
        bool allowStatementText = telemetry?.Redaction?.AllowStatementText ?? true;
        bool rawLogSql = profileOptions?.LogGeneratedSql
            ?? fallbackDefaults.LogGeneratedSql
            ?? gateway?.Logging?.LogGeneratedSql
            ?? false;
        bool logSql = allowStatementText && rawLogSql;

        double rawRatio = profileOptions?.SamplingRatio
            ?? fallbackDefaults.SamplingRatio
            ?? telemetry?.Sampling?.Ratio
            ?? 1.0;
        double samplingRatio = Math.Clamp(rawRatio, 0.0, 1.0);

        OutboundPropagationMode propagation = profileOptions?.OutboundPropagation ?? fallbackDefaults.OutboundPropagation;
        if (propagation == OutboundPropagationMode.Inherit)
        {
            propagation = (telemetry?.Propagation?.Enabled ?? true)
                ? OutboundPropagationMode.Always
                : OutboundPropagationMode.Never;
        }

        // 3. Operational Guardrails
        long rawMaxRows = profileOptions?.MaxResponseRows
            ?? fallbackDefaults.MaxResponseRows
            ?? gateway?.RowLimits?.Trino?.DefaultMaxRows
            ?? 10_000L;
        long maxRows = relaxed ? rawMaxRows : Math.Min(rawMaxRows, ceilings.MaxResponseRowsCeiling);

        long rawMaxBytes = profileOptions?.MaxResponseBytes
            ?? fallbackDefaults.MaxResponseBytes
            ?? 10_485_760L; // 10 MB default
        long maxBytes = relaxed ? rawMaxBytes : Math.Min(rawMaxBytes, ceilings.MaxResponseBytesCeiling);

        int rawCmdTimeout = profileOptions?.CommandTimeoutSeconds
            ?? fallbackDefaults.CommandTimeoutSeconds
            ?? connectionOptions?.CommandTimeoutSeconds
            ?? 30;
        int cmdTimeoutSec = relaxed ? rawCmdTimeout : Math.Min(rawCmdTimeout, ceilings.CommandTimeoutCeilingSeconds);
        TimeSpan commandTimeout = TimeSpan.FromSeconds(Math.Max(1, cmdTimeoutSec));

        int? minPool = profileOptions?.MinPoolSize ?? fallbackDefaults.MinPoolSize;
        int? maxPool = profileOptions?.MaxPoolSize ?? fallbackDefaults.MaxPoolSize;

        IsolationLevel defaultIso = profileOptions?.DefaultIsolationLevel
            ?? fallbackDefaults.DefaultIsolationLevel
            ?? (connectionOptions?.ReadUncommitted == true ? IsolationLevel.ReadUncommitted : IsolationLevel.ReadCommitted);

        DataSourceTransportFlags transports = profileOptions?.AllowedTransports
            ?? fallbackDefaults.AllowedTransports;

        // 4. Caching & Staging Guardrails
        bool cachingEnabled = profileOptions?.CachingEnabled
            ?? fallbackDefaults.CachingEnabled
            ?? true;

        int rawStagingTtl = profileOptions?.StagingTtlSeconds
            ?? fallbackDefaults.StagingTtlSeconds
            ?? 3600;
        int stagingTtlSec = relaxed ? rawStagingTtl : Math.Min(rawStagingTtl, ceilings.StagingTtlCeilingSeconds);
        TimeSpan stagingTtl = TimeSpan.FromSeconds(Math.Max(1, stagingTtlSec));

        int rawKeyLimit = profileOptions?.MaxKeySetImportLimit
            ?? fallbackDefaults.MaxKeySetImportLimit
            ?? 1000;
        int keyLimit = relaxed ? rawKeyLimit : Math.Min(rawKeyLimit, ceilings.MaxKeySetImportLimitCeiling);

        return new EffectiveDataSourceProfile(
            DataSourceName: dataSourceName,
            QueryCostMultiplier: costMultiplier,
            MaxAllowedComplexity: complexity,
            MaxAggregateRowBudget: budget,
            QueryTimeout: queryTimeout,
            MinimumLogLevel: parsedLogLevel.ToString(),
            LogGeneratedSql: logSql,
            SamplingRatio: samplingRatio,
            OutboundPropagation: propagation,
            MaxResponseRows: maxRows,
            MaxResponseBytes: maxBytes,
            CommandTimeout: commandTimeout,
            MinPoolSize: minPool,
            MaxPoolSize: maxPool,
            DefaultIsolationLevel: defaultIso,
            AllowedTransports: transports,
            CachingEnabled: cachingEnabled,
            StagingTtl: stagingTtl,
            MaxKeySetImportLimit: keyLimit,
            CeilingRelaxed: relaxed);
    }

    public void Dispose()
    {
        foreach (var sub in _subscriptions)
        {
            sub.Dispose();
        }
        _subscriptions.Clear();
    }
}
