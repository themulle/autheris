using System.Text.Json;
using System.Text.RegularExpressions;

namespace Autheris.Tests.Parity.Infrastructure;

public enum ParityOutcome
{
    Pass,
    Fail,
    Gap,
    Pending,
}

/// <summary>One cell result; serialized as one JSON line (D-6). Never carries headers, bodies or data values (SEC-PAR-08).</summary>
public sealed record ParityResult(string CellId, string Block, string Channel, string Capability, string Dialect, ParityOutcome Outcome, string? GapId, string Seed, string? Observation = null);

public static partial class ParityEnvironment
{
    public const string SeedVar = "AUTHERIS_PARITY_SEED";
    public const string ReportDirVar = "AUTHERIS_PARITY_REPORT_DIR";
    public const string DefaultSeed = "20261023";

    public static string Seed => Environment.GetEnvironmentVariable(SeedVar) is { Length: > 0 } s ? s : DefaultSeed;

    public static string ReportDir => Environment.GetEnvironmentVariable(ReportDirVar) is { Length: > 0 } d ? d : Path.Combine("TestResults", "parity");
}

/// <summary>Appends one JSON line per cell to <c>parity-results.jsonl</c> under a lock (D-6).</summary>
public sealed partial class ParityResultsWriter(string directory)
{
    public const string FileName = "parity-results.jsonl";

    private static readonly object Gate = new();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]{1,200}$")]
    private static partial Regex CellIdPattern();

    public static ParityResultsWriter Default { get; } = new(ParityEnvironment.ReportDir);

    public string FilePath { get; } = Path.Combine(directory, FileName);

    public void Record(ParityResult result)
    {
        if (!CellIdPattern().IsMatch(result.CellId))
        {
            throw new ArgumentException($"Invalid cell id '{result.CellId}'.", nameof(result));
        }

        var line = JsonSerializer.Serialize(new
        {
            cellId = result.CellId,
            block = result.Block,
            channel = result.Channel,
            capability = result.Capability,
            dialect = result.Dialect,
            outcome = result.Outcome.ToString().ToLowerInvariant(),
            gapId = result.GapId,
            seed = result.Seed,
            observation = result.Observation,
        });
        lock (Gate)
        {
            Directory.CreateDirectory(directory);
            File.AppendAllText(FilePath, line + "\n");
        }
    }
}

public sealed record ParityCellInfo(string Block, string Channel, string Capability, string CaseId, string Dialect = "sqlite", string? GapId = null, ParityAspect Aspect = ParityAspect.None)
{
    public string CellId => Sanitize($"{Block}.{Channel}.{Capability}.{CaseId}");

    private static string Sanitize(string value)
    {
        var chars = value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '-' ? c : '_').Take(200).ToArray();
        return new string(chars);
    }
}

/// <summary>Runs one parity cell: applies the KnownGap rule and records exactly one result line (D-5, D-6).</summary>
public static class ParityCell
{
    public static async Task RunAsync(ParityCellInfo cell, Func<Task> body, ParityResultsWriter? writer = null)
    {
        writer ??= ParityResultsWriter.Default;
        var outcome = ParityOutcome.Pass;
        using var observations = ParityObservations.Begin();
        try
        {
            await KnownGap.RunAsync(cell.GapId, body, cell.Aspect);
        }
        catch (KnownGapObservedException)
        {
            outcome = ParityOutcome.Gap;
            throw;
        }
        catch (PendingCellException)
        {
            outcome = ParityOutcome.Pending;
            throw;
        }
        catch
        {
            outcome = ParityOutcome.Fail;
            throw;
        }
        finally
        {
            writer.Record(new ParityResult(cell.CellId, cell.Block, cell.Channel, cell.Capability, cell.Dialect, outcome, cell.GapId, ParityEnvironment.Seed, observations.Tolerated501));
        }
    }
}

/// <summary>
/// Observations made while a cell runs. A tolerated 501 (an unsupported capability answered with a stable code, D-9) is a 5xx and must stay
/// visible: the cell records it and the report lists it. The holder is created by <see cref="ParityCell"/> so callees can fill it.
/// </summary>
public sealed class ParityObservations : IDisposable
{
    private static readonly AsyncLocal<ParityObservations?> Current = new();

    /// <summary>Code of a tolerated 501 seen by the running cell, or null.</summary>
    public string? Tolerated501 { get; private set; }

    /// <summary>True while a cell holder is registered for the current async flow.</summary>
    public static bool IsActive => Current.Value is not null;

    public static ParityObservations Begin()
    {
        var holder = new ParityObservations();
        Current.Value = holder;
        return holder;
    }

    /// <summary>Clears the holder so a later cell on the same flow never writes into a finished cell's observations.</summary>
    public void Dispose()
    {
        if (ReferenceEquals(Current.Value, this))
        {
            Current.Value = null;
        }
    }

    public static void RecordTolerated501(string code)
    {
        if (Current.Value is { } holder)
        {
            holder.Tolerated501 = code;
        }
    }
}
