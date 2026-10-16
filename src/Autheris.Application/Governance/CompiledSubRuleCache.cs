namespace Autheris.Application.Governance;

using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Autheris.Domain.Model;
using DynamicExpresso;

/// <summary>
/// WP-PF-03: compiles each Casbin <c>sub_rule</c> once into a typed delegate instead of building an interpreter and running a
/// regex on every evaluation. Compile failures are stored as <c>null</c> and surface as an "unknown" result, which callers treat
/// fail-closed (an allow rule does not match, a deny rule matches). The interpreter is sandboxed: assignment is disabled and no
/// additional types or references are registered beyond the defaults.
/// </summary>
internal static class CompiledSubRuleCache
{
    /// <summary>The Casbin request shape (<c>r.sub</c>, <c>r.tenant</c>, <c>r.obj</c>, <c>r.act</c>, <c>r.ctx</c>).</summary>
    internal sealed class SubRuleRequest
    {
        public SecurityEvaluationContext ctx { get; init; } = default!;

        public string sub { get; init; } = string.Empty;

        public string tenant { get; init; } = string.Empty;

        public string obj { get; init; } = string.Empty;

        public string act { get; init; } = string.Empty;
    }

    private delegate bool SubRuleDelegate(SubRuleRequest r, SecurityEvaluationContext ctx, SecurityEvaluationContext context);

    // Upper bound that keeps adversarial or very large policy sets from growing the cache without limit. Beyond it a rule is
    // compiled per call, which is slower but equivalent (eviction only produces misses).
    internal const int MaxEntries = 20_000;

    private static readonly Regex QuoteNormalizer = new(@"'([^']{2,})'", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, SubRuleDelegate?> Compiled = new(StringComparer.Ordinal);

    internal static int Count => Compiled.Count;

    internal static void Clear() => Compiled.Clear();

    /// <summary>Returns <c>null</c> when the expression cannot be compiled or evaluated.</summary>
    internal static bool? Evaluate(string subRule, SecurityEvaluationContext context, string subject, string tableStr, string act)
    {
        if (!Compiled.TryGetValue(subRule, out var del))
        {
            del = Compile(subRule);
            if (Compiled.Count < MaxEntries)
            {
                Compiled.TryAdd(subRule, del);
            }
        }

        if (del is null)
        {
            return null;
        }

        try
        {
            return del(
                new SubRuleRequest { ctx = context, sub = subject, tenant = context.Tenant.Value, obj = tableStr, act = act },
                context,
                context);
        }
        catch
        {
            return null;
        }
    }

    private static SubRuleDelegate? Compile(string subRule)
    {
        try
        {
            var interpreter = new Interpreter();
            interpreter.EnableAssignment(AssignmentOperators.None);
            var normalized = QuoteNormalizer.Replace(subRule, "\"$1\"");
            return interpreter.ParseAsDelegate<SubRuleDelegate>(normalized, "r", "ctx", "context");
        }
        catch
        {
            return null;
        }
    }
}
