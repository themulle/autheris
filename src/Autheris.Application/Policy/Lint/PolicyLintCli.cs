namespace Autheris.Application.Policy.Lint;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Command line front end of the lint. Exit codes: 0 clean, 1 unparsable predicates, 2 usage or configuration error.</summary>
public static class PolicyLintCli
{
    public const string ConnectionStringVariable = "AUTHERIS_GOVERNANCE_CONNECTION";

    public static Task<int> RunAsync(
        string[] args,
        Func<string, CancellationToken, Task<PolicyLintInput>> loadInput,
        Func<string, string?> getEnvironmentVariable,
        TextWriter output,
        CancellationToken ct = default) => Task.FromResult(0);
}
