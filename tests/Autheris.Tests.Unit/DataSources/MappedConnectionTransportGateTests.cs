namespace Autheris.Tests.Unit.DataSources;

using Autheris.Application.DataSources;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>#668: the transport gate and the per-source profile of a mapped logical name come from the connection key.</summary>
[Trait("Category", "Security")]
public sealed class MappedConnectionTransportGateTests
{
    private static (DataSourceTransportGate Gate, HierarchicalDataSourceOptionsResolver Resolver) Build(
        DataSourceTransportFlags logicalProfile, DataSourceTransportFlags connectionProfile)
    {
        var resolver = new HierarchicalDataSourceOptionsResolver(
            new DataSourceTransportGateTests.FixedMonitor<DataSourceConfigurationOptions>(new DataSourceConfigurationOptions
            {
                Profiles = new Dictionary<string, DataSourceProfileOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["sales"] = new() { AllowedTransports = logicalProfile },
                    ["pgdata"] = new() { AllowedTransports = connectionProfile }
                }
            }),
            new DataSourceTransportGateTests.FixedMonitor<GatewayOptions>(new GatewayOptions
            {
                WebSql = new WebSqlOptions { DataSourceMappings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["sales"] = "pgdata" } }
            }));
        var context = new IngressTransportContext();
        context.Set(DataSourceTransportFlags.WebSql);
        return (new DataSourceTransportGate(context, resolver, Substitute.For<ITransportDenialRecorder>()), resolver);
    }

    [Fact]
    public async Task MappedLogicalName_GateEvaluatesEffectiveConnection()
    {
        var (gate, resolver) = Build(logicalProfile: DataSourceTransportFlags.WebSql, connectionProfile: DataSourceTransportFlags.GraphQL);
        using var _ = resolver;

        await Should.ThrowAsync<TransportNotAllowedException>(() => gate.EnsureCatalogSourceAsync("sales", CancellationToken.None));
    }

    [Fact]
    public async Task Profile_ComesFromConnectionKey_NotLogicalName()
    {
        var (gate, resolver) = Build(logicalProfile: DataSourceTransportFlags.GraphQL, connectionProfile: DataSourceTransportFlags.WebSql);
        using var _ = resolver;

        await Should.NotThrowAsync(() => gate.EnsureCatalogSourceAsync("sales", CancellationToken.None));
    }
}
