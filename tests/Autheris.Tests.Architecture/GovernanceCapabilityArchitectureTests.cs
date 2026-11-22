namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Security;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class GovernanceCapabilityArchitectureTests
{
    // Classification & policy write endpoint files awaiting future W1-06 / W1-09 migration to IGovernanceCapabilities (shrink-only).
    private static readonly HashSet<string> LegacyWriterAllowlist = new(StringComparer.Ordinal)
    {
        "src/Autheris.Api/Endpoints/CatalogApiEndpoints.cs",
        "src/Autheris.Api/Endpoints/ClassificationAdminEndpoints.cs",
        "src/Autheris.Api/Endpoints/ConsentApiEndpoints.Grants.cs",
        "src/Autheris.Api/Endpoints/GovernanceClassificationEndpoints.cs",
        "src/Autheris.Api/Endpoints/GovernanceEndpoints.Access.cs",
        "src/Autheris.Api/Endpoints/GovernanceEndpoints.Catalog.cs",
        "src/Autheris.Api/Endpoints/GovernanceEndpoints.cs",
        "src/Autheris.Api/Endpoints/ProductEndpoints.Handlers.cs",
        "src/Autheris.Api/Endpoints/VirtualFilterEndpoints.Crud.cs",
        "src/Autheris.Api/Endpoints/VirtualFilterEndpoints.cs"
    };

    private static readonly Regex ClassificationOrPolicyWritePattern = new(
        @"\b(AssignColumnClassificationAsync|RemoveColumnClassificationAsync|UpsertMaskOverrideAsync|DeleteMaskOverrideAsync|UpsertClassificationAsync|UpsertMaskRuleAsync|ClassifyTableAsync|ClassifyColumnAsync|SaveFilterAsync|ApproveFilterAsync|DeleteFilterAsync|SaveProfileAsync|ApproveProfileAsync|DeleteProfileAsync|ApplyManifestAsync|CreateConsentAsync|RevokeConsentAsync|UpdateDataOwnerAsync|SetDatasetRequiredFiltersHandler|UpsertWriteRule)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void Classification_and_policy_writers_use_capabilities()
    {
        typeof(IGovernanceCapabilities).IsAssignableFrom(typeof(GovernanceCapabilities)).ShouldBeTrue();
        typeof(GovernanceCapabilities).IsSealed.ShouldBeTrue();

        var diRegistrationFiles = RepositoryScan.ProductionFiles
            .Where(f => f.RelativePath.StartsWith("src/Autheris.Api/Extensions/DependencyInjection/GatewayGovernanceServiceExtensions", StringComparison.Ordinal))
            .ToList();
        diRegistrationFiles.Any(f => f.Text.Contains("IGovernanceCapabilities", StringComparison.Ordinal) &&
                                     f.Text.Contains("GovernanceCapabilities", StringComparison.Ordinal))
            .ShouldBeTrue("IGovernanceCapabilities must be registered in GatewayGovernanceServiceExtensions.");

        // 1. IL verification: ClassificationAdminEndpoints.Columns.cs must authorize via IGovernanceCapabilities.CanClassifyAsync.
        var apiAssembly = typeof(Autheris.Api.Endpoints.ClassificationAdminEndpoints).Assembly;
        var module = CecilModuleCache.For(apiAssembly.Location);
        var endpointsType = module.GetTypes().Single(t => t.FullName == "Autheris.Api.Endpoints.ClassificationAdminEndpoints");

        var nestedAndSelfMethods = CollectAllTypes(endpointsType)
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .ToList();

        var canClassifyCallers = nestedAndSelfMethods
            .Where(m => m.Body.Instructions.Any(i =>
                i.Operand is MethodReference mr &&
                mr.DeclaringType.FullName == "Autheris.Application.Common.Interfaces.IGovernanceCapabilities" &&
                mr.Name == nameof(IGovernanceCapabilities.CanClassifyAsync)))
            .ToList();

        canClassifyCallers.ShouldNotBeEmpty("ClassificationAdminEndpoints.Columns.cs must check IGovernanceCapabilities.CanClassifyAsync.");

        var columnsFileText = RepositoryScan.ReadText("src/Autheris.Api/Endpoints/ClassificationAdminEndpoints.Columns.cs");
        columnsFileText.ShouldContain("IGovernanceCapabilities");
        columnsFileText.ShouldContain("CanClassifyAsync");

        var columnRouteMethods = nestedAndSelfMethods
            .Where(m => m.Body.Instructions.Any(i =>
                i.Operand is MethodReference mr &&
                (mr.Name == "AssignColumnClassificationAsync" ||
                 mr.Name == "RemoveColumnClassificationAsync" ||
                 mr.Name == "UpsertMaskOverrideAsync" ||
                 mr.Name == "DeleteMaskOverrideAsync")))
            .ToList();
        columnRouteMethods.Count.ShouldBe(4, "Expected all 4 column classification/mask-override endpoints in ClassificationAdminEndpoints.Columns.cs.");
        foreach (var routeMethod in columnRouteMethods)
        {
            var callsCapabilityOrHelper = routeMethod.Body.Instructions.Any(i =>
                i.Operand is MethodReference mr &&
                ((mr.DeclaringType.FullName == "Autheris.Application.Common.Interfaces.IGovernanceCapabilities" &&
                  mr.Name == nameof(IGovernanceCapabilities.CanClassifyAsync)) ||
                 (mr.DeclaringType.FullName == "Autheris.Api.Endpoints.ClassificationAdminEndpoints" &&
                  mr.Name == "AuthorizeColumnWriteAsync")));
            callsCapabilityOrHelper.ShouldBeTrue(
                $"Column endpoint '{routeMethod.Name}' must authorize through IGovernanceCapabilities.CanClassifyAsync.");
        }

        // 2. Endpoint file ratchet: new or migrated classification/policy writers must use IGovernanceCapabilities instead of ad-hoc role checks.
        var endpointFiles = RepositoryScan.ProductionFiles
            .Where(f => f.RelativePath.StartsWith("src/Autheris.Api/Endpoints/", StringComparison.Ordinal))
            .ToList();

        var unallowlistedViolations = new List<string>();
        foreach (var file in endpointFiles)
        {
            if (!ClassificationOrPolicyWritePattern.IsMatch(file.Text))
            {
                continue;
            }

            if (file.Text.Contains("IGovernanceCapabilities", StringComparison.Ordinal))
            {
                continue;
            }

            if (!LegacyWriterAllowlist.Contains(file.RelativePath))
            {
                unallowlistedViolations.Add(file.RelativePath);
            }
        }

        unallowlistedViolations.ShouldBeEmpty(
            "Classification and policy write endpoints must authorize via IGovernanceCapabilities: " +
            string.Join(", ", unallowlistedViolations));

        var missingFromEndpoints = LegacyWriterAllowlist
            .Where(path => endpointFiles.All(f => !string.Equals(f.RelativePath, path, StringComparison.Ordinal)))
            .ToList();
        missingFromEndpoints.ShouldBeEmpty(
            "Shrink LegacyWriterAllowlist when legacy endpoint files are removed or migrated: " +
            string.Join(", ", missingFromEndpoints));
    }

    private static IEnumerable<TypeDefinition> CollectAllTypes(TypeDefinition root)
    {
        yield return root;
        foreach (var nested in root.NestedTypes)
        {
            foreach (var child in CollectAllTypes(nested))
            {
                yield return child;
            }
        }
    }
}
