using Autheris.Api.Endpoints;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Protection;
using Autheris.Infrastructure.Persistence;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed class ProtectionStoreAccessArchitectureTests
{
    private const string DomainRepoInterface = "Autheris.Application.Common.Interfaces.IGovernanceDomainRepository";
    private const string GetProtectionMethod = "GetProtectionAsync";

    private static readonly HashSet<string> AllowedGetProtectionCallers = new(StringComparer.Ordinal)
    {
        typeof(ProtectionSnapshotProvider).FullName!,
        typeof(GovernanceDomainService).FullName!,
        typeof(GovernanceDomainStore).FullName!
    };

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        foreach (var t in module.GetTypes())
        {
            yield return t;
            foreach (var nested in CollectNested(t))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<TypeDefinition> CollectNested(TypeDefinition root)
    {
        foreach (var n in root.NestedTypes)
        {
            yield return n;
            foreach (var child in CollectNested(n))
            {
                yield return child;
            }
        }
    }

    private static string OwningTopLevelType(TypeDefinition type)
    {
        var cur = type;
        while (cur.DeclaringType is not null)
        {
            cur = cur.DeclaringType;
        }

        return cur.FullName;
    }

    [Fact]
    public void Object_protection_read_only_via_provider()
    {
        var assemblies = new[]
        {
            typeof(GovernanceDomainService).Assembly,
            typeof(GovernanceDomainStore).Assembly,
            typeof(GovernanceDomainEndpoints).Assembly,
            typeof(Autheris.GraphQL.Types.Mutation).Assembly,
            typeof(Autheris.Extensions.ExtensionsServiceCollectionExtensions).Assembly
        };

        var directCallers = new List<string>();
        foreach (var asm in assemblies.DistinctBy(a => a.Location))
        {
            var module = CecilModuleCache.For(asm.Location);
            foreach (var type in AllTypes(module))
            {
                var owner = OwningTopLevelType(type);
                if (AllowedGetProtectionCallers.Contains(owner))
                {
                    continue;
                }

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode.Code is Code.Call or Code.Callvirt or Code.Ldftn &&
                            instr.Operand is MethodReference mr &&
                            mr.Name == GetProtectionMethod &&
                            (mr.DeclaringType.FullName == DomainRepoInterface ||
                             mr.DeclaringType.FullName == typeof(GovernanceDomainStore).FullName))
                        {
                            directCallers.Add($"{owner}::{method.Name}");
                        }
                    }
                }
            }
        }

        directCallers.ShouldBeEmpty(
            "Object protection reads must go through IProtectionSnapshotProvider rather than calling GetProtectionAsync directly: " +
            string.Join(", ", directCallers));

        var rawTableMentions = RepositoryScan.ProductionFiles
            .Where(f => !f.RelativePath.Contains("/Persistence/Migrations/", StringComparison.Ordinal))
            .Where(f => !f.RelativePath.EndsWith("/GovernanceDomainStore.cs", StringComparison.Ordinal))
            .Where(f => !f.RelativePath.EndsWith("/GovernanceTable.cs", StringComparison.Ordinal))
            .Where(f => !f.RelativePath.EndsWith("/AccessPolicySettingsProvider.cs", StringComparison.Ordinal))
            .Where(f => string.Join('\n', RepositoryScan.CodeLines(f.Text)).Contains("OBJECT_PROTECTION", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath)
            .ToList();

        rawTableMentions.ShouldBeEmpty(
            "Only GovernanceDomainStore may query OBJECT_PROTECTION directly: " + string.Join(", ", rawTableMentions));
    }
}
