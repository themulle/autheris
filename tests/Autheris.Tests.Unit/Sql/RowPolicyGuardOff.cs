using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;

namespace Autheris.Tests.Unit.Sql;

/// <summary>
/// For a test of the function allowlist or the rewriting of web SQL through a tenant-bound table: switches the expression allowlist for
/// statements with a row policy off for the current async flow. The allowlist has its own tests in the engine project.
/// </summary>
internal static class RowPolicyGuardOff
{
    public static IDisposable Scope() => CompilerHooks.Use(new Hooks());

    private sealed class Hooks : ICompilerHooks
    {
        public bool IsDisabled(SecurityControl control) => control == SecurityControl.RowPolicyExpressionAllowlist;
        public void OnPass(string name) { }
        public SqlStatement InterceptInjected(SqlStatement secured) => secured;
        public string InterceptEmitted(string sql) => sql;
        public bool OmitsCacheKeyField(string name) => false;
        public DmlGuardOptions InterceptDmlGuards(DmlGuardOptions requested) => requested;
        public bool BypassesExecutionStackGuard() => false;
        public GovernedSqlException? LeakProviderError(string providerMessage, TargetSqlDialect dialect) => null;
    }
}
