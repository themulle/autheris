using Autheris.Application.Sql.Services;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>Issue #447 (SEC-ATR-01, SEC-ATR-13): the fail-open transport paths are gone; the transport gate is the only decision point.</summary>
[Trait("Category", "Security")]
public sealed class TransportGateArchitectureTests
{
    [Fact]
    public void DataSourceAllowlistGuard_HasNoEnsureTransportAllowed()
    {
        typeof(DataSourceAllowlistGuard).GetMethods().Select(m => m.Name).ShouldNotContain("EnsureTransportAllowed");
    }

    [Fact]
    public void DataSourceOptionsResolver_HasNoIsTransportAllowed()
    {
        typeof(Autheris.Application.DataSources.IDataSourceOptionsResolver).GetMethods().Select(m => m.Name).ShouldNotContain("IsTransportAllowed");
        typeof(Autheris.Application.DataSources.HierarchicalDataSourceOptionsResolver).GetMethods().Select(m => m.Name).ShouldNotContain("IsTransportAllowed");
    }
}
