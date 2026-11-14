using Autheris.Api.Configuration;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.OptionsBinding;

public sealed class InertOptionWarningsTests
{
    [Fact]
    public void DefaultSubqueryStrategy_ProducesNoWarning()
    {
        InertOptionWarnings.Collect(new GatewayOptions()).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(RowFilterSubqueryStrategy.In)]
    [InlineData(RowFilterSubqueryStrategy.InCorrelated)]
    public void NonDefaultSubqueryStrategy_WarnsThatTheGovernedSqlPathIgnoresIt(RowFilterSubqueryStrategy strategy)
    {
        var options = new GatewayOptions();
        options.RowFilters.SubqueryStrategy = strategy;

        var warning = InertOptionWarnings.Collect(options).ShouldHaveSingleItem();

        warning.ShouldContain("Gateway:RowFilters:SubqueryStrategy");
        warning.ShouldContain(strategy.ToString());
        warning.ShouldContain("WebSQL");
        warning.ShouldContain("REST data API");
    }
}
