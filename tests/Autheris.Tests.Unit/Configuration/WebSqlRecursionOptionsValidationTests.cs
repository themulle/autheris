namespace Autheris.Tests.Unit.Configuration;

using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #277: start-time validation of the recursive CTE switch (FR-11, SEC-277-07, SEC-277-08).</summary>
public sealed class WebSqlRecursionOptionsValidationTests
{
    private static string[] Validate(WebSqlOptions webSql)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var result = new GatewayCoreOptionsValidator(environment).Validate(null, new GatewayOptions { WebSql = webSql });
        return result.Failures?.ToArray() ?? [];
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(10001)]
    public void MaxRecursionDepth_OutOfRange_FailsStart(int depth) =>
        Validate(new WebSqlOptions { MaxRecursionDepth = depth }).ShouldContain(f => f.Contains("MaxRecursionDepth"));

    [Theory]
    [InlineData(1)]
    [InlineData(10000)]
    public void MaxRecursionDepth_InRange_Passes(int depth) =>
        Validate(new WebSqlOptions { MaxRecursionDepth = depth }).ShouldNotContain(f => f.Contains("MaxRecursionDepth"));

    [Fact]
    public void Defaults_AreDisabledAnd100()
    {
        var defaults = new WebSqlOptions();

        defaults.AllowRecursiveCte.ShouldBeFalse();
        defaults.MaxRecursionDepth.ShouldBe(100);
        Validate(defaults).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData(0, true)]
    [InlineData(-5, true)]
    [InlineData(301, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(1, false)]
    [InlineData(30, false)]
    [InlineData(300, false)]
    public void SEC_277_07_Validator_AllowRecursiveCte_RequiresTimeoutUpTo300(int timeoutSeconds, bool fails)
    {
        var failures = Validate(new WebSqlOptions { AllowRecursiveCte = true, ExecutionTimeoutSeconds = timeoutSeconds });

        failures.Any(f => f.Contains("AllowRecursiveCte")).ShouldBe(fails);
    }

    [Trait("Category", "Security")]
    [Fact]
    public void SEC_277_07_TimeoutIsNotConstrained_WhenTheFlagIsOff() =>
        Validate(new WebSqlOptions { AllowRecursiveCte = false, ExecutionTimeoutSeconds = 3600 }).ShouldNotContain(f => f.Contains("AllowRecursiveCte"));

    [Trait("Category", "Security")]
    [Fact]
    public void SEC_277_08_StartupWarning_NamesTheTempSpacePrerequisite_OnlyWhenEnabled()
    {
        var enabledLogger = Substitute.For<ILogger>();
        enabledLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);
        var disabledLogger = Substitute.For<ILogger>();
        disabledLogger.IsEnabled(Arg.Any<LogLevel>()).Returns(true);

        GatewayStartupValidator.WarnAboutRecursiveCte(new GatewayOptions { WebSql = new WebSqlOptions { AllowRecursiveCte = true } }, enabledLogger);
        GatewayStartupValidator.WarnAboutRecursiveCte(new GatewayOptions { WebSql = new WebSqlOptions() }, disabledLogger);

        var warnings = enabledLogger.ReceivedCalls().Where(c => c.GetMethodInfo().Name == nameof(ILogger.Log)).ToList();
        warnings.Count.ShouldBe(1);
        warnings[0].GetArguments()[0].ShouldBe(LogLevel.Warning);
        warnings[0].GetArguments()[2]!.ToString()!.ShouldContain("temp");
        disabledLogger.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(ILogger.Log)).ShouldBe(0);
    }
}
