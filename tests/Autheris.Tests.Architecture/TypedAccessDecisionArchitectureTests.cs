using System.Reflection;
using Autheris.Domain.Interfaces;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>WP-X0e, SEC-ADG-25: the typed row policy is an opaque Domain abstraction; no text can become a policy.</summary>
public sealed class TypedAccessDecisionArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(IRowPolicy).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(Autheris.Application.Policy.PolicyPredicateRowPolicy).Assembly;

    [Fact]
    public void Domain_HasNoTrinoSqlEngineReference()
    {
        DomainAssembly.GetReferencedAssemblies().Select(a => a.Name).ShouldNotContain("TrinoSqlEngine");
    }

    [Fact]
    public void IRowPolicy_HasNoStaticFactoryAndNoDomainImplementation()
    {
        typeof(IRowPolicy).GetMethods(BindingFlags.Static | BindingFlags.Public).ShouldBeEmpty();
        DomainAssembly.GetTypes().Where(t => t is { IsClass: true } && typeof(IRowPolicy).IsAssignableFrom(t)).ShouldBeEmpty();
    }

    [Fact]
    public void RowPolicyImplementations_HaveNoPublicEntryFromText()
    {
        var implementations = ApplicationAssembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IRowPolicy).IsAssignableFrom(t))
            .ToList();
        implementations.ShouldNotBeEmpty();

        foreach (var type in implementations)
        {
            var entries = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Cast<MethodBase>()
                .Concat(type.GetMethods(BindingFlags.Public | BindingFlags.Static).Where(m => typeof(IRowPolicy).IsAssignableFrom(m.ReturnType)))
                .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
                .Select(m => $"{type.Name}.{m.Name}");
            entries.ShouldBeEmpty();
        }
    }

    [Fact]
    public void RowPolicyFactory_TextEntryOnlyThroughTheParser()
    {
        // The factory is the single place that turns admin text into a policy, and it does so only via IPolicyExpressionParser.
        var fields = typeof(Autheris.Application.Policy.RowPolicyFactory)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance).Select(f => f.FieldType.Name);
        fields.ShouldContain("IPolicyExpressionParser");
    }
}
