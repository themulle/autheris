namespace Autheris.Application.Policy.Lint;

using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Command line front end of the lint. Exit codes: 0 clean, 1 unparsable predicates, 2 usage or configuration error.
/// The governance connection string is read from <see cref="ConnectionStringVariable"/> (a plain value or a <c>keyvault:&lt;ref&gt;</c>
/// reference) and never from the arguments, where it would be visible in the process list and in shell history (SEC-X1-16).
/// The output lists table and policy ids and finding codes only; it never contains predicate text or values.
/// </summary>
public static class PolicyLintCli
{
    public const string ConnectionStringVariable = "AUTHERIS_GOVERNANCE_CONNECTION";
    public const string KeyVaultPrefix = "keyvault:";

    public static async Task<int> RunAsync(
        string[] args,
        Func<string, CancellationToken, Task<PolicyLintInput>> loadInput,
        Func<string, string?> getEnvironmentVariable,
        TextWriter output,
        Func<string, string>? resolveKeyVaultReference = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(loadInput);
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(output);

        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            PrintUsage(output);
            return 0;
        }

        if (args.Length > 0)
        {
            // Unknown arguments are never echoed: one of them may be a connection string.
            output.WriteLine($"Error: arguments are not accepted. The governance connection string is read from {ConnectionStringVariable}.");
            PrintUsage(output);
            return 2;
        }

        var configured = getEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(configured))
        {
            output.WriteLine($"Error: set {ConnectionStringVariable} (a connection string or a {KeyVaultPrefix}<reference>).");
            return 2;
        }

        string connectionString = configured;
        if (configured.StartsWith(KeyVaultPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (resolveKeyVaultReference is null)
            {
                output.WriteLine("Error: Key Vault references are not available in this host.");
                return 2;
            }

            try
            {
                connectionString = resolveKeyVaultReference(configured[KeyVaultPrefix.Length..]);
            }
            catch (Exception)
            {
                output.WriteLine("Error: the Key Vault reference could not be resolved.");
                return 2;
            }
        }

        PolicyLintInput input;
        try
        {
            input = await loadInput(connectionString, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The message may echo the connection string.
            output.WriteLine($"Error: the governance store could not be read ({ex.GetType().Name}).");
            return 2;
        }

        var report = new PolicyPredicateLinter().Lint(input);
        foreach (var finding in report.Findings)
        {
            output.WriteLine($"UNPARSABLE kind={finding.Kind} table={finding.Table} policy={finding.PolicyId} code={finding.Code}");
        }

        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"checked={report.Checked} unparsable={report.Findings.Count} unchecked={report.Unchecked}"));
        return report.IsClean ? 0 : 1;
    }

    private static void PrintUsage(TextWriter output)
    {
        output.WriteLine("policy-predicate-lint: parses every stored row predicate with the production parser setup.");
        output.WriteLine($"Usage: {ConnectionStringVariable}=<governance connection string | keyvault:ref> policy-predicate-lint");
        output.WriteLine("Exit codes: 0 all predicates parse, 1 unparsable predicates (table and policy ids listed), 2 usage or configuration error.");
    }
}
