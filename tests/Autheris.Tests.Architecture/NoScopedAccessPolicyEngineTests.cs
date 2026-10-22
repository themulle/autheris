using System.Reflection;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>
/// EP-11: Verifies that no ScopedAccessPolicy types exist in Application or Api assemblies.
/// </summary>
public sealed class NoScopedAccessPolicyEngineTests
{
    [Fact]
    public void NoTypeNamed_ScopedAccessPolicy_Exists()
    {
        var assemblies = new[]
        {
            typeof(Autheris.Application.Services.ConsentResolutionService).Assembly,
            Assembly.Load("Autheris.Api")
        };

        foreach (var assembly in assemblies)
        {
            var matchingTypes = assembly.GetTypes()
                .Where(t => t.Name.Contains("ScopedAccessPolicy", StringComparison.OrdinalIgnoreCase))
                .Select(t => t.FullName)
                .ToList();

            matchingTypes.ShouldBeEmpty($"Assembly {assembly.GetName().Name} should not contain any ScopedAccessPolicy types.");
        }
    }
}
