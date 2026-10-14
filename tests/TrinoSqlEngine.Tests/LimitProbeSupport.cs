using TrinoSqlEngine.Ast.Capabilities;
using Xunit;

namespace TrinoSqlEngine.Tests;

/// <summary>
/// Tests that probe a limit by compiling a huge statement must fail because of that limit and nothing else. The default engine
/// has a 5 s parse budget (including the wait on the process-wide parser slot limiter) that a loaded machine can exhaust, so the
/// probes use an engine without a parse budget and with a parser limiter of their own, and the compile budget at its maximum.
/// </summary>
internal static class LimitProbeSupport
{
    /// <summary>An engine without a parse time budget and with its own parser limiter (no wait on the shared static one).</summary>
    public static FastSqlEngine Engine(int maxQueryLength = int.MaxValue) => new()
    {
        MaxQueryLength = maxQueryLength,
        ParseTimeout = TimeSpan.Zero,
        ParseConcurrencyLimiter = new SemaphoreSlim(1, 1)
    };

    /// <summary>
    /// A compiler over <paramref name="engine"/> whose capability table is the production one with <c>MaxBindParameters</c> lowered to
    /// <paramref name="maxBindParameters"/>: the boundary logic is exercised with a few dozen values instead of 65,535.
    /// </summary>
    internal static GovernedSqlCompiler WithBindLimit(FastSqlEngine engine, int maxBindParameters) =>
        new(engine, new ReducedBindLimit(maxBindParameters), new TrinoSqlEngine.Ast.Emit.CompiledSqlTemplateCache());

    private sealed class ReducedBindLimit(int maxBindParameters) : IDialectCapabilityProvider
    {
        public DialectCapabilities Get(TargetSqlDialect dialect) =>
            DialectCapabilityTable.Default.Get(dialect) with { MaxBindParameters = maxBindParameters };
    }
}

/// <summary>
/// The probes at the production limits (up to 65,535 bound values) are CPU heavy and are the ones a loaded machine can starve; they run
/// one at a time, never next to another class of the assembly.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProductionLimitProbes
{
    public const string Name = "ProductionLimitProbes";
}
