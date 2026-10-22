using Shouldly;
using Xunit;

namespace Autheris.Tests.Architecture;

/// <summary>VF-04: the cross-source row reader has no entry point that takes client text (a filter string would reach native SQL).</summary>
public sealed class CrossSourceRowReaderArchitectureTests
{
    [Fact]
    public void CrossSourceRowReader_HasNoClientTextEntryPoint()
    {
        var offenders = typeof(Autheris.Application.Data.Relations.ICrossSourceRowReader)
            .GetMethods()
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .Select(m => m.Name)
            .ToList();

        offenders.ShouldBeEmpty();
        typeof(Autheris.Application.Data.Relations.GovernedCrossSourceRowReader)
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
            .ShouldBeEmpty();
    }
}
