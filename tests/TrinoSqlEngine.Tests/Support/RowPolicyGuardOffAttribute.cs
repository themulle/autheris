using System.Reflection;
using Xunit.Sdk;

namespace TrinoSqlEngine.Tests;

/// <summary>
/// For test classes that check SQL emission or parsing through a tenant-bound request: the expression allowlist for statements with a
/// row policy or tenant predicate is switched off (a test mutant control, installed through the test-only hooks) for each test of the
/// class. The allowlist itself is covered by <c>RowPolicyExpressionAllowlistTests</c> and the mutant gate.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class RowPolicyGuardOffAttribute : BeforeAfterTestAttribute
{
    private static readonly AsyncLocal<IDisposable?> Scope = new();

    public override void Before(MethodInfo methodUnderTest)
    {
        Scope.Value = CompilerHooks.Use(new MutantHooks { DisabledControls = new HashSet<SecurityControl> { SecurityControl.RowPolicyExpressionAllowlist } });
    }

    public override void After(MethodInfo methodUnderTest)
    {
        Scope.Value?.Dispose();
        Scope.Value = null;
    }
}
