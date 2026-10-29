using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Infrastructure.Persistence.Migrations.Shape;

namespace Autheris.Infrastructure.Persistence.Migrations;

/// <summary>
/// A governance migration step from version 2 on: embedded SQL (<c>Steps/&lt;engine&gt;/NNNN_name.sql</c>) plus its shape delta
/// (<c>NNNN_name.shape.json</c>), plan D-2. The text is normalised once (BOM stripped, CRLF to LF); the checksum covers exactly that
/// string and the batches executed are cut from the same string, so hash and execution cannot diverge (I388-SC-01).
/// Header lines: <c>-- guard: SELECT ...</c> (1 = already applied, statements skipped) and <c>-- phase3: ID</c>.
/// </summary>
internal sealed partial class SqlMigrationStep
{
    internal const string ResourcePrefix = "governance-step.";
    private const string BatchMarker = "-- batch";

    private SqlMigrationStep(
        string engine, int version, string name, string sql, string checksum, string? guardSql, string? phase3Marker,
        IReadOnlyList<string> batches, GovernanceSchemaShapeDelta shapeDelta)
    {
        Engine = engine;
        Version = version;
        Name = name;
        Sql = sql;
        Checksum = checksum;
        GuardSql = guardSql;
        Phase3Marker = phase3Marker;
        Batches = batches;
        ShapeDelta = shapeDelta;
    }

    public string Engine { get; }

    public int Version { get; }

    public string Name { get; }

    /// <summary>The normalised text that is hashed and executed.</summary>
    public string Sql { get; }

    public string Checksum { get; }

    public string? GuardSql { get; }

    public string? Phase3Marker { get; }

    public IReadOnlyList<string> Batches { get; }

    public GovernanceSchemaShapeDelta ShapeDelta { get; }

