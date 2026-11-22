namespace Autheris.Tests.Architecture;

using System;
using System.Linq;
using Autheris.Api.Endpoints;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.OData;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Services;
using Autheris.Application.Streaming.Services;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ProtectionGateCoverageArchitectureTests
{
    [Fact]
    public void Every_row_returning_path_uses_the_pdp()
    {
        var pdpInterface = typeof(IAccessDecisionPoint);
        var executionTypes = new[]
        {
            typeof(GatewayExecutionService),
            typeof(GovernedSqlRewriter),
            typeof(GovernedProcedureExecutionService),
            typeof(StreamRlsPolicyEnforcer),
            typeof(FederatedDuckDbExecutionService),
            typeof(DiscoveryArtifactService),
            typeof(ODataQueryPolicyEnforcer)
        };

        foreach (var type in executionTypes)
        {
            var referencesPdp = type.GetConstructors().Any(c => c.GetParameters().Any(p => p.ParameterType == pdpInterface)) ||
                                type.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                                    .Any(f => f.FieldType == pdpInterface);
            referencesPdp.ShouldBeTrue($"{type.Name} must route decisions through {pdpInterface.Name}.");
        }

        var asyncEndpointsSource = RepositoryScan.ReadText("src/Autheris.Api/Endpoints/AsyncJobEndpoints.cs");
        asyncEndpointsSource.ShouldContain(nameof(IAccessDecisionPoint));

        var policySource = RepositoryScan.ReadText("src/Autheris.Application/Policy/TableAccessPolicy.Protection.cs");
        policySource.ShouldContain("InternalAudienceResolver.IsInternal");
        policySource.ShouldContain("RollupEffectiveTableClass");

        typeof(AccessDecisionPoint).GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(Autheris.Application.Common.Interfaces.IProtectionSnapshotProvider)))
            .ShouldBeTrue("AccessDecisionPoint must accept IProtectionSnapshotProvider.");
    }
}
