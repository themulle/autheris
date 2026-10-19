namespace Autheris.Tests.Unit.DataSources;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.DataSources;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Options.Observability;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

public sealed class DataSourceSafetyCeilingSecurityTests
{
    [Fact]
    public void SecDs05_01_StrictAntiEscalationSafetyCeilings_ClampsValuesAboveCeilings()
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
                ["escalated_source"] = new DataSourceProfileOptions
                {
                    MaxAllowedComplexity = 99_999,
                    MaxAggregateRowBudget = 99_999_999,
                    QueryTimeoutSeconds = 1200,
                    MaxResponseRows = 500_000,
                    MaxResponseBytes = 100_000_000,
                    CommandTimeoutSeconds = 1200,
                    StagingTtlSeconds = 86_400,
                    MaxKeySetImportLimit = 50_000,
                    AllowCeilingRelaxation = false
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("escalated_source");

        profile.MaxAllowedComplexity.ShouldBe(2000);
        profile.MaxAggregateRowBudget.ShouldBe(50_000);
        profile.QueryTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        profile.MaxResponseRows.ShouldBe(10_000);
        profile.MaxResponseBytes.ShouldBe(5_000_000);
        profile.CommandTimeout.ShouldBe(TimeSpan.FromSeconds(60));
        profile.StagingTtl.ShouldBe(TimeSpan.FromSeconds(1800));
        profile.MaxKeySetImportLimit.ShouldBe(500);
        profile.CeilingRelaxed.ShouldBeFalse();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public void SecDs05_02_DualAdministrativeOptIn_RequiredForCeilingRelaxation(
        bool sourceOptIn,
        bool globalAdminOptIn,
        bool expectedRelaxed)
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Ceilings = new DataSourceSafetyCeilingsOptions
            {
                MaxResponseRowsCeiling = 10_000,
                AllowGlobalCeilingRelaxation = globalAdminOptIn
            },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["test_source"] = new DataSourceProfileOptions
                {
                    MaxResponseRows = 50_000,
                    AllowCeilingRelaxation = sourceOptIn
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("test_source");

        profile.CeilingRelaxed.ShouldBe(expectedRelaxed);
        if (expectedRelaxed)
        {
            profile.MaxResponseRows.ShouldBe(50_000); // Unclamped
        }
        else
        {
            profile.MaxResponseRows.ShouldBe(10_000); // Clamped to ceiling
        }
    }

    [Fact]
    public void SecDs05_03_FailClosedTransportEnforcement_RejectsUnauthorizedTransports()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["graphql_only"] = new DataSourceProfileOptions
                {
                    AllowedTransports = DataSourceTransportFlags.GraphQL
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        // Allowed transport succeeds
        Should.NotThrow(() =>
            DataSourceAllowlistGuard.EnsureTransportAllowed(resolver, "graphql_only", DataSourceTransportFlags.GraphQL));

        // Unauthorized transport fails closed
        var ex = Should.Throw<WebSqlPolicyException>(() =>
            DataSourceAllowlistGuard.EnsureTransportAllowed(resolver, "graphql_only", DataSourceTransportFlags.WebSql));

        ex.Message.ShouldContain("not allowed over transport");
    }

    [Fact]
    public void SecDs05_04_CredentialIsolation_EffectiveProfileExposesNoSecrets()
    {
        var properties = typeof(EffectiveDataSourceProfile).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            var name = prop.Name;
            name.ShouldNotContain("Password", Case.Insensitive);
            name.ShouldNotContain("Secret", Case.Insensitive);
            name.ShouldNotContain("ConnectionString", Case.Insensitive);
            name.ShouldNotContain("KeyVault", Case.Insensitive);
        }
    }

    [Fact]
    public void SecDs05_05_FailClosedUnknownSourceResolution_NeverThrowsAndProvidesSafeDefaults()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions());
        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("completely_unknown_source");

        profile.ShouldNotBeNull();
        profile.CeilingRelaxed.ShouldBeFalse();
        profile.MaxResponseRows.ShouldBeLessThanOrEqualTo(100_000);
        profile.CommandTimeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(300));
    }

    [Fact]
    public void SecDs05_06_TelemetryRedactionCeilingPrecedence_DisablesSqlLoggingWhenRedacted()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["sensitive_source"] = new DataSourceProfileOptions
                {
                    LogGeneratedSql = true // Source requests SQL logging
                }
            }
        });

        // Global telemetry forbids statement text (Redaction.AllowStatementText = false)
        var telemetryMonitor = new TestMonitor<TelemetryOptions>(new TelemetryOptions
        {
            Redaction = new RedactionOptions { AllowStatementText = false }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor, null, telemetryMonitor);

        var profile = resolver.Resolve("sensitive_source");

        // Global redaction ceiling wins: SQL logging is suppressed
        profile.LogGeneratedSql.ShouldBeFalse();
    }

    [Fact]
    public void SecDs05_07_OutboundTracePropagationLeakagePrevention_SetsNeverMode()
    {
        var configMonitor = new TestMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["external_partner_api"] = new DataSourceProfileOptions
                {
                    OutboundPropagation = OutboundPropagationMode.Never
                }
            }
        });

        using var resolver = new HierarchicalDataSourceOptionsResolver(configMonitor);

        var profile = resolver.Resolve("external_partner_api");

        profile.OutboundPropagation.ShouldBe(OutboundPropagationMode.Never);
    }

    [Fact]
    public void SecDs05_08_StartupValidationOfConfigurationIntegrity_FailsFastOnInvalidOptions()
    {
        var validator = new DataSourceOptionsValidator();

        var invalidOptions = new DataSourceConfigurationOptions
        {
            Ceilings = new DataSourceSafetyCeilingsOptions
            {
                CommandTimeoutCeilingSeconds = -5 // Invalid negative ceiling
            },
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["broken_source"] = new DataSourceProfileOptions
                {
                    SamplingRatio = 2.5, // Invalid > 1.0
                    MinPoolSize = 50,
                    MaxPoolSize = 10     // Invalid min > max
                }
            }
        };

        var result = validator.Validate("DataSourceConfiguration", invalidOptions);

        result.Failed.ShouldBeTrue();
        result.Failures.ShouldNotBeEmpty();
        result.Failures.Any(f => f.Contains("CommandTimeoutCeilingSeconds")).ShouldBeTrue();
        result.Failures.Any(f => f.Contains("SamplingRatio")).ShouldBeTrue();
        result.Failures.Any(f => f.Contains("MinPoolSize cannot exceed MaxPoolSize")).ShouldBeTrue();

        var validOptions = new DataSourceConfigurationOptions
        {
            Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["valid_source"] = new DataSourceProfileOptions
                {
                    QueryCostMultiplier = 2.0,
                    SamplingRatio = 0.5,
                    MinPoolSize = 5,
                    MaxPoolSize = 20
                }
            }
        };

        var validResult = validator.Validate("DataSourceConfiguration", validOptions);
        validResult.Succeeded.ShouldBeTrue();
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
