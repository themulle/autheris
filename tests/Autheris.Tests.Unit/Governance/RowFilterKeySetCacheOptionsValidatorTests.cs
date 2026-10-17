using Autheris.Application.Policy;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Databricks;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance;

/// <summary>Issue #107 (D-15, SEC-RF107-16): startup validation of Gateway:RowFilters:KeySetCache.</summary>
[Trait("Category", "Security")]
public sealed class RowFilterKeySetCacheOptionsValidatorTests
{
    private static (bool Ok, string Failures, CapturingLogger<RowFilterKeySetCacheOptionsValidator> Log) Run(
        RowFilterKeySetCacheOptions? cache = null, int webSqlTimeout = 30, int endpointTimeout = 60)
    {
        var log = new CapturingLogger<RowFilterKeySetCacheOptionsValidator>();
        var options = new GatewayOptions
        {
            RowFilters = new RowFilterOptions { KeySetCache = cache ?? new RowFilterKeySetCacheOptions() },
            WebSql = new WebSqlOptions { ExecutionTimeoutSeconds = webSqlTimeout },
            SqlEndpoints = new SqlEndpointsOptions { MaxQueryTimeoutSeconds = endpointTimeout }
        };
        var result = new RowFilterKeySetCacheOptionsValidator(log).Validate(null, options);
        return (result.Succeeded, string.Join("|", result.Failures ?? []), log);
    }

    [Fact]
    public void Defaults_AreValid_AndLogNoWarning()
    {
        var (ok, _, log) = Run();

        ok.ShouldBeTrue();
        log.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void Validator_WorksWithoutLogger()
        => new RowFilterKeySetCacheOptionsValidator().Validate(null, new GatewayOptions()).Succeeded.ShouldBeTrue();

    [Fact]
    public void DefaultStrategyKeySetCache_IsValid_ButLogsStartupWarning()
    {
        var (ok, _, log) = Run(new RowFilterKeySetCacheOptions { DefaultStrategy = RowFilterEvaluationStrategy.KeySetCache });

        ok.ShouldBeTrue();
        log.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Text.Contains("DefaultStrategy"));
    }

    [Fact]
    public void UndefinedDefaultStrategy_Fails()
        => Run(new RowFilterKeySetCacheOptions { DefaultStrategy = (RowFilterEvaluationStrategy)5 }).Ok.ShouldBeFalse();

    [Theory]
    [InlineData(0)]
    [InlineData(999)]
    public void DefaultDurationBelowOneSecond_Fails(int ms)
        => Run(new RowFilterKeySetCacheOptions { DefaultDuration = TimeSpan.FromMilliseconds(ms) }).Ok.ShouldBeFalse();

    [Fact]
    public void DefaultDurationAboveMaxDuration_Fails()
        => Run(new RowFilterKeySetCacheOptions { DefaultDuration = TimeSpan.FromMinutes(10), MaxDuration = TimeSpan.FromMinutes(5) }).Ok.ShouldBeFalse();

    [Fact]
    public void DefaultDurationEqualToMaxDuration_IsValid()
        => Run(new RowFilterKeySetCacheOptions { DefaultDuration = TimeSpan.FromMinutes(5), MaxDuration = TimeSpan.FromMinutes(5) }).Ok.ShouldBeTrue();

    [Fact]
    public void MaxDurationAbove24Hours_Fails_AndExactly24HoursIsValid()
    {
        Run(new RowFilterKeySetCacheOptions { MaxDuration = TimeSpan.FromHours(24) + TimeSpan.FromSeconds(1) }).Ok.ShouldBeFalse();
        Run(new RowFilterKeySetCacheOptions { MaxDuration = TimeSpan.FromHours(24) }).Ok.ShouldBeTrue();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    [InlineData(1, true)]
    [InlineData(10_000, true)]
    [InlineData(10_001, false)]
    public void MaxKeys_Range(int keys, bool valid)
        => Run(new RowFilterKeySetCacheOptions { MaxKeys = keys }).Ok.ShouldBe(valid);

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public void BindParameterReserve_Range(int reserve, bool valid)
        => Run(new RowFilterKeySetCacheOptions { BindParameterReserve = reserve }).Ok.ShouldBe(valid);

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(64, true)]
    [InlineData(65, false)]
    public void MaxConcurrentKeyQueries_Range_SEC_RF107_13(int n, bool valid)
        => Run(new RowFilterKeySetCacheOptions { MaxConcurrentKeyQueries = n }).Ok.ShouldBe(valid);

    [Theory]
    [InlineData(0L, false)]
    [InlineData(-5L, false)]
    [InlineData(1L, true)]
    public void SizeLimitBytes_MustBePositive(long bytes, bool valid)
        => Run(new RowFilterKeySetCacheOptions { SizeLimitBytes = bytes }).Ok.ShouldBe(valid);

    [Fact]
    public void KeyQueryTimeout_MustBePositive()
        => Run(new RowFilterKeySetCacheOptions { KeyQueryTimeout = TimeSpan.Zero }).Ok.ShouldBeFalse();

    [Fact]
    public void KeyQueryTimeout_AboveSmallestStatementTimeout_Fails()
    {
        // WebSQL 30 s is the smaller of the two endpoints' timeouts.
        Run(new RowFilterKeySetCacheOptions { KeyQueryTimeout = TimeSpan.FromSeconds(31) }, webSqlTimeout: 30, endpointTimeout: 60).Ok.ShouldBeFalse();
        Run(new RowFilterKeySetCacheOptions { KeyQueryTimeout = TimeSpan.FromSeconds(30) }, webSqlTimeout: 30, endpointTimeout: 60).Ok.ShouldBeTrue();
        // SQL endpoints 10 s is the smaller one.
        Run(new RowFilterKeySetCacheOptions { KeyQueryTimeout = TimeSpan.FromSeconds(11) }, webSqlTimeout: 30, endpointTimeout: 10).Ok.ShouldBeFalse();
    }

    [Fact]
    public void FailureMessages_NameOptionPaths_AndCollectAllFailures()
    {
        var (ok, failures, _) = Run(new RowFilterKeySetCacheOptions { MaxKeys = 0, BindParameterReserve = 5000, MaxConcurrentKeyQueries = 0 });

        ok.ShouldBeFalse();
        failures.ShouldContain("Gateway:RowFilters:KeySetCache:MaxKeys");
        failures.ShouldContain("Gateway:RowFilters:KeySetCache:BindParameterReserve");
        failures.ShouldContain("Gateway:RowFilters:KeySetCache:MaxConcurrentKeyQueries");
    }
}
