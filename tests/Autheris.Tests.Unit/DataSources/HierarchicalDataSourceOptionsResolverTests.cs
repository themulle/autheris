namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Data;
using Autheris.Application.DataSources;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class HierarchicalDataSourceOptionsResolverTests
{
    [Fact]
    public void Resolve_WhenDataSourceIsUnspecifiedOrUnknown_ReturnsDefaultProfile()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Defaults = new DataSourceProfileOptions
            {
                QueryCostMultiplier = 1.5,
                MaxAllowedComplexity = 1500,
                CommandTimeoutSeconds = 45,
                AllowedTransports = DataSourceTransportFlags.WebSql | DataSourceTransportFlags.GraphQL
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var nullResult = resolver.Resolve(null);
        var emptyResult = resolver.Resolve("  ");
        var unknownResult = resolver.Resolve("non_existent_source");

        nullResult.ShouldNotBeNull();
        nullResult.QueryCostMultiplier.ShouldBe(1.5);
        nullResult.MaxAllowedComplexity.ShouldBe(1500);
        nullResult.CommandTimeout.ShouldBe(TimeSpan.FromSeconds(45));

        emptyResult.ShouldBeSameAs(nullResult);
        unknownResult.ShouldBeSameAs(nullResult);
    }

    [Fact]
    public void Resolve_WhenProfileConfigured_OverridesGlobalDefaults()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Defaults = new DataSourceProfileOptions
            {
                QueryCostMultiplier = 1.0,
                MaxResponseRows = 5000,
                CachingEnabled = true,
                SamplingRatio = 0.5,
                StagingTtlSeconds = 1800
            },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["analytics_warehouse"] = new DataSourceProfileOptions
                {
                    QueryCostMultiplier = 3.5,
                    MaxResponseRows = 50_000,
                    CachingEnabled = false,
                    SamplingRatio = 1.0,
                    StagingTtlSeconds = 7200,
                    MinimumLogLevel = "Debug",
                    LogGeneratedSql = true,
                    AllowedTransports = DataSourceTransportFlags.Trino | DataSourceTransportFlags.OData
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("analytics_warehouse");

        profile.DataSourceName.ShouldBe("analytics_warehouse");
        profile.QueryCostMultiplier.ShouldBe(3.5);
        profile.MaxResponseRows.ShouldBe(50_000);
        profile.CachingEnabled.ShouldBeFalse();
        profile.SamplingRatio.ShouldBe(1.0);
        profile.StagingTtl.ShouldBe(TimeSpan.FromSeconds(7200));
        profile.MinimumLogLevel.ShouldBe("Debug");
        profile.LogGeneratedSql.ShouldBeTrue();
        profile.AllowedTransports.ShouldBe(DataSourceTransportFlags.Trino | DataSourceTransportFlags.OData);
    }

    [Fact]
    public void Resolve_WhenProfileOmitsProperties_InheritsGlobalDefaults()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Defaults = new DataSourceProfileOptions
            {
                QueryCostMultiplier = 2.0,
                MaxResponseRows = 8000,
                CachingEnabled = true
            },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["partial_source"] = new DataSourceProfileOptions
                {
                    QueryCostMultiplier = 4.0 // Only multiplier is overridden
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("partial_source");

        profile.QueryCostMultiplier.ShouldBe(4.0);
        profile.MaxResponseRows.ShouldBe(8000); // Inherited
        profile.CachingEnabled.ShouldBeTrue(); // Inherited
    }

    [Fact]
    public void Resolve_InheritsFromDataSourceConnectionOptions_WhenNotOverriddenInProfile()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions());
        var gatewayMonitor = new TestMonitor<GatewayOptions>(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["legacy_sqlserver"] = new DataSourceConnectionOptions
                    {
                        CommandTimeoutSeconds = 75,
                        ReadUncommitted = true
                    }
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor, gatewayMonitor);

        var profile = resolver.Resolve("legacy_sqlserver");

        profile.CommandTimeout.ShouldBe(TimeSpan.FromSeconds(75));
        profile.DefaultIsolationLevel.ShouldBe(IsolationLevel.ReadUncommitted);
    }

    [Fact]
    public void Resolve_WhenOptionsReloaded_ReflectsNewConfigurationImmediately()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["live_source"] = new DataSourceProfileOptions
                {
                    QueryCostMultiplier = 1.0
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        resolver.Resolve("live_source").QueryCostMultiplier.ShouldBe(1.0);

        // Update configuration dynamically
        configMonitor.Update(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["live_source"] = new DataSourceProfileOptions
                {
                    QueryCostMultiplier = 5.25
                }
            }
        });

        resolver.Resolve("live_source").QueryCostMultiplier.ShouldBe(5.25);
    }

    [Fact]
    public void Resolve_InSteadyStateLoop_AllocatesZeroBytes()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["hot_source"] = new DataSourceProfileOptions
                {
                    QueryCostMultiplier = 1.2
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        // Warm up JIT
        for (int i = 0; i < 100; i++)
        {
            _ = resolver.Resolve("hot_source");
        }

        long beforeAlloc = GC.GetAllocatedBytesForCurrentThread();

        for (int i = 0; i < 10_000; i++)
        {
            var p = resolver.Resolve("hot_source");
            _ = p.QueryCostMultiplier;
        }

        long afterAlloc = GC.GetAllocatedBytesForCurrentThread();
        long diff = afterAlloc - beforeAlloc;

        diff.ShouldBe(0);
    }

    [Fact]
    public void EffectiveProfile_IsTransportAllowed_EvaluatesTransportFlagsBitmask()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["locked_source"] = new DataSourceProfileOptions
                {
                    AllowedTransports = DataSourceTransportFlags.GraphQL | DataSourceTransportFlags.Rest
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("locked_source");
        profile.IsTransportAllowed(DataSourceTransportFlags.GraphQL).ShouldBeTrue();
        profile.IsTransportAllowed(DataSourceTransportFlags.Rest).ShouldBeTrue();
        profile.IsTransportAllowed(DataSourceTransportFlags.WebSql).ShouldBeFalse();
        profile.IsTransportAllowed(DataSourceTransportFlags.Trino).ShouldBeFalse();
    }

    [Fact]
    public void TryResolve_ReturnsTrueForConfiguredSource_AndFalseForUnknownSource()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["registered"] = new DataSourceProfileOptions { QueryCostMultiplier = 2.0 }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        bool found = resolver.TryResolve("registered", out var regProfile);
        found.ShouldBeTrue();
        regProfile.DataSourceName.ShouldBe("registered");
        regProfile.QueryCostMultiplier.ShouldBe(2.0);

        bool unknownFound = resolver.TryResolve("non_existent", out var unkProfile);
        unknownFound.ShouldBeFalse();
        unkProfile.ShouldNotBeNull(); // Returns fallback default
    }

    [Fact]
    public void HierarchicalDataSourceTelemetryPolicyProvider_ReturnsClampedPolicy()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["telemetry_source"] = new DataSourceProfileOptions
                {
                    MinimumLogLevel = "Debug",
                    LogGeneratedSql = true
                }
            }
        });
        var telemetryMonitor = new TestMonitor<TelemetryOptions>(new TelemetryOptions
        {
            Redaction = new RedactionOptions { AllowStatementText = true }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);
        var provider = new HierarchicalDataSourceTelemetryPolicyProvider(resolver, telemetryMonitor);

        var policy = provider.GetPolicy("telemetry_source");
        policy.MinimumLogLevel.ShouldBe(Microsoft.Extensions.Logging.LogLevel.Debug);
        policy.StatementText.ShouldBe(Autheris.Domain.Diagnostics.StatementTextMode.Parameterized);
    }

    [Fact]
    public void Rebuild_AddsWebSqlDefaultAllowedAndMappedSources()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Defaults = new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.GraphQL },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["explicit_src"] = new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.Rest }
            }
        });
        var webSql = new WebSqlOptions
        {
            DefaultDataSourceName = "main_db",
            AllowedDataSources = ["allowed_db", "explicit_src"],
            DataSourceMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["alias"] = "mapped_db" },
            TenantDataSourceAllowlist = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase) { ["t1"] = ["tenant_db"] }
        };
        var gatewayMonitor = new TestMonitor<GatewayOptions>(new GatewayOptions { WebSql = webSql });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor, gatewayMonitor);

        foreach (var name in new[] { "main_db", "allowed_db", "mapped_db", "tenant_db" })
        {
            resolver.TryResolve(name, out var p).ShouldBeTrue(name);
            p.AllowedTransports.ShouldBe(DataSourceTransportFlags.GraphQL, name);
        }

        // the closure never overwrites an explicit profile (SEC-ATR-10)
        resolver.TryResolve("explicit_src", out var explicitProfile).ShouldBeTrue();
        explicitProfile.AllowedTransports.ShouldBe(DataSourceTransportFlags.Rest);
        resolver.TryResolve("alias", out _).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Gate_RebuildFailure_KeepsLastGood_SEC_ATR_10()
    {
        var monitor = new FlakyMonitor(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["src"] = new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.GraphQL }
            }
        });
        using var resolver = new HierarchicalDataSourceOptionsResolver(monitor);

        // a rebuild that fails keeps the previous snapshot (never an empty map, never the default for a known source)
        monitor.FailNext = true;
        monitor.RaiseChange();

        resolver.TryResolve("src", out var profile).ShouldBeTrue();
        profile.AllowedTransports.ShouldBe(DataSourceTransportFlags.GraphQL);
    }

    [Fact]
    public void Resolve_WhenProfileHasNonPositiveOrNegativeCeilingValues_ClampsToPositiveMinimum()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Ceilings = new DataSourceSafetyCeilingsOptions
            {
                MaxAllowedComplexityCeiling = 2000,
                MaxAggregateRowBudgetCeiling = 50_000,
                QueryTimeoutCeilingSeconds = 60,
                MaxResponseRowsCeiling = 10_000,
                MaxResponseBytesCeiling = 5_000_000,
                CommandTimeoutCeilingSeconds = 60,
                StagingTtlCeilingSeconds = 1800,
                MaxKeySetImportLimitCeiling = 500,
                AllowGlobalCeilingRelaxation = false
            },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["non_positive_source"] = new DataSourceProfileOptions
                {
                    MaxAllowedComplexity = -50,
                    MaxAggregateRowBudget = 0,
                    QueryTimeoutSeconds = -10,
                    MaxResponseRows = -1,
                    MaxResponseBytes = 0,
                    CommandTimeoutSeconds = -5,
                    StagingTtlSeconds = 0,
                    MaxKeySetImportLimit = -100
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("non_positive_source");

        profile.MaxAllowedComplexity.ShouldBe(1);
        profile.MaxAggregateRowBudget.ShouldBe(1L);
        profile.QueryTimeout.ShouldBe(TimeSpan.FromSeconds(1));
        profile.MaxResponseRows.ShouldBe(1L);
        profile.MaxResponseBytes.ShouldBe(1L);
        profile.CommandTimeout.ShouldBe(TimeSpan.FromSeconds(1));
        profile.StagingTtl.ShouldBe(TimeSpan.FromSeconds(1));
        profile.MaxKeySetImportLimit.ShouldBe(1);
    }

    [Fact]
    public void Resolve_WhenCeilingRelaxedAndNonPositiveValues_ClampsToPositiveMinimum()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Ceilings = new DataSourceSafetyCeilingsOptions
            {
                AllowGlobalCeilingRelaxation = true
            },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["relaxed_negative"] = new DataSourceProfileOptions
                {
                    AllowCeilingRelaxation = true,
                    MaxResponseRows = -1,
                    MaxAggregateRowBudget = 0,
                    MaxAllowedComplexity = -999
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("relaxed_negative");
        profile.CeilingRelaxed.ShouldBeTrue();
        profile.MaxResponseRows.ShouldBe(1L);
        profile.MaxAggregateRowBudget.ShouldBe(1L);
        profile.MaxAllowedComplexity.ShouldBe(1);
    }

    [Fact]
    public void RebuildFailure_LogsErrorAndMarksConfigurationStale()
    {
        var monitor = new FlakyMonitor(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["src"] = new DataSourceProfileOptions { AllowedTransports = DataSourceTransportFlags.GraphQL }
            }
        });
        var logger = new TestLogger<HierarchicalDataSourceOptionsResolver>();
        using var resolver = new HierarchicalDataSourceOptionsResolver(monitor, logger: logger);

        resolver.IsConfigurationStale.ShouldBeFalse();
        resolver.LastRebuildError.ShouldBeNull();

        // Cause rebuild to throw
        monitor.FailNext = true;
        monitor.RaiseChange();

        resolver.IsConfigurationStale.ShouldBeTrue();
        resolver.LastRebuildError.ShouldNotBeNull();
        resolver.LastRebuildError.ShouldBeOfType<InvalidOperationException>();
        resolver.LastRebuildError!.Message.ShouldBe("boom");

        logger.Logs.Count.ShouldBeGreaterThan(0);
        logger.Logs.ShouldContain(l => l.Level == LogLevel.Error && l.Exception == resolver.LastRebuildError);

        // Next successful rebuild recovers state
        monitor.FailNext = false;
        monitor.RaiseChange();

        resolver.IsConfigurationStale.ShouldBeFalse();
        resolver.LastRebuildError.ShouldBeNull();
    }

    [Fact]
    public void TryResolve_DistinguishesConfiguredSource_ConfiguredDefault_AndUnknownSource()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Defaults = new DataSourceProfileOptions
            {
                QueryCostMultiplier = 1.0,
                MaxResponseRows = 5000
            },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["known_source"] = new DataSourceProfileOptions { QueryCostMultiplier = 2.0 },
                ["default"] = new DataSourceProfileOptions { QueryCostMultiplier = 3.0 }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        // Known explicitly configured source
        resolver.TryResolve("known_source", out var known).ShouldBeTrue();
        known.DataSourceName.ShouldBe("known_source");
        known.QueryCostMultiplier.ShouldBe(2.0);
        known.IsUnresolved.ShouldBeFalse();
        known.IsResolved.ShouldBeTrue();

        // Known explicitly configured "default" profile
        resolver.TryResolve("default", out var explicitDefault).ShouldBeTrue();
        explicitDefault.DataSourceName.ShouldBe("default");
        explicitDefault.QueryCostMultiplier.ShouldBe(3.0);
        explicitDefault.IsUnresolved.ShouldBeFalse();
        explicitDefault.IsResolved.ShouldBeTrue();

        // Unknown source
        resolver.TryResolve("unknown_source", out var unknown).ShouldBeFalse();
        unknown.ShouldNotBeNull();
        unknown.IsUnresolved.ShouldBeTrue();
        unknown.IsResolved.ShouldBeFalse();

        // Null / whitespace source
        resolver.TryResolve(null, out var nullProfile).ShouldBeFalse();
        nullProfile.ShouldNotBeNull();
        nullProfile.IsUnresolved.ShouldBeTrue();

        resolver.TryResolve("   ", out var blankProfile).ShouldBeFalse();
        blankProfile.ShouldNotBeNull();
        blankProfile.IsUnresolved.ShouldBeTrue();

        // Resolve() also marks unresolved for unknown sources
        var resolvedUnknown = resolver.Resolve("unknown_source");
        resolvedUnknown.IsUnresolved.ShouldBeTrue();
        resolvedUnknown.IsResolved.ShouldBeFalse();

        var resolvedKnown = resolver.Resolve("known_source");
        resolvedKnown.IsUnresolved.ShouldBeFalse();
        resolvedKnown.IsResolved.ShouldBeTrue();
    }

    private sealed class TestLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Exception, string Message)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Logs.Add((logLevel, exception, formatter(state, exception)));
        }
    }

    private sealed class FlakyMonitor(DataSourceConfigurationOptions value) : IOptionsMonitor<DataSourceConfigurationOptions>
    {
        private Action<DataSourceConfigurationOptions, string?>? _listener;
        public bool FailNext { get; set; }

        public DataSourceConfigurationOptions CurrentValue => FailNext ? throw new InvalidOperationException("boom") : value;

        public DataSourceConfigurationOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<DataSourceConfigurationOptions, string?> listener)
        {
            _listener = listener;
            return null;
        }

        public void RaiseChange() => _listener?.Invoke(value, null);
    }

    private sealed class TestMonitor<T>(T initialValue) : IOptionsMonitor<T>
    {
        private T _currentValue = initialValue;
        private readonly List<Action<T, string?>> _listeners = new();

        public T CurrentValue => _currentValue;

        public T Get(string? name) => _currentValue;

        public IDisposable OnChange(Action<T, string?> listener)
        {
            _listeners.Add(listener);
            return new ActionDisposable(() => _listeners.Remove(listener));
        }

        public void Update(T newValue)
        {
            _currentValue = newValue;
            foreach (var listener in _listeners)
            {
                listener(newValue, null);
            }
        }

        private sealed class ActionDisposable(Action action) : IDisposable
        {
            public void Dispose() => action();
        }
    }
}
