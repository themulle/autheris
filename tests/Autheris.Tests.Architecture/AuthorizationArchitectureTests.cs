namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.OData;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Services;
using Autheris.GraphQL.Interceptors;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AuthorizationArchitectureTests
{
    private const string ExpectedPdpConstructionFile = "src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs";

    [Fact]
    public void Only_Pdp_Constructs_TableAccessPolicy()
    {
        var violatingFiles = new List<string>();

        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (file.Text.Contains("new TableAccessPolicy(") || file.Text.Contains("new Autheris.Application.Policy.TableAccessPolicy("))
            {
                if (!string.Equals(file.RelativePath, ExpectedPdpConstructionFile, StringComparison.Ordinal))
                {
                    violatingFiles.Add(file.RelativePath);
                }
            }
        }

        violatingFiles.ShouldBeEmpty(
            $"SEC-AZ-01 violation: Only {ExpectedPdpConstructionFile} may construct TableAccessPolicy. " +
            $"Found unauthorized construction in: {string.Join(", ", violatingFiles)}");
    }

    [Theory]
    [InlineData(typeof(GovernedSqlRewriter))]
    [InlineData(typeof(GatewayExecutionService))]
    [InlineData(typeof(GovernedProcedureExecutionService))]
    [InlineData(typeof(FederatedDuckDbExecutionService))]
    [InlineData(typeof(DefaultCrossDomainAccessResolver))]
    [InlineData(typeof(ODataQueryPolicyEnforcer))]
    [InlineData(typeof(Autheris.Application.Streaming.Services.StreamRlsPolicyEnforcer))]
    [InlineData(typeof(Autheris.Application.Mesh.Services.EnvoyExtAuthzService))]
    [InlineData(typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService))]
    [InlineData(typeof(Autheris.Application.Mcp.Services.AiDataGuardrailService))]
    public void Every_Transport_Entry_Depends_On_IAccessDecisionPoint(Type transportType)
    {
        var pdpType = typeof(IAccessDecisionPoint);

        var constructors = transportType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var hasPdpParameter = constructors.Any(c => c.GetParameters().Any(p => p.ParameterType == pdpType));

        var fields = transportType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var hasPdpField = fields.Any(f => f.FieldType == pdpType || f.FieldType.Name.Contains("IAccessDecisionPoint"));

        (hasPdpParameter || hasPdpField).ShouldBeTrue(
            $"Transport type {transportType.Name} must depend on {pdpType.FullName} (constructor parameter or field).");
    }

    [Fact]
    public void GraphQlDataSecurityMiddleware_Uses_IAccessDecisionPoint()
    {
        var pdpType = typeof(IAccessDecisionPoint);
        var methods = typeof(GraphQlDataSecurityMiddleware).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        var hasPdpMethod = methods.Any(m => m.GetParameters().Any(p => p.ParameterType == pdpType));

        hasPdpMethod.ShouldBeTrue("GraphQlDataSecurityMiddleware must expose methods taking IAccessDecisionPoint.");
    }
}
