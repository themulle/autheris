using System.Reflection;
using System.Text.Json.Serialization;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Identity.Attributes;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

[Trait("Category", "Security")]
public sealed class ConsentAuthorityArchitectureTests
{
    [Fact]
    public void Authority_NotConstructibleOutsideFactory()
    {
        var type = typeof(ConsentRevocationAuthority);
        type.IsSealed.ShouldBeTrue();

        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        ctors.ShouldNotBeEmpty();
        ctors.ShouldAllBe(c => c.IsPrivate);

        foreach (var ctor in ctors)
        {
            ctor.GetCustomAttribute<JsonConstructorAttribute>().ShouldBeNull();
        }

        var publicSetters = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.SetMethod is { IsPublic: true });
        publicSetters.ShouldBeEmpty();

        var factory = type.GetMethod("FromPrincipal", BindingFlags.Public | BindingFlags.Static);
        factory.ShouldNotBeNull();
    }

    [Fact]
    public void ConsentRevokeMethods_HaveNoDefaultAuthorityParameter()
    {
        Type[] typesToCheck =
        [
            typeof(IConsentRepository),
            typeof(IConsentDescriptionRepository),
            typeof(AttributeValidatingConsentRepository),
            typeof(GovernanceRepository)
        ];

        foreach (var t in typesToCheck)
        {
            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.Name is nameof(IConsentRepository.RevokeConsentAsync) or nameof(IConsentRepository.ReplaceConsentsAsync))
                .ToList();

            methods.ShouldNotBeEmpty(t.Name);
            foreach (var method in methods)
            {
                var authParam = method.GetParameters().SingleOrDefault(p => p.ParameterType == typeof(ConsentRevocationAuthority));
                authParam.ShouldNotBeNull($"{t.Name}.{method.Name} must accept ConsentRevocationAuthority");
                authParam!.HasDefaultValue.ShouldBeFalse($"{t.Name}.{method.Name} must not provide a default ConsentRevocationAuthority");
            }
        }
    }

    [Fact]
    public void GetTransitiveRolesAsync_IsRemoved()
    {
        typeof(Autheris.Application.Governance.Ownership.IContainerOwnership).GetMethods().Any(m => m.Name == "GetTransitiveRolesAsync").ShouldBeFalse();
        typeof(GovernanceRepository).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => m.Name == "GetTransitiveRolesAsync").ShouldBeFalse();
    }

    [Fact]
    public void Ownership_IsResolvedFromContainersOnly_NoPerTableOwnerSeamRemains()
    {
        // #1009 W3-12b (D13): the per-table ownership model is gone; IContainerOwnership is the only owner authority.
        var ownerSeams = new[] { "IDataOwnershipRepository", "IDataOwnerAssignmentRepository", "IOwnerGrantStore", "IOwnerGrantService" };
        typeof(IGovernanceRepository).GetInterfaces().Select(i => i.Name).ShouldNotContain(n => ownerSeams.Contains(n));
        typeof(GovernanceRepository).GetInterfaces().Select(i => i.Name).ShouldNotContain(n => ownerSeams.Contains(n));

        var legacyOwnerMethods = new[]
        {
            "GetDataOwnersForTableAsync", "GetDataOwnersForTablesAsync", "IsAuthorizedApproverForTableAsync", "GetOwnedOrDelegatedTablesAsync",
            "GetAuthorizedApproverSidsForTableAsync", "IsAnyApproverAuthorizedForTableAsync", "DelegateDataOwnershipAsync", "ApplyTableOwnerAssignmentsAsync"
        };
        typeof(GovernanceRepository).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Select(m => m.Name).Where(legacyOwnerMethods.Contains).ShouldBeEmpty("the governance store no longer answers ownership; container ownership does");
    }

    [Fact]
    public void OwnerGrantSource_IsRemoved()
    {
        Enum.GetNames<Autheris.Domain.Model.ConsentGrantSource>().ShouldNotContain("Owner");
        Enum.GetNames<Autheris.Domain.Interfaces.GrantSourceKind>().ShouldNotContain("Owner");
        Autheris.Domain.Model.ConsentWorkflowSqlMapping.TryParseGrantSource("OWNER", out _).ShouldBeFalse();
    }
}
