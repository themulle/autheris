using System.Reflection;
using Autheris.Application.Streaming.Services;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// Issue #447 (SEC-ATR-04, SEC-ATR-13): <c>/api/v1/cdc/events</c> and <c>/api/v1/cdc/streams/{id}/resync</c> are exempt from the ingress
/// stamping because they read no source: an event is pushed by the caller, a resync restarts a stream consumer. This proves the claim
/// structurally: the services behind both routes (and the route classes) depend on no choke-point or source-reader type, so adding one
/// (which would read rows or row-derived data) fails here and the route must then be stamped and gated instead.
/// </summary>
[Trait("Category", "Security")]
public sealed class CdcRoutesReachNoSourceReaderTests
{
    private static readonly string[] SourceReaderTypeNames =
    [
        "IDataSourceExecutor", "IChangeVersionProvider", "IGatewayExecutionService", "IGovernedSqlExecutionService",
        "IGovernedAggregationService", "IOlapStagingService", "IFederatedStagingService", "IFederatedQueryExecutionService",
        "ISqlConnectionFactory", "IAutherisConnectorRegistry", "IAutherisConnectorFactory", "GovernedConnectorReader",
        "IGovernedChangeDetectionService", "IMcpDatasetCatalog", "ISqlTableReadExecutor"
    ];

    [Theory]
    [InlineData(typeof(CdcEventIngestionService))]
    [InlineData(typeof(CdcStreamAdminService))]
    public void SEC_ATR_04_CdcRouteServices_DependOnNoSourceReader(Type service)
    {
        var offenders = DependencyTypes(service).Where(t => SourceReaderTypeNames.Contains(t.Name)).Select(t => t.FullName).ToList();

        offenders.ShouldBeEmpty($"{service.Name} must not read sources (the CDC routes are exempt from the ingress gate).");
    }

    [Theory]
    [InlineData("Autheris.Api.Endpoints.StreamingCdcEndpoints")]
    [InlineData("Autheris.Api.Endpoints.CdcStreamAdminEndpoints")]
    public void SEC_ATR_04_CdcRouteClasses_TakeNoSourceReader(string routeClass)
    {
        var type = typeof(Autheris.Api.Endpoints.ChangeDetectionEndpoints).Assembly.GetType(routeClass, throwOnError: true)!;
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var parameters = type.GetMethods(all).SelectMany(m => m.GetParameters()).Select(p => p.ParameterType);
        var closures = type.GetNestedTypes(all).SelectMany(n => n.GetFields(all)).Select(f => f.FieldType);

        parameters.Concat(closures).Where(t => SourceReaderTypeNames.Contains(t.Name)).ShouldBeEmpty();
    }

    private static IEnumerable<Type> DependencyTypes(Type type)
    {
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        foreach (var ctor in type.GetConstructors(all))
        {
            foreach (var parameter in ctor.GetParameters())
            {
                yield return parameter.ParameterType;
            }
        }

        foreach (var field in type.GetFields(all))
        {
            yield return field.FieldType;
        }
    }
}
