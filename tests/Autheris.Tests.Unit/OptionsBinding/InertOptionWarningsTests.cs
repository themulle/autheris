using Autheris.Api.Configuration;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.OptionsBinding;

[Trait("Category", "Security")]
public sealed class InertOptionWarningsTests
{
    [Fact]
    public void DefaultSubqueryStrategy_ProducesNoWarning()
    {
        InertOptionWarnings.Collect(new GatewayOptions()).ShouldBeEmpty();
    }
}
