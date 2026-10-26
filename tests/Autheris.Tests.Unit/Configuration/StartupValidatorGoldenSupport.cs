namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autheris.Domain.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;

/// <summary>
/// Shared harness of the startup validator goldens (ARCH-MNT-14): a case is a configuration (bound like production binds
/// <c>Gateway</c>), an environment name and optional environment variables. The outcome of a case is the exception type and
/// message, or <c>OK</c>. The goldens are generated on the unchanged validator and must never change in a refactor.
/// </summary>
internal static class StartupValidatorGoldenSupport
{
    internal delegate void ValidateAction(GatewayOptions options, IHostEnvironment environment, Func<string, string?> getEnv, ILogger logger);

    internal sealed record GoldenCase(string Name, string Environment, IReadOnlyDictionary<string, string?> Config, IReadOnlyDictionary<string, string?>? EnvVars = null, Action<GatewayOptions>? Mutate = null);

    private const string TmpToken = "{tmp}";

    /// <summary>A Production configuration that passes the validator; every failure case is this base plus one deviation.</summary>
    internal static IReadOnlyDictionary<string, string?> ProductionBase { get; } = new Dictionary<string, string?>
    {
        ["DataMasking:HmacSecretKeyVaultRef"] = "kv://masking-hmac",
        ["Audit:ChainAnchorPath"] = TmpToken + "/anchor",
    };

    internal static string CreateFixtureDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "autheris-startup-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "model-empty.conf"), string.Empty);
        File.WriteAllText(Path.Combine(dir, "model-invalid.conf"), "this is not a casbin model\n");
        File.WriteAllText(Path.Combine(dir, "policy-empty.csv"), string.Empty);
        File.WriteAllText(Path.Combine(dir, "policy-norules.csv"), "# only a comment\n");
        File.WriteAllText(Path.Combine(dir, "policy-invalid.csv"), "p, only-one-field\n");
        return dir;
    }

    internal static GoldenCase Prod(string name, params (string Key, string? Value)[] overrides) =>
        Case(name, "Production", ProductionBase, null, overrides);

    internal static GoldenCase ProdEnv(string name, (string Key, string Value)[] envVars, params (string Key, string? Value)[] overrides) =>
        Case(name, "Production", ProductionBase, envVars, overrides);

    internal static GoldenCase Dev(string name, params (string Key, string? Value)[] overrides) =>
        Case(name, "Development", new Dictionary<string, string?>(), null, overrides);

    internal static GoldenCase DevEnv(string name, (string Key, string Value)[] envVars, params (string Key, string? Value)[] overrides) =>
        Case(name, "Development", new Dictionary<string, string?>(), envVars, overrides);

    /// <summary>A case whose options are changed after binding (reflection reaches init-only members that no configuration key can set).</summary>
    internal static GoldenCase ProdMutated(string name, Action<GatewayOptions> mutate) =>
        Case(name, "Production", ProductionBase, null, []) with { Mutate = mutate };

    private static GoldenCase Case(
        string name,
        string environment,
        IReadOnlyDictionary<string, string?> baseConfig,
        (string Key, string Value)[]? envVars,
        (string Key, string? Value)[] overrides)
    {
        var config = new Dictionary<string, string?>(baseConfig);
        foreach (var (key, value) in overrides)
        {
            config[key] = value;
        }

        return new GoldenCase(
            name,
            environment,
            config,
            envVars?.ToDictionary(e => e.Key, e => (string?)e.Value));
    }

    internal static GatewayOptions Bind(IReadOnlyDictionary<string, string?> config, string fixtureDir)
    {
        var values = config.ToDictionary(
            kv => "Gateway:" + kv.Key,
            kv => kv.Value?.Replace(TmpToken, fixtureDir, StringComparison.Ordinal));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return configuration.GetSection("Gateway").Get<GatewayOptions>() ?? new GatewayOptions();
    }

    internal static IHostEnvironment HostEnvironment(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    /// <summary>The outcome of one case: one line, <c>name [environment]: OK</c> or the exception type and message.</summary>
    internal static string RunFailureOutcome(GoldenCase golden, ValidateAction validate, string fixtureDir)
    {
        var (outcome, _, _) = Execute(golden, validate, fixtureDir);
        return $"{golden.Name} [{golden.Environment}]: {outcome}";
    }

    /// <summary>The outcome plus everything logged or written to the console while the validator ran.</summary>
    internal static string RunWarningOutcome(GoldenCase golden, ValidateAction validate, string fixtureDir)
    {
        var (outcome, logs, console) = Execute(golden, validate, fixtureDir);
        var text = new StringBuilder();
        text.Append("## ").Append(golden.Name).Append(" [").Append(golden.Environment).Append("]\n");
        text.Append("outcome: ").Append(outcome).Append('\n');
        foreach (var log in logs)
        {
            text.Append("log: ").Append(Escape(log, fixtureDir)).Append('\n');
        }

        foreach (var line in console.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            text.Append("console: ").Append(Escape(line, fixtureDir)).Append('\n');
        }

        return text.ToString();
    }

    private static (string Outcome, List<string> Logs, string Console) Execute(GoldenCase golden, ValidateAction validate, string fixtureDir)
    {
        var options = Bind(golden.Config, fixtureDir);
        golden.Mutate?.Invoke(options);
        var logger = new RecordingLogger();
        var vars = golden.EnvVars;
        Func<string, string?> getEnv = key => vars is not null && vars.TryGetValue(key, out var value) ? value : null;

        var originalOut = Console.Out;
        using var captured = new StringWriter();
        string outcome;
        try
        {
            Console.SetOut(captured);
            validate(options, HostEnvironment(golden.Environment), getEnv, logger);
            outcome = "OK";
        }
        catch (Exception ex)
        {
            outcome = $"{ex.GetType().Name}: {Escape(ex.Message, fixtureDir)}";
        }
        finally
        {
            Console.SetOut(originalOut);
        }

        return (outcome, logger.Lines, captured.ToString());
    }

    private static string Escape(string text, string fixtureDir) =>
        text.Replace(fixtureDir, "<tmp>", StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add($"{logLevel}: {formatter(state, exception)}" + (exception is null ? string.Empty : $" [{exception.GetType().Name}]"));
    }
}
