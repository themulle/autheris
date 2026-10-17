namespace Autheris.Tests.Unit.Catalog;

using System.Linq;
using Autheris.Api.Configuration.Validation;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #111 T-1 item 5 (start-up part): <c>Gateway:Catalog:MaxTagsPerDataset</c> 1-256, <c>DefaultVisibility</c> only Hidden or Summary.</summary>
public sealed class DatasetCatalogOptionsValidationTests
{
    private static bool Fails(DataCatalogOptions catalog, string optionName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns("Production");
        var result = new GatewayCoreOptionsValidator(environment).Validate(null, new GatewayOptions { Catalog = catalog });
        return result.Failed && result.Failures!.Any(f => f.Contains(optionName));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(32, false)]
    [InlineData(256, false)]
    [InlineData(0, true)]
    [InlineData(257, true)]
    [InlineData(-5, true)]
    public void MaxTagsPerDataset_RangeIsValidated(int value, bool fails) =>
        Fails(new DataCatalogOptions { MaxTagsPerDataset = value }, "Gateway:Catalog:MaxTagsPerDataset").ShouldBe(fails);

    [Theory]
    [InlineData(DatasetVisibility.Hidden, false)]
    [InlineData(DatasetVisibility.Summary, false)]
    [InlineData(DatasetVisibility.Full, true)]
    [InlineData((DatasetVisibility)7, true)]
    public void DefaultVisibility_FullAndUnknownValuesFailStartup(DatasetVisibility value, bool fails) =>
        Fails(new DataCatalogOptions { DefaultVisibility = value }, "Gateway:Catalog:DefaultVisibility").ShouldBe(fails);
}
