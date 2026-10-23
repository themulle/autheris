namespace Autheris.Tests.Unit.Sql.TimeZones;

using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>BC-130-9 / ST-130-7: an operator cannot re-open an unvalidated time zone function through <c>WebSql:AdditionalAllowedFunctions</c>.</summary>
[Trait("Category", "Security")]
public sealed class ConvertTimeZoneOptionsTests
{
    private static ValidateResult Validate(params string[] additional)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var result = new GatewayCoreOptionsValidator(environment).Validate(null, new GatewayOptions { WebSql = new WebSqlOptions { AdditionalAllowedFunctions = [.. additional] } });
        return new ValidateResult(result.Failed, result.Failures?.ToList() ?? []);
    }

    private sealed record ValidateResult(bool Failed, System.Collections.Generic.List<string> Failures);

    [Theory]
    [InlineData("timezone")]
    [InlineData("TIMEZONE")]
    [InlineData("at_timezone")]
    [InlineData("with_timezone")]
    [InlineData("switchoffset")]
    [InlineData("SwitchOffset")]
    [InlineData("todatetimeoffset")]
    [InlineData("timezone_hour")]
    [InlineData("timezone_minute")]
    [InlineData("convert_timezone")]
    [InlineData("dbo.switchoffset")]
    [InlineData("pg_catalog.timezone")]
    [InlineData("\"timezone\"")]
    [InlineData("[switchoffset]")]
    [InlineData("  timezone  ")]
    public void StartupRejectsATimeZoneFunctionInTheAdditionalList(string entry)
    {
        var result = Validate("lower", entry);
        result.Failed.ShouldBeTrue(entry);
        result.Failures.ShouldContain(f => f.Contains("Gateway:WebSql:AdditionalAllowedFunctions"));
    }

    [Theory]
    [InlineData("lower")]
    [InlineData("abs")]
    [InlineData("my_timezone_helper")]
    [InlineData("timezone_name")]
    public void OrdinaryEntriesAreStillAccepted(string entry) => Validate(entry).Failed.ShouldBeFalse();

    [Fact]
    public void NoEntryAtAllIsAccepted() => Validate().Failed.ShouldBeFalse();

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.DuckDb)]
    [InlineData(TargetSqlDialect.Sqlite)]
    public void TheAllowlistBuilderNeverAdmitsTheRelatedNamesEvenIfAskedTo(TargetSqlDialect dialect)
    {
        var allowlist = SqlFunctionAllowlists.Build(dialect, ["timezone", "AT_TIMEZONE", "with_timezone", "switchoffset", "todatetimeoffset", "timezone_hour", "timezone_minute", "abs"]);
        foreach (var name in new[] { "timezone", "at_timezone", "with_timezone", "switchoffset", "todatetimeoffset", "timezone_hour", "timezone_minute" })
        {
            allowlist.ShouldNotContain(name);
        }

        // GAP-019: SQLite has no zone database and does not advertise convert_timezone.
        if (dialect == TargetSqlDialect.Sqlite) allowlist.ShouldNotContain("convert_timezone");
        else allowlist.ShouldContain("convert_timezone");
    }
}
