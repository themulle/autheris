namespace Autheris.Tests.Unit.DataSources;

using Autheris.Application.DataSources;
using Autheris.Domain.Options.DataSources;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #447 WP-3, owner decision 1 (R1): a source named by the data catalog (never by client input) that has no profile inherits
/// <c>Defaults</c> instead of reading as unknown; a blank name, an unset ingress and an explicit <c>None</c> still deny (SEC-ATR-01).
/// </summary>
[Trait("Category", "Security")]
public sealed class CatalogKnownSourceTransportTests
{
    private static HierarchicalDataSourceOptionsResolver Build(DataSourceTransportFlags? defaults, params (string Name, DataSourceTransportFlags Flags)[] profiles)
    {
        var options = new DataSourceConfigurationOptions { Defaults = new DataSourceProfileOptions { AllowedTransports = defaults } };
        foreach (var (name, flags) in profiles)
        {
            options.Profiles[name] = new DataSourceProfileOptions { AllowedTransports = flags };
        }

        return new HierarchicalDataSourceOptionsResolver(new DataSourceTransportGateTests.FixedMonitor<DataSourceConfigurationOptions>(options));
    }

    [Fact]
    public void CatalogSourceWithoutProfile_InheritsDefaults_Allowed()
    {
        using var resolver = Build(null);
        DataSourceTransportGate.Decide(resolver, "crm", DataSourceTransportFlags.WebSql, catalogKnown: true).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void CatalogSourceWithoutProfile_DefaultsRestricted_Denied()
    {
        using var resolver = Build(DataSourceTransportFlags.GraphQL);
        DataSourceTransportGate.Decide(resolver, "crm", DataSourceTransportFlags.WebSql, catalogKnown: true).Reason.ShouldBe(TransportDecisionReason.FlagNotSet);
        DataSourceTransportGate.Decide(resolver, "crm", DataSourceTransportFlags.GraphQL, catalogKnown: true).IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public void CatalogSource_ExplicitProfile_WinsOverDefaults()
    {
        using var resolver = Build(DataSourceTransportFlags.All, ("crm", DataSourceTransportFlags.None));
        DataSourceTransportGate.Decide(resolver, "crm", DataSourceTransportFlags.WebSql, catalogKnown: true).Reason.ShouldBe(TransportDecisionReason.FlagNotSet);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CatalogSource_BlankName_StillDenied(string? name)
    {
        using var resolver = Build(null);
        DataSourceTransportGate.Decide(resolver, name, DataSourceTransportFlags.WebSql, catalogKnown: true).Reason.ShouldBe(TransportDecisionReason.UnknownSource);
    }

    [Fact]
    public void CatalogSource_UnsetOrMultiBitIngress_StillDenied()
    {
        using var resolver = Build(null);
        DataSourceTransportGate.Decide(resolver, "crm", DataSourceTransportFlags.None, catalogKnown: true).Reason.ShouldBe(TransportDecisionReason.TransportUnset);
        DataSourceTransportGate.Decide(resolver, "crm", DataSourceTransportFlags.WebSql | DataSourceTransportFlags.Rest, catalogKnown: true).Reason
            .ShouldBe(TransportDecisionReason.InvalidTransport);
    }

    [Fact]
    public void ClientNamedUnknownSource_StaysDenied_OnTheStrictForm()
    {
        using var resolver = Build(null);
        DataSourceTransportGate.Decide(resolver, "crm", DataSourceTransportFlags.WebSql).Reason.ShouldBe(TransportDecisionReason.UnknownSource);
    }
}
