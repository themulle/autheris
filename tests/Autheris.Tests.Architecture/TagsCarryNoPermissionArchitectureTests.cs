using System.Text.RegularExpressions;
using Autheris.Application.Analytics;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed partial class TagsCarryNoPermissionArchitectureTests
{
    private static readonly string[] AuthzFiles =
    [
        "src/Autheris.Domain/Plugins/Auth/PluginAuthorizationContext.cs",
        "src/Autheris.Application/Governance/Contracts/SchemaContractDefinition.cs",
        "src/Autheris.Domain/Options/Tenancy/MultiTenancyOptions.cs",
        "src/Autheris.Application/Policy/TableAccessPolicy.cs",
        "src/Autheris.Application/Policy/Pdp/AccessDecisionPoint.cs",
        "src/Autheris.Application/Services/CatalogVisibility.cs",
        "src/Autheris.Application/Catalog/Services/CatalogVisibilityService.cs",
        "src/Autheris.Application/Analytics/GovernedAggregationService.GroupSize.cs"
    ];

    private static readonly Regex ForbiddenTagIdentifiers = new(
        @"\b(TableTags|TableSensitivity|CatalogTags|IncludedTags|ExcludedTags|IsPiiTag|Tags)\b",
        RegexOptions.CultureInvariant);

    [Fact]
    public void No_tag_read_in_authz_paths()
    {
        foreach (var path in AuthzFiles)
        {
            var code = string.Join('\n', RepositoryScan.CodeLines(RepositoryScan.ReadText(path)));
            var match = ForbiddenTagIdentifiers.Match(code);
            match.Success.ShouldBeFalse($"Authorization path '{path}' must not read or branch on tags (found '{match.Value}').");
        }

        var module = CecilModuleCache.For(typeof(TableAccessPolicy).Assembly.Location);
        string[] guardedTypes =
        [
            typeof(TableAccessPolicy).FullName!,
            typeof(AccessDecisionPoint).FullName!,
            typeof(CatalogVisibility).FullName!,
            typeof(GovernedAggregationService).FullName!
        ];

        var tagGetters = new List<string>();
        foreach (var typeName in guardedTypes)
        {
            var type = module.GetType(typeName);
            type.ShouldNotBeNull($"Expected type {typeName} in application assembly.");

            foreach (var targetType in new[] { type }.Concat(type.NestedTypes))
            {
                foreach (var method in targetType.Methods.Where(m => m.HasBody))
                {
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode.Code is Code.Call or Code.Callvirt &&
                            instr.Operand is MethodReference mr &&
                            (mr.Name == "get_Tags" || mr.Name == "get_CatalogTags" || mr.Name == "get_TableTags") &&
                            mr.DeclaringType.Namespace.StartsWith("Autheris.", StringComparison.Ordinal))
                        {
                            tagGetters.Add($"{targetType.FullName}::{method.Name} -> {mr.DeclaringType.Name}.{mr.Name}");
                        }
                    }
                }
            }
        }

        tagGetters.ShouldBeEmpty("Authz paths must not invoke tag getters via IL: " + string.Join(", ", tagGetters));
    }
}
