namespace Autheris.Tools.PolicyPredicateLint;

using Autheris.Application.Policy.Lint;
using Autheris.Infrastructure.Persistence;

internal static class Program
{
    private static Task<int> Main(string[] args) =>
        PolicyLintCli.RunAsync(
            args,
            SqliteGovernanceLintSource.LoadAsync,
            Environment.GetEnvironmentVariable,
            Console.Out);
}
