using System.Reflection;
using Autheris.Api.Hosting.Warmup;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// PLAN-OPS-150 WP-B (SEC-OPS150-07): the warm-up code (framework types and every <see cref="IStartupWarmup"/> implementation) may
/// not depend on audit, consent, connector, repository (governance write), rate-limit or HTTP-client services.
/// </summary>
[Trait("Category", "Security")]
public sealed class StartupWarmupArchitectureTests
{
    private static readonly string[] Forbidden =
    [
        "Audit", "Consent", "Connector", "RateLimiter", "IHttpClientFactory", "HttpClient", "GovernanceRepository", "Casbin",
    ];

    private static IEnumerable<Type> WarmupTypes()
    {
        var assembly = typeof(IStartupWarmup).Assembly;
        return assembly.GetTypes().Where(t =>
            t.Namespace == typeof(IStartupWarmup).Namespace
            || (typeof(IStartupWarmup).IsAssignableFrom(t) && !t.IsInterface));
    }

    private static IEnumerable<Type> Flatten(Type type)
    {
        yield return type;
        if (type.IsGenericType)
        {
            foreach (var arg in type.GetGenericArguments().SelectMany(Flatten))
            {
                yield return arg;
            }
        }
    }

    [Fact]
    public void WarmupRegistrations_DependOnNoAuditConsentConnectorOrGovernanceWrite()
    {
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        var violations = new List<string>();

        foreach (var type in WarmupTypes())
        {
            var dependencies = type.GetConstructors(all).SelectMany(c => c.GetParameters().Select(p => p.ParameterType))
                .Concat(type.GetFields(all).Select(f => f.FieldType))
                .SelectMany(Flatten);

            foreach (var dependency in dependencies)
            {
                var name = dependency.FullName ?? dependency.Name;
                if (Forbidden.Any(f => name.Contains(f, StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"{type.Name} -> {name}");
                }
            }
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void WarmupFramework_TypesAreDiscovered()
    {
        var names = WarmupTypes().Select(t => t.Name).ToList();
        names.ShouldContain(nameof(StartupWarmupService));
        names.ShouldContain(nameof(GraphQlExecutorGate));
    }
}
