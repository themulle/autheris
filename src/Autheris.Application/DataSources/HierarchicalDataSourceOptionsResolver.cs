namespace Autheris.Application.DataSources;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using Autheris.Application.Sql.Services;
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
    private readonly ILogger<HierarchicalDataSourceOptionsResolver>? _logger;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly object _syncRoot = new();

    // profiles and the default profile are swapped together as one immutable snapshot.
    private sealed record Snapshot(FrozenDictionary<string, EffectiveDataSourceProfile> Profiles, EffectiveDataSourceProfile DefaultProfile);

    private volatile Snapshot _snapshot = null!;
    private volatile Exception? _lastRebuildError;
    private long _generation;

    /// <inheritdoc />
    public long Generation => Interlocked.Read(ref _generation);

    /// <inheritdoc />
    public bool IsConfigurationStale => _lastRebuildError is not null;

    /// <inheritdoc />
    public Exception? LastRebuildError => _lastRebuildError;

    public HierarchicalDataSourceOptionsResolver(
        IOptionsMonitor<DataSourceConfigurationOptions> dataSourceOptions,
        IOptionsMonitor<GatewayOptions>? gatewayOptions = null,
        IOptionsMonitor<TelemetryOptions>? telemetryOptions = null,
        ILogger<HierarchicalDataSourceOptionsResolver>? logger = null)
    {
        _dataSourceOptions = dataSourceOptions ?? throw new ArgumentNullException(nameof(dataSourceOptions));
        _gatewayOptions = gatewayOptions;
        _telemetryOptions = telemetryOptions;
        _logger = logger;

        Rebuild();

        var subConfig = _dataSourceOptions.OnChange(_ => RebuildKeepingLastGood());
        if (subConfig != null) _subscriptions.Add(subConfig);

        if (_gatewayOptions != null)
        {
            var subGateway = _gatewayOptions.OnChange(_ => RebuildKeepingLastGood());
            if (subGateway != null) _subscriptions.Add(subGateway);
        }
        if (_telemetryOptions != null)
        {
            var subTelemetry = _telemetryOptions.OnChange(_ => RebuildKeepingLastGood());
            if (subTelemetry != null) _subscriptions.Add(subTelemetry);
        }
    }

    /// <inheritdoc />
    public EffectiveDataSourceProfile Resolve(string? dataSourceName)
    {
        var snapshot = _snapshot;
        if (string.IsNullOrWhiteSpace(dataSourceName))
        {
            return snapshot.DefaultProfile;
        }

        if (snapshot.Profiles.TryGetValue(dataSourceName, out var profile))
        {
            return profile;
        }

        return snapshot.DefaultProfile;
    }

    /// <inheritdoc />
    public bool TryResolve(string? dataSourceName, out EffectiveDataSourceProfile profile)
    {
        var snapshot = _snapshot;
        if (string.IsNullOrWhiteSpace(dataSourceName))
        {
            profile = snapshot.DefaultProfile;
            return false;
        }

        if (snapshot.Profiles.TryGetValue(dataSourceName, out var resolved))
        {
            profile = resolved;
            return true;
        }

        profile = snapshot.DefaultProfile;
        return false;
    }

    /// <summary>
    /// a failed rebuild (for example an options source that throws) keeps the last good snapshot instead of failing open or
    /// leaving a half-built map.
    /// </summary>
    private void RebuildKeepingLastGood()
    {
        try
        {
            Rebuild();
        }
        catch (Exception ex)
        {
            _lastRebuildError = ex;
            _logger?.LogError(ex, "Failed to rebuild hierarchical data source options. Keeping last known good snapshot.");
        }
    }

    /// <inheritdoc />
    public string CanonicalName(string? dataSourceName)
    {
        var webSql = _gatewayOptions?.CurrentValue?.WebSql;
        return webSql == null
            ? string.IsNullOrWhiteSpace(dataSourceName) ? string.Empty : dataSourceName
            : DataSourceAllowlistGuard.MapDataSource(webSql, dataSourceName);
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

            // 10 / decision 5: every configured WebSQL source name gets a profile built from Defaults, so a legitimately
            // configured source never reads as unknown. Explicit profiles are never overwritten (the map is keyed by name).
            AddWebSqlSourceNames(gateway?.WebSql, names);

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

            _snapshot = new Snapshot(dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), defaultProfile);
            _lastRebuildError = null;

            // after the swap: a cache that sees the new generation also sees the new snapshot
            Interlocked.Increment(ref _generation);
        }
    }

    private static void AddWebSqlSourceNames(WebSqlOptions? webSql, HashSet<string> names)
    {
        if (webSql == null)
        {
            return;
        }

        AddName(webSql.DefaultDataSourceName, names);

        if (webSql.AllowedDataSources != null)
        {
            foreach (var name in webSql.AllowedDataSources)
            {
                AddName(name, names);
            }
        }

        if (webSql.DataSourceMappings != null)
        {
            foreach (var mapped in webSql.DataSourceMappings.Values)
            {
                AddName(mapped, names);
            }
        }

        if (webSql.TenantDataSourceAllowlist != null)
        {
            foreach (var list in webSql.TenantDataSourceAllowlist.Values)
            {
                if (list == null) continue;
                foreach (var name in list)
                {
                    AddName(name, names);
                }
            }
        }
    }

    private static void AddName(string? name, HashSet<string> names)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            names.Add(name);
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
        // Dual administrative opt-in required for ceiling relaxation
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
        int complexityCeiling = Math.Max(1, ceilings.MaxAllowedComplexityCeiling);
        int complexity = relaxed ? Math.Max(1, rawComplexity) : Math.Clamp(rawComplexity, 1, complexityCeiling);

        long rawBudget = profileOptions?.MaxAggregateRowBudget
            ?? fallbackDefaults.MaxAggregateRowBudget
            ?? 100_000L;
        long budgetCeiling = Math.Max(1L, ceilings.MaxAggregateRowBudgetCeiling);
        long budget = relaxed ? Math.Max(1L, rawBudget) : Math.Clamp(rawBudget, 1L, budgetCeiling);

        int rawQueryTimeout = profileOptions?.QueryTimeoutSeconds
            ?? fallbackDefaults.QueryTimeoutSeconds
            ?? connectionOptions?.CommandTimeoutSeconds
            ?? 30;
        int queryTimeoutCeiling = Math.Max(1, ceilings.QueryTimeoutCeilingSeconds);
        int queryTimeoutSec = relaxed ? Math.Max(1, rawQueryTimeout) : Math.Clamp(rawQueryTimeout, 1, queryTimeoutCeiling);
        TimeSpan queryTimeout = TimeSpan.FromSeconds(queryTimeoutSec);

        // 2. Logging & Telemetry
        string rawLogLevel = profileOptions?.MinimumLogLevel
            ?? fallbackDefaults.MinimumLogLevel
            ?? telemetry?.DataSourceDefaults?.MinimumLogLevel
            ?? "Information";

        if (!Enum.TryParse<LogLevel>(rawLogLevel, ignoreCase: true, out var parsedLogLevel))
        {
            parsedLogLevel = LogLevel.Information;
        }

        // Telemetry redaction ceiling precedence
        bool allowStatementText = (telemetry?.Redaction?.AllowStatementText ?? true) && (gateway?.Logging?.AllowStatementText ?? true);
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
        long maxRowsCeiling = Math.Max(1L, ceilings.MaxResponseRowsCeiling);
        long maxRows = relaxed ? Math.Max(1L, rawMaxRows) : Math.Clamp(rawMaxRows, 1L, maxRowsCeiling);

        long rawMaxBytes = profileOptions?.MaxResponseBytes
            ?? fallbackDefaults.MaxResponseBytes
            ?? 10_485_760L; // 10 MB default
        long maxBytesCeiling = Math.Max(1L, ceilings.MaxResponseBytesCeiling);
        long maxBytes = relaxed ? Math.Max(1L, rawMaxBytes) : Math.Clamp(rawMaxBytes, 1L, maxBytesCeiling);

        int rawCmdTimeout = profileOptions?.CommandTimeoutSeconds
            ?? fallbackDefaults.CommandTimeoutSeconds
            ?? connectionOptions?.CommandTimeoutSeconds
            ?? 30;
        int cmdTimeoutCeiling = Math.Max(1, ceilings.CommandTimeoutCeilingSeconds);
        int cmdTimeoutSec = relaxed ? Math.Max(1, rawCmdTimeout) : Math.Clamp(rawCmdTimeout, 1, cmdTimeoutCeiling);
        TimeSpan commandTimeout = TimeSpan.FromSeconds(cmdTimeoutSec);

        int? minPool = profileOptions?.MinPoolSize ?? fallbackDefaults.MinPoolSize;
        int? maxPool = profileOptions?.MaxPoolSize ?? fallbackDefaults.MaxPoolSize;

        IsolationLevel defaultIso = profileOptions?.DefaultIsolationLevel
            ?? fallbackDefaults.DefaultIsolationLevel
            ?? (connectionOptions?.ReadUncommitted == true ? IsolationLevel.ReadUncommitted : IsolationLevel.ReadCommitted);

        // 01 / decision 13: a profile without the key inherits Defaults, then All; an explicit None stays None.
        DataSourceTransportFlags transports = profileOptions?.AllowedTransports
            ?? fallbackDefaults.AllowedTransports
            ?? DataSourceTransportFlags.All;

        // 4. Caching & Staging Guardrails
        bool cachingEnabled = profileOptions?.CachingEnabled
            ?? fallbackDefaults.CachingEnabled
            ?? true;

        int rawStagingTtl = profileOptions?.StagingTtlSeconds
            ?? fallbackDefaults.StagingTtlSeconds
            ?? 3600;
        int stagingTtlCeiling = Math.Max(1, ceilings.StagingTtlCeilingSeconds);
        int stagingTtlSec = relaxed ? Math.Max(1, rawStagingTtl) : Math.Clamp(rawStagingTtl, 1, stagingTtlCeiling);
        TimeSpan stagingTtl = TimeSpan.FromSeconds(stagingTtlSec);

        int rawKeyLimit = profileOptions?.MaxKeySetImportLimit
            ?? fallbackDefaults.MaxKeySetImportLimit
            ?? 1000;
        int keyLimitCeiling = Math.Max(1, ceilings.MaxKeySetImportLimitCeiling);
        int keyLimit = relaxed ? Math.Max(1, rawKeyLimit) : Math.Clamp(rawKeyLimit, 1, keyLimitCeiling);

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
            CeilingRelaxed: relaxed,
            IsUnresolved: isGlobalDefault);
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
