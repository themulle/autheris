using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Caching;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// #1063 §2: Structural call-graph ratchet enforcing that every governance write path (consents, grants,
/// owners, classifications, access profiles, visibility grants, domains, and access-policy settings)
/// passes through <see cref="IGovernanceWriteInvalidator"/> rather than ad-hoc epoch or catalog events.
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernanceWriteInvalidationArchitectureTests
{
    private const string InvalidatorInterface = "Autheris.Application.Governance.Caching.IGovernanceWriteInvalidator";
    private const string EpochServiceInterface = "Autheris.Application.Common.Interfaces.IEpochValidationService";
    private const string ChangeNotifierInterface = "Autheris.Application.Governance.Caching.IGovernanceChangeNotifier";

    private static readonly IReadOnlySet<string> WriteInvalidatorTargets = new HashSet<string>(StringComparer.Ordinal)
    {
        $"{InvalidatorInterface}::InvalidateTablesAsync",
        $"{InvalidatorInterface}::InvalidateWideAsync"
    };

    private static readonly string[] GovernanceWriteInterfaces =
    [
        "Autheris.Domain.Interfaces.IConsentRepository",
        "Autheris.Domain.Interfaces.IClassificationRepository",
        "Autheris.Domain.Interfaces.IAccessProfileRepository",
        "Autheris.Domain.Interfaces.ICatalogVisibilityGrantRepository",
        "Autheris.Domain.Interfaces.IGovernanceDomainRepository",
        "Autheris.Domain.Interfaces.IAccessPolicySettingsRepository"
    ];

    private static readonly string[] WriteMethodPrefixes =
    [
        "Create", "Upsert", "Update", "Replace", "Prune", "Delete", "Remove",
        "Revoke", "Assign", "Approve", "Renew", "Delegate", "Set", "Apply",
        "Activate", "Execute", "Record", "Bump", "Ensure"
    ];

    private static readonly IReadOnlySet<string> NonPolicyWriteAllowlist = new HashSet<string>(StringComparer.Ordinal)
    {
        // ConsentModels.cs / §2 explicit exemption: description updates never touch validity, rules, or policy epochs.
        "UpdateConsentDescriptionWithAuditAsync",
        // Pending request / rejection / reminder paths do not create or mutate active grants or policy state.
        "CreateConsentRequestAsync",
        "RejectConsentRequestAsync",
        "RejectExternalConsentRequestAsync"
    };

    private static readonly string[] RequiredGovernanceWriteMethods =
    [
        "Autheris.Infrastructure.Persistence.GovernanceRepository::CreateConsentAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::RevokeConsentAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::RevokeSystemConsentAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::RevokeSystemConsentByIdAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::ReplaceConsentsAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::ExecuteAtomicApprovalWithConsentAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::ActivateConsentAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::ActivateConsentForAutoApproveAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::RecordGrantExpiryAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::Autheris.Application.ConsentRequests.ProfileAssignments.IProfileAssignmentStore.InvalidateTablesAfterCommitAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::UpsertClassificationAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::UpsertClassificationMaskRuleAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::AssignColumnClassificationAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::RemoveColumnClassificationAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::UpsertColumnMaskOverrideAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::DeleteColumnMaskOverrideAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::UpsertProfileAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::DeleteProfileAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::AssignProfileAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::ApproveAssignmentAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::RenewAssignmentAsync",
        "Autheris.Infrastructure.Persistence.GovernanceRepository::RemoveAssignmentAsync",
        "Autheris.Infrastructure.Persistence.AccessProfileStore::UpsertProfileAsync",
        "Autheris.Infrastructure.Persistence.AccessProfileStore::DeleteProfileAsync",
        "Autheris.Infrastructure.Persistence.AccessProfileStore::AssignProfileAsync",
        "Autheris.Infrastructure.Persistence.AccessProfileStore::ApproveAssignmentAsync",
        "Autheris.Infrastructure.Persistence.AccessProfileStore::RenewAssignmentAsync",
        "Autheris.Infrastructure.Persistence.AccessProfileStore::RemoveAssignmentAsync",
        "Autheris.Infrastructure.Persistence.CatalogVisibilityGrantStore::CreateAsync",
        "Autheris.Infrastructure.Persistence.CatalogVisibilityGrantStore::RevokeAsync",
        "Autheris.Infrastructure.Persistence.CatalogVisibilityGrantStore::RevokeAllForSubjectAsync",
        "Autheris.Infrastructure.Persistence.CatalogGrantRevocationWriter::RevokeWithAuditAsync",
        "Autheris.Infrastructure.Persistence.GovernanceDomainStore::UpsertAsync",
        "Autheris.Infrastructure.Persistence.GovernanceDomainStore::DeleteAsync",
        "Autheris.Infrastructure.Persistence.GovernanceDomainStore::EnsureUnassignedAsync",
        "Autheris.Infrastructure.Persistence.GovernanceDomainStore::AssignTablesAsync",
        "Autheris.Infrastructure.Persistence.GovernanceDomainStore::SetProtectionAsync",
        "Autheris.Infrastructure.Persistence.GovernanceDomainStore::SetTableProtectionAsync",
        "Autheris.Infrastructure.Persistence.GovernanceDomainStore::BumpEpochAsync",
        "Autheris.Infrastructure.Persistence.AccessPolicySettingsStore::UpsertAsync",
        "Autheris.Infrastructure.Persistence.AccessPolicySettingsStore::DeleteIfVersionAsync",
        "Autheris.Application.Governance.Requests.PermissionRequestEngine::DecideAsync",
        "Autheris.Application.Governance.Requests.PermissionRequestEngine::ExecuteSelfGrantAsync",
        "Autheris.Application.Governance.Requests.PermissionRequestEngine::RenewGrantAsync",
        "Autheris.Application.Governance.Domains.GovernanceDomainService::CreateDomainAsync",
        "Autheris.Application.Governance.Domains.GovernanceDomainService::UpdateDomainAsync",
        "Autheris.Application.Governance.Domains.GovernanceDomainService::DeleteDomainAsync",
        "Autheris.Application.Governance.Domains.GovernanceDomainService::AssignOwnerGroupAsync",
        "Autheris.Application.Governance.Domains.GovernanceDomainService::AssignTablesAsync",
        "Autheris.Application.Governance.Domains.GovernanceDomainService::SetTableProtectionAsync",
        "Autheris.Application.Governance.Domains.GovernanceDomainService::SetColumnProtectionAsync",
        "Autheris.Application.Products.DataProductService::PutAsync",
        "Autheris.Application.Products.DataProductService::RetireAsync",
        "Autheris.Application.Products.DataProductService::SetOwnerGroupAsync",
        "Autheris.Application.Products.DataProductService::ClassifyColumnAsync"
    ];

    private static readonly IReadOnlySet<string> AllowedDirectSignalTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "Autheris.Application.Governance.Caching.GovernanceWriteInvalidator",
        "Autheris.Infrastructure.Cache.EpochValidationService",
        // Cache-side L1 floor eviction when ConsentCacheService clears cached entries (not a governance write).
        "Autheris.Infrastructure.Cache.ConsentCacheService",
        // Identity SCIM/deprovisioning events are identity-scoped (not consent/policy/classification table writes).
        "Autheris.Application.Identity.Services.IdentityDeprovisioningService",
        "Autheris.Application.Identity.Services.ScimService",
        "Autheris.Application.Identity.Services.ScimGroupMembershipValidator"
    };

    [Fact]
    public void AllGovernanceWritePaths_ReachWriteInvalidator()
    {
        var modules = ProductionAssemblies().Select(CecilModuleCache.For).ToList();
        var missing = FindMissingWriteInvalidatorPaths(modules, CollectTargetMethods(modules));
        missing.ShouldBeEmpty(
            "Governance write method(s) do not pass through IGovernanceWriteInvalidator (InvalidateTablesAsync / InvalidateWideAsync): "
            + string.Join(", ", missing));
    }

    [Fact]
    public void DirectEpochAndNotifierInvalidation_IsNotBypassedOutsideWriteInvalidator()
    {
        var modules = ProductionAssemblies().Select(CecilModuleCache.For).ToList();
        var bypasses = FindDirectSignalBypasses(modules);
        bypasses.ShouldBeEmpty(
            "Direct calls to IEpochValidationService invalidation or IGovernanceChangeNotifier.NotifyAsync bypass IGovernanceWriteInvalidator: "
            + string.Join(", ", bypasses));
    }

    [Fact]
    public void Scanner_FlagsSyntheticGovernanceWriteWithoutInvalidator()
    {
        using var selfModule = ModuleDefinition.ReadModule(typeof(GovernanceWriteInvalidationArchitectureTests).Assembly.Location);
        var syntheticFullName = typeof(SyntheticOffendingGovernanceStore).FullName!.Replace('+', '/');
        var syntheticType = AllTypes(selfModule.Types).First(t => t.FullName == syntheticFullName);
        var targets = syntheticType.Methods
            .Where(m => m.Name is nameof(SyntheticOffendingGovernanceStore.CreateGrantWithoutInvalidatorAsync)
                or nameof(SyntheticOffendingGovernanceStore.UpdateClassificationOnlyCatalogPublishAsync))
            .ToList();
        targets.Count.ShouldBe(2);

        var missing = FindMissingWriteInvalidatorPaths([selfModule], targets);
        missing.ShouldContain($"{syntheticFullName}::{nameof(SyntheticOffendingGovernanceStore.CreateGrantWithoutInvalidatorAsync)}");
        missing.ShouldContain($"{syntheticFullName}::{nameof(SyntheticOffendingGovernanceStore.UpdateClassificationOnlyCatalogPublishAsync)}");
    }

    private static List<MethodDefinition> CollectTargetMethods(IReadOnlyList<ModuleDefinition> modules)
    {
        var allTypes = modules.SelectMany(m => AllTypes(m.Types)).ToList();
        var byFullName = allTypes
            .SelectMany(t => t.Methods.Select(m => (Key: $"{t.FullName}::{m.Name}", Method: m)))
            .ToLookup(x => x.Key, x => x.Method, StringComparer.Ordinal);

        var targets = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
        foreach (var required in RequiredGovernanceWriteMethods)
        {
            var found = byFullName[required].FirstOrDefault();
            found.ShouldNotBeNull($"Expected governance write method '{required}' was not found.");
            targets[MethodKey(found)] = found;
        }

        // Auto-discover any new write method on persistence stores implementing core governance write interfaces.
        var storeImplNames = new[]
        {
            "Autheris.Infrastructure.Persistence.GovernanceRepository",
            "Autheris.Infrastructure.Persistence.CatalogVisibilityGrantStore",
            "Autheris.Infrastructure.Persistence.GovernanceDomainStore",
            "Autheris.Infrastructure.Persistence.AccessPolicySettingsStore"
        };

        var interfaceWriteNames = allTypes
            .Where(t => GovernanceWriteInterfaces.Contains(t.FullName, StringComparer.Ordinal))
            .SelectMany(t => t.Methods)
            .Select(m => m.Name)
            .Where(IsWriteMethodName)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var store in allTypes.Where(t => storeImplNames.Contains(t.FullName, StringComparer.Ordinal)))
        {
            foreach (var m in store.Methods.Where(m => m.IsPublic && m.HasBody))
            {
                if (!IsWriteMethodName(m.Name) || NonPolicyWriteAllowlist.Contains(m.Name))
                {
                    continue;
                }

                if (interfaceWriteNames.Contains(m.Name))
                {
                    targets[MethodKey(m)] = m;
                }
            }
        }

        return targets.Values.ToList();
    }

    private static bool IsWriteMethodName(string name) =>
        name.EndsWith("Async", StringComparison.Ordinal)
        && WriteMethodPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal));

    private static List<string> FindMissingWriteInvalidatorPaths(
        IReadOnlyList<ModuleDefinition> modules,
        IReadOnlyList<MethodDefinition> targets)
    {
        var methodIndex = modules
            .SelectMany(m => AllTypes(m.Types))
            .SelectMany(t => t.Methods)
            .Where(m => m.HasBody)
            .GroupBy(MethodKey, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var missing = new List<string>();
        foreach (var entry in targets)
        {
            if (!ReachesWriteInvalidator(entry, methodIndex))
            {
                missing.Add($"{entry.DeclaringType.FullName}::{entry.Name}");
            }
        }

        return missing.Order(StringComparer.Ordinal).ToList();
    }

    private static bool ReachesWriteInvalidator(
        MethodDefinition entry,
        IReadOnlyDictionary<string, MethodDefinition> methodIndex)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<MethodDefinition>();
        EnqueueMethodAndStateMachines(entry, visited, queue);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!current.HasBody)
            {
                continue;
            }

            foreach (var instr in current.Body.Instructions)
            {
                if (instr.Operand is not MethodReference targetRef)
                {
                    continue;
                }

                var declaredCall = $"{targetRef.DeclaringType.FullName}::{targetRef.Name}";
                if (WriteInvalidatorTargets.Contains(declaredCall))
                {
                    return true;
                }

                var resolved = TryResolve(targetRef);
                if (resolved is not null)
                {
                    EnqueueMethodAndStateMachines(resolved, visited, queue);
                }
                else if (methodIndex.TryGetValue(declaredCall, out var indexed))
                {
                    EnqueueMethodAndStateMachines(indexed, visited, queue);
                }
            }
        }

        return false;
    }

    private static List<string> FindDirectSignalBypasses(IReadOnlyList<ModuleDefinition> modules)
    {
        var bypasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in modules.SelectMany(m => AllTypes(m.Types)))
        {
            var logicalOwner = OuterTypeName(type);
            if (AllowedDirectSignalTypes.Contains(logicalOwner))
            {
                continue;
            }

            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is not MethodReference targetRef)
                    {
                        continue;
                    }

                    var targetCall = $"{targetRef.DeclaringType.FullName}::{targetRef.Name}";
                    if (targetCall is $"{EpochServiceInterface}::InvalidateEpochAsync"
                        or $"{EpochServiceInterface}::InvalidateAllTables"
                        or $"{ChangeNotifierInterface}::NotifyAsync")
                    {
                        bypasses.Add($"{logicalOwner}::{method.Name} -> {targetCall}");
                    }
                }
            }
        }

        return bypasses.Order(StringComparer.Ordinal).ToList();
    }

    private static void EnqueueMethodAndStateMachines(
        MethodDefinition method,
        HashSet<string> visited,
        Queue<MethodDefinition> queue)
    {
        if (visited.Add(MethodKey(method)))
        {
            queue.Enqueue(method);
        }

        if (!method.HasCustomAttributes)
        {
            return;
        }

        foreach (var attr in method.CustomAttributes)
        {
            if (attr.AttributeType.FullName is not (
                "System.Runtime.CompilerServices.AsyncStateMachineAttribute"
                or "System.Runtime.CompilerServices.IteratorStateMachineAttribute"))
            {
                continue;
            }

            if (attr.ConstructorArguments.Count > 0
                && attr.ConstructorArguments[0].Value is TypeReference smRef
                && TryResolveType(smRef) is { } smType)
            {
                foreach (var smMethod in smType.Methods.Where(m => m.HasBody))
                {
                    if (visited.Add(MethodKey(smMethod)))
                    {
                        queue.Enqueue(smMethod);
                    }
                }
            }
        }
    }

    private static string OuterTypeName(TypeDefinition type)
    {
        var current = type;
        while (current.IsNested && current.DeclaringType is not null)
        {
            current = current.DeclaringType;
        }

        return current.FullName;
    }

    private static string MethodKey(MethodDefinition method) =>
        $"{method.DeclaringType.FullName}::{method.Name}({method.Parameters.Count})";

    private static MethodDefinition? TryResolve(MethodReference method)
    {
        try
        {
            return method.Resolve();
        }
        catch
        {
            return null;
        }
    }

    private static TypeDefinition? TryResolveType(TypeReference type)
    {
        try
        {
            return type.Resolve();
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> roots)
    {
        foreach (var type in roots)
        {
            yield return type;
            foreach (var nested in AllTypes(type.NestedTypes))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<Assembly> ProductionAssemblies() =>
    [
        typeof(IGovernanceWriteInvalidator).Assembly,
        typeof(Autheris.Infrastructure.Persistence.GovernanceRepository).Assembly
    ];

    private sealed class SyntheticOffendingGovernanceStore(IGovernanceWriteInvalidator invalidator)
    {
        public async Task CreateGrantWithoutInvalidatorAsync(CancellationToken ct)
        {
            await Task.Yield();
            GC.KeepAlive(ct);
        }

        public async Task UpdateClassificationOnlyCatalogPublishAsync(CancellationToken ct)
        {
            await invalidator.PublishCatalogChangedAsync(new Autheris.Application.Catalog.CatalogVersion(1, 2), ct).ConfigureAwait(false);
        }
    }
}
