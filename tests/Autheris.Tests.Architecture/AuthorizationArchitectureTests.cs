namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.OData;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Model;
using Autheris.GraphQL.Interceptors;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class AuthorizationArchitectureTests
{
    private const string ExpectedPdpConstructionFile = "src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs";

    [Fact]
    public void Architecture_NoUnifiedPolicyDecisionPointReferences()
    {
        // Built from parts so this test file does not match its own scan.
        var retiredNames = new[] { "Unified" + "PolicyDecisionPoint", "IUnified" + "PolicyDecisionPoint" };
        var offenders = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (retiredNames.Any(name => file.Text.Contains(name, StringComparison.Ordinal)))
            {
                offenders.Add(file.RelativePath);
            }
        }

        foreach (var path in RepositoryScan.EnumerateFiles("tests", "*.cs"))
        {
            if (path.EndsWith("AuthorizationArchitectureTests.cs", StringComparison.Ordinal))
            {
                continue; // this ratchet names the retired type in its own test name
            }

            var text = File.ReadAllText(Path.Combine(RepositoryScan.RepoRoot, path));
            if (retiredNames.Any(name => text.Contains(name, StringComparison.Ordinal)))
            {
                offenders.Add(path);
            }
        }

        offenders.ShouldBeEmpty("The unified policy decision point adapter was removed; every consumer uses IAccessDecisionPoint.");
        typeof(IAccessDecisionPoint).Assembly.GetTypes()
            .Where(t => retiredNames.Contains(t.Name, StringComparer.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public void Architecture_MigratedReadConsumers_UseReadDecisionsWithTheExpectedTransport()
    {
        string[] analytics =
        [
            "src/Autheris.Application/Analytics/GovernedAggregationService.ValidationAndExecution.cs",
            "src/Autheris.Application/Analytics/SemanticMetricQueryService.ConsentAndEvaluation.cs"
        ];
        string[] rest =
        [
            "src/Autheris.Application/Policy/GovernedQueryStages.cs",
            "src/Autheris.Application/DataSources/Relations/CrossSourceRelationResolver.Helpers.cs"
        ];
        string[] viaStage =
        [
            "src/Autheris.Api/Endpoints/TableStreamEndpoints.cs",
            "src/Autheris.Application/DataSources/Services/GovernedDataQueryService.cs",
            "src/Autheris.Application/DataSources/Changes/GovernedChangeDetectionService.cs"
        ];
        var files = RepositoryScan.ProductionFiles.ToDictionary(f => f.RelativePath, f => f.Text, StringComparer.Ordinal);
        var problems = new List<string>();

        foreach (var path in analytics.Concat(rest).Concat(viaStage))
        {
            var text = files[path];
            if (text.Contains("new AccessRequest(", StringComparison.Ordinal) || text.Contains("AccessOperation.", StringComparison.Ordinal))
            {
                problems.Add($"{path}: builds its own request; read consumers go through ReadAccessDecisions (always Read).");
            }

            if (text.Contains("AccessTransport.Internal", StringComparison.Ordinal))
            {
                problems.Add($"{path}: caller-facing reads never use the Internal transport.");
            }
        }

        problems.AddRange(analytics.Where(p => !files[p].Contains("AccessTransport.Analytics", StringComparison.Ordinal))
            .Select(p => $"{p}: must decide with the Analytics transport."));
        problems.AddRange(analytics.Where(p => files[p].Contains("AccessTransport.Rest", StringComparison.Ordinal))
            .Select(p => $"{p}: must not use the Rest transport."));
        problems.AddRange(rest.Where(p => !files[p].Contains("AccessTransport.Rest", StringComparison.Ordinal))
            .Select(p => $"{p}: must decide with the Rest transport."));
        problems.AddRange(viaStage.Where(p => !files[p].Contains("AuthorizeTableAsync(", StringComparison.Ordinal))
            .Select(p => $"{p}: must authorize through GovernedQueryStages.AuthorizeTableAsync (Rest)."));

        problems.ShouldBeEmpty();
    }

    [Fact]
    public void Architecture_PdpProvenance_IsStampedOnlyInsideThePdpNamespace()
    {
        var offenders = RepositoryScan.ProductionFiles
            .Where(f => f.Text.Contains("EvaluatedByPdp = true", StringComparison.Ordinal))
            .Select(f => f.RelativePath)
            .Where(path => !path.StartsWith("src/Autheris.Application/Policy/Pdp/", StringComparison.Ordinal))
            .ToList();

        offenders.ShouldBeEmpty("Only the access decision point may mark a decision as evaluated by the PDP.");
    }

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

    [Fact]
    public void Transport_error_mappers_never_reference_DecisionReason()
    {
        var violatingFiles = new List<string>();

        foreach (var file in RepositoryScan.ProductionFiles)
        {
            var fileName = System.IO.Path.GetFileName(file.RelativePath);
            if ((fileName.Contains("Error", StringComparison.Ordinal)
                    || fileName.Contains("ExceptionHandler", StringComparison.Ordinal)
                    || fileName.Contains("ExceptionStatusMapper", StringComparison.Ordinal))
                && (file.Text.Contains("DecisionReason", StringComparison.Ordinal)
                    || file.Text.Contains("DecisionGrantRef", StringComparison.Ordinal)))
            {
                violatingFiles.Add(file.RelativePath);
            }
        }

        violatingFiles.ShouldBeEmpty(
            $"DecisionReason and DecisionGrantRef must be surfaced only by explain and audit, never by transport error mappers: {string.Join(", ", violatingFiles)}");
    }

    [Fact]
    public void No_free_sql_predicate_on_grant_models()
    {
        Type[] grantModelTypes =
        [
            typeof(Consent),
            typeof(AccessProfile),
            typeof(AccessProfileWriteRule),
            typeof(AccessProfileAssignment),
            typeof(WriteRule)
        ];

        var violations = new List<string>();
        foreach (var type in grantModelTypes)
        {
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (prop.PropertyType == typeof(string) &&
                    (prop.Name.Contains("Predicate", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Contains("RowFilter", StringComparison.OrdinalIgnoreCase) ||
                     prop.Name.Contains("Sql", StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{type.Name}.{prop.Name}");
                }
            }
        }

        violations.ShouldBeEmpty(
            $"Free-SQL predicate string properties must not exist on grant or profile models: {string.Join(", ", violations)}");
    }

    [Fact]
    public void Row_scopes_built_only_by_RowScopeCompiler()
    {
        var allowedPrefixes = new[]
        {
            "src/Autheris.Application/Policy/RowScopes/RowScopeCompiler",
            "src/Autheris.Application/Policy/RowScopes/GrantRowScopeValidator",
            "src/Autheris.Application/Policy/RowScopes/RowPolicyMigrationConverter",
            "src/TrinoSqlEngine/",
            "src/Autheris.Domain/",
            "src/Autheris.Infrastructure/Persistence/"
        };

        string[] predicateConstructionMarkers =
        [
            "PolicyPredicate.Create",
            "PolicyPredicateRowPolicy",
            "InListExpression",
            "InSubqueryExpression",
            "ExistsExpression",
            "ValuesQueryBody",
            "BuildTyped(",
            "IVirtualFilterKeySetProvider",
            "StructuredFilterSqlBuilder",
            "TypedRowFilterBuilder"
        ];

        string[] rowScopeTokens =
        [
            "GrantRowScopeEntry",
            "WriteRule.RowFilter"
        ];

        var violations = new List<string>();
        foreach (var file in RepositoryScan.ProductionFiles)
        {
            if (allowedPrefixes.Any(prefix => file.RelativePath.StartsWith(prefix, StringComparison.Ordinal)))
            {
                continue;
            }

            bool touchesRowScopeProp = System.Text.RegularExpressions.Regex.IsMatch(
                file.Text,
                @"\.RowScope\b",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            bool touchesWriteRowFilter = file.Text.Contains("WriteRule", StringComparison.Ordinal) &&
                System.Text.RegularExpressions.Regex.IsMatch(file.Text, @"\.RowFilter\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
            bool touchesRowScope = touchesRowScopeProp || touchesWriteRowFilter || rowScopeTokens.Any(token => file.Text.Contains(token, StringComparison.Ordinal));
            bool buildsPredicate = predicateConstructionMarkers.Any(marker => file.Text.Contains(marker, StringComparison.Ordinal));
            if (touchesRowScope && buildsPredicate)
            {
                violations.Add(file.RelativePath);
            }
        }

        violations.ShouldBeEmpty(
            $"Row-scope predicates for consents and write grants may only be constructed in RowScopeCompiler: {string.Join(", ", violations)}");
    }
}
