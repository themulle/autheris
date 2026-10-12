using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autheris.Application.Interfaces;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

public sealed class AuditArchitectureTests
{
    private static readonly Assembly[] AutherisAssemblies =
    [
        typeof(IAuditLogRepository).Assembly,
        typeof(Autheris.Extensions.Lakehouse.Services.IcebergRestCatalogFederationService).Assembly,
        typeof(Autheris.GraphQL.Subscriptions.CdcSubscriptionGovernor).Assembly
    ];

    [Fact]
    public void Constructors_MustNotHaveOptionalOrNullableAuditRepository()
    {
        var nullabilityContext = new NullabilityInfoContext();
        var violations = new List<string>();

        foreach (var assembly in AutherisAssemblies)
        {
            foreach (var type in assembly.GetTypes().Where(t => !t.IsAbstract && !t.IsInterface))
            {
                foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    foreach (var param in ctor.GetParameters())
                    {
                        if (param.ParameterType == typeof(IAuditLogRepository))
                        {
                            var nullability = nullabilityContext.Create(param);
                            if (nullability.ReadState == NullabilityState.Nullable || param.HasDefaultValue)
                            {
                                violations.Add($"{type.FullName}.{ctor.Name}({param.Name}) is nullable or has default value");
                            }
                        }
                    }
                }
            }
        }

        violations.ShouldBeEmpty(
            $"L-5 Violation: Found classes with optional/nullable IAuditLogRepository:\n{string.Join("\n", violations)}");
    }
}
