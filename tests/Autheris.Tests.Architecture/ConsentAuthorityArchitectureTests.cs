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
        typeof(IDataOwnershipRepository).GetMethods().Any(m => m.Name == "GetTransitiveRolesAsync").ShouldBeFalse();
        typeof(GovernanceRepository).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
            .Any(m => m.Name == "GetTransitiveRolesAsync").ShouldBeFalse();
    }
}
