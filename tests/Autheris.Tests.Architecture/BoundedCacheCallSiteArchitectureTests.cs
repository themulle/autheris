namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autheris.Application.State.Caching;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class BoundedCacheCallSiteArchitectureTests
{
    private const string AllowListRelativePath = "tests/Autheris.Tests.Architecture/Baselines/bounded-cache-getoraddasync-allowlist.txt";
    private const string BoundedCacheFullName = "Autheris.Application.State.Caching.BoundedCache`2";

    private static IEnumerable<Assembly> ProductionAssemblies() =>
    [
        typeof(Autheris.Domain.Common.TenantId).Assembly,
        typeof(BoundedCache<,>).Assembly,
        typeof(Autheris.Infrastructure.Persistence.GovernanceRepository).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly,
        typeof(Autheris.Extensions.Dbt.DbtWebhookReceiver).Assembly,
        typeof(Autheris.Modules.Abstractions.IAutherisModule).Assembly,
        typeof(Autheris.Modules.Lakehouse.LakehouseModule).Assembly,
        typeof(TrinoSqlEngine.FastSqlEngine).Assembly,
        typeof(Autheris.Api.Middleware.TrustedDocumentStore).Assembly
    ];

    private static List<string> FindBoundedCacheGetOrAddAsyncCallSites()
    {
        var callSites = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var assembly in ProductionAssemblies())
        {
            var module = CecilModuleCache.For(assembly);
            foreach (var type in AllTypes(module))
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    // Exclude BoundedCache's own internal overload forwarding.
                    if (string.Equals(type.FullName, BoundedCacheFullName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode.Code is not (Code.Call or Code.Callvirt))
                        {
                            continue;
                        }

                        if (instr.Operand is not MethodReference target)
                        {
                            continue;
                        }

                        if (!string.Equals(target.Name, nameof(BoundedCache<int, int>.GetOrAddAsync), StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var declaring = target.DeclaringType.GetElementType().FullName;
                        if (string.Equals(declaring, BoundedCacheFullName, StringComparison.Ordinal))
                        {
                            callSites.Add($"{type.FullName}.{method.Name}");
                        }
                    }
                }
            }
        }

        return callSites.ToList();
    }

    private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
    {
        var stack = new Stack<TypeDefinition>(module.Types);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            yield return next;
            foreach (var nested in next.NestedTypes)
            {
                stack.Push(nested);
            }
        }
    }

    private static Dictionary<string, string> ReadAllowList()
    {
        var path = Path.Combine(RepositoryScan.RepoRoot, AllowListRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).ShouldBeTrue($"Missing allow-list file: {AllowListRelativePath}");

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var parts = line.Split('|', 2);
            parts.Length.ShouldBe(2, $"Allow-list entry must include '| rationale': '{line}'");
            var callSite = parts[0].Trim();
            var rationale = parts[1].Trim();
            callSite.ShouldNotBeNullOrWhiteSpace();
            rationale.ShouldNotBeNullOrWhiteSpace($"Allow-list entry '{callSite}' must explain why its key covers caller and is pure.");
            map.Add(callSite, rationale);
        }

        return map;
    }

    [Fact]
    public void BoundedCache_GetOrAddAsync_call_sites_require_allowlist_entry_with_key_rationale()
    {
        var allowList = ReadAllowList();
        var actualCallSites = FindBoundedCacheGetOrAddAsyncCallSites();

        var unapproved = actualCallSites.Where(site => !allowList.ContainsKey(site)).ToList();
        unapproved.ShouldBeEmpty(
            $"Unapproved BoundedCache.GetOrAddAsync call sites found: {string.Join(", ", unapproved)}. " +
            $"Add an entry to {AllowListRelativePath} explaining why the key covers the caller and the factory is pure.");

        var stale = allowList.Keys.Where(site => !actualCallSites.Contains(site, StringComparer.Ordinal)).ToList();
        stale.ShouldBeEmpty($"Stale BoundedCache.GetOrAddAsync allow-list entries found: {string.Join(", ", stale)}");
    }
}
