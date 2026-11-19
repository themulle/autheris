namespace Autheris.Tests.Integration.Interop.Trino;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

/// <summary>Runs an external client program (Java or Python) against the interop gateway and captures its output.</summary>
internal static class TrinoInteropProcess
{
    public const string JdbcJarVariable = "AUTHERIS_TRINO_JDBC_JAR";
    public const string PythonVariable = "AUTHERIS_TRINO_PYTHON";
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    public static string Require(string variable)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
        {
            throw new InvalidOperationException(
                $"Environment variable {variable} must point to an existing file (the trino-interop-nightly workflow sets it); interop tests only run on request.");
        }

        return value;
    }

    public static string WriteScript(string name, string content)
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris-trino-interop");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    public static async Task<(int ExitCode, string Output, string Error)> RunAsync(
        string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in environment)
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{fileName} did not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var cts = new System.Threading.CancellationTokenSource(Timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{fileName} did not finish within {Timeout}.");
        }

        return (process.ExitCode, await output, await error);
    }
}
