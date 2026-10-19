namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Data;
using Autheris.Application.DataSources;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.Observability;
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
                    AllowedTransports = DataSourceTransportFlags.Trino | DataSourceTransportFlags.ArrowFlight
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
        profile.AllowedTransports.ShouldBe(DataSourceTransportFlags.Trino | DataSourceTransportFlags.ArrowFlight);
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
    public void IsTransportAllowed_EvaluatesTransportFlagsBitmask()
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

        resolver.IsTransportAllowed("locked_source", DataSourceTransportFlags.GraphQL).ShouldBeTrue();
        resolver.IsTransportAllowed("locked_source", DataSourceTransportFlags.Rest).ShouldBeTrue();
        resolver.IsTransportAllowed("locked_source", DataSourceTransportFlags.WebSql).ShouldBeFalse();
        resolver.IsTransportAllowed("locked_source", DataSourceTransportFlags.Trino).ShouldBeFalse();
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