    /// <summary>BOM stripped, CRLF and lone CR to LF; nothing else (I388-SC-01).</summary>
    public static string Normalize(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        return raw.TrimStart('﻿').Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    /// <summary><c>SHA-256("&lt;engine&gt;:&lt;NNNN&gt;:&lt;name&gt;:" + normalised text)</c>, lower-case hex.</summary>
    public static string ComputeChecksum(string engine, int version, string name, string normalizedSql) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{engine}:{version:D4}:{name}:") + normalizedSql)));

    public static SqlMigrationStep Parse(string engine, int version, string name, string rawSql, string shapeDeltaJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(engine);
        ArgumentNullException.ThrowIfNull(rawSql);
        ArgumentNullException.ThrowIfNull(shapeDeltaJson);
        if (version < 2)
        {
            throw new InvalidOperationException("File-based governance migration steps start at version 2; the baseline is frozen (fail closed).");
        }

        var sql = Normalize(rawSql);
        string? guard = null;
        string? marker = null;
        foreach (var line in sql.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length == 0)
            {
                continue;
            }

            if (!t.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            var header = t[2..].TrimStart();
            if (header.StartsWith("guard:", StringComparison.OrdinalIgnoreCase))
            {
                guard = header["guard:".Length..].Trim();
            }
            else if (header.StartsWith("phase3:", StringComparison.OrdinalIgnoreCase))
            {
                marker = header["phase3:".Length..].Trim();
            }
        }

        var violations = StepSqlLint.Validate(sql, guard, marker);
        if (violations.Count > 0)
        {
            throw new InvalidOperationException(
                $"Governance migration {version:D4} '{name}' ({engine}) violates the step lint (fail closed): {string.Join("; ", violations)}.");
        }

        // The SQL Server batch split and trim is a deterministic function of the checksummed, normalised text, so the executed batches are covered by the checksum.
        var batches = engine == "sqlserver"
            ? sql.Split('\n').Aggregate(
                new List<StringBuilder> { new() },
                (list, line) =>
                {
                    if (line.Trim().Equals(BatchMarker, StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(new StringBuilder());
                    }
                    else
                    {
                        list[^1].AppendLine(line);
                    }

                    return list;
                }).Select(b => b.ToString().Trim()).Where(b => b.Length > 0).ToArray()
            : [sql];

        return new SqlMigrationStep(
            engine, version, name, sql, ComputeChecksum(engine, version, name, sql), guard, marker, batches,
            GovernanceSchemaShapeDelta.Parse(shapeDeltaJson, $"{version:D4}_{name}"));
    }

    /// <summary>Loads every step of an engine from the embedded resources (an empty list while no step beyond the baseline exists).</summary>
    public static IReadOnlyList<SqlMigrationStep> LoadEmbedded(string engine) => LoadEmbedded(engine, typeof(SqlMigrationStep).Assembly);

    internal static IReadOnlyList<SqlMigrationStep> LoadEmbedded(string engine, Assembly assembly)
    {
        var folder = ResourcePrefix + engine + "/";
        var resources = assembly.GetManifestResourceNames().Where(n => n.StartsWith(folder, StringComparison.Ordinal)).ToArray();
        return Load(
            engine,
            resources.Select(r => r[folder.Length..]).ToArray(),
            file =>
            {
                using var stream = assembly.GetManifestResourceStream(folder + file)
                    ?? throw new InvalidOperationException($"The governance migration resource '{file}' is missing (fail closed).");
                using var reader = new StreamReader(stream, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
                return reader.ReadToEnd();
            });
    }

    /// <summary>Builds steps from file names and a reader; used by the embedded loader and by tests.</summary>
    internal static IReadOnlyList<SqlMigrationStep> Load(string engine, IReadOnlyCollection<string> files, Func<string, string> read)
    {
        var sqlFiles = files.Where(f => f.EndsWith(".sql", StringComparison.Ordinal)).ToArray();
        var deltaFiles = files.Where(f => f.EndsWith(".shape.json", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        foreach (var other in files.Except(sqlFiles).Where(f => !deltaFiles.Contains(f)))
        {
            throw new InvalidOperationException($"Unexpected governance migration resource '{other}' (fail closed).");
        }

        var steps = new List<SqlMigrationStep>();
        foreach (var file in sqlFiles.Order(StringComparer.Ordinal))
        {
            var match = StepFileName().Match(file);
            if (!match.Success)
            {
                throw new InvalidOperationException($"The governance migration file '{file}' does not match NNNN_snake_name.sql (fail closed).");
            }

            var deltaFile = file[..^".sql".Length] + ".shape.json";
            if (!deltaFiles.Remove(deltaFile))
            {
                throw new InvalidOperationException($"The governance migration '{file}' has no shape delta '{deltaFile}' (fail closed).");
            }

            steps.Add(Parse(engine, int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), match.Groups[2].Value, read(file), read(deltaFile)));
        }

        if (deltaFiles.Count > 0)
        {
            throw new InvalidOperationException($"The shape delta '{deltaFiles.First()}' has no migration SQL (fail closed).");
        }

        return steps;
    }

    /// <summary>The runtime step: transactional, runs the guard, then the batches on the step transaction.</summary>
    public MigrationStep ToMigrationStep() =>
        new(Version, Name, Checksum, ApplyAsync, Transactional: true) { ShapeDelta = ShapeDelta };

    private async Task ApplyAsync(MigrationContext ctx)
    {
        if (GuardSql is not null)
        {
            await using var guard = ctx.Connection.CreateCommand();
            guard.Transaction = ctx.Transaction;
            guard.CommandText = GuardSql;
            var already = await guard.ExecuteScalarAsync(ctx.CancellationToken).ConfigureAwait(false);
            if (already is not null and not DBNull && Convert.ToInt64(already, CultureInfo.InvariantCulture) != 0)
            {
                return;
            }
        }

        foreach (var batch in Batches)
        {
            await using var cmd = ctx.Connection.CreateCommand();
            cmd.Transaction = ctx.Transaction;
            cmd.CommandText = batch;
            await cmd.ExecuteNonQueryAsync(ctx.CancellationToken).ConfigureAwait(false);
        }
    }

    [GeneratedRegex(@"^(\d{4})_([a-z0-9_]+)\.sql$", RegexOptions.CultureInvariant)]
    private static partial Regex StepFileName();
}
