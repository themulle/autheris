using Xunit;

namespace TrinoSqlEngine.Tests.Security;

/// <summary>
/// Plan M-3: the mutant hooks are "compiled out of Release builds". In a Release build this test proves it by reflection: neither the
/// seam type nor the control switches exist in the production assembly, so production code can neither reference nor enable them.
/// In a Debug build it proves the opposite, so that a Debug-only suite cannot silently run against an assembly without hooks.
/// </summary>
[Trait("Category", "Security")]
[Trait("Category", "SqlGate")]
public class ReleaseHookAbsenceTests
{
    [Fact]
    public void MutantHooks_ExistOnlyInDebugBuilds()
    {
        var assembly = typeof(FastSqlEngine).Assembly;
        bool seam = assembly.GetType("TrinoSqlEngine.CompilerTestSeams") is not null;
        bool controls = assembly.GetType("TrinoSqlEngine.SecurityControl") is not null;
#if DEBUG
        Assert.True(seam && controls);
#else
        Assert.False(seam, "CompilerTestSeams must not exist in a Release build.");
        Assert.False(controls, "SecurityControl must not exist in a Release build.");
#endif
    }
}
