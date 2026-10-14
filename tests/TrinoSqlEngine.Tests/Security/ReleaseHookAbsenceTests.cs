using Xunit;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// ARC-S-06: Debug and Release compile the same security code. The shipped assembly contains no test seam type and no
/// <see cref="ICompilerHooks"/> implementation other than the no-op, so no production code path can switch a control off. The
/// mutant implementation lives in the test assembly only. Runs in both configurations.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "SqlGate")]
public class ReleaseHookAbsenceTests
{
    [Fact]
    public void ProductionAssembly_HasNoCompilerTestSeamsType()
    {
        var assembly = typeof(FastSqlEngine).Assembly;

        Assert.Null(assembly.GetType("TrinoSqlEngine.CompilerTestSeams"));
    }

    [Fact]
    public void ProductionAssembly_ContainsOnlyTheNoOpHooksImplementation()
    {
        var implementations = typeof(FastSqlEngine).Assembly.GetTypes()
            .Where(t => !t.IsInterface && typeof(ICompilerHooks).IsAssignableFrom(t))
            .ToList();

        var only = Assert.Single(implementations);
        Assert.Equal(typeof(NoOpCompilerHooks), only);
    }

    [Fact]
    public void WithoutInstalledHooks_AllControlsAreEnabled()
    {
        Assert.Same(NoOpCompilerHooks.Instance, CompilerHooks.Current);
        foreach (var control in Enum.GetValues<SecurityControl>())
        {
            Assert.False(CompilerHooks.IsDisabled(control), control.ToString());
        }
    }

    [Fact]
    public void DisposingTheScope_RestoresTheNoOpHooks()
    {
        using (CompilerHooks.Use(new MutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.CoverageVerifier } }))
        {
            Assert.True(CompilerHooks.IsDisabled(SecurityControl.CoverageVerifier));
        }

        Assert.False(CompilerHooks.IsDisabled(SecurityControl.CoverageVerifier));
        Assert.Same(NoOpCompilerHooks.Instance, CompilerHooks.Current);
    }
}
