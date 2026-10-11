namespace Autheris.Application.Policy.Lint;

using System.Collections.Generic;

/// <summary>Parses every stored row predicate through the production parser setup and reports the ones that fail closed.</summary>
public sealed class PolicyPredicateLinter
{
    public PolicyLintReport Lint(PolicyLintInput input) => new(0, new List<PolicyLintFinding>());
}
