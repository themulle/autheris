namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;

/// <summary>
/// The effective producer settings of one tracked table: <c>MssqlChangeTracking</c> defaults overridden by the table's entry in
/// <c>Tables</c> (PLAN-CDC-99 section 3.4). Pure value; resolved from live options on every use.
/// </summary>
public sealed record CdcTableSettings(
    TableIdentifier Table,
    string? DataSource,
    TimeSpan PollInterval,
    int BatchSize,
    int QueryTimeoutSeconds,
    bool AllowNonSnapshotRead,
    CdcOnWatermarkExpired OnWatermarkExpired)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Cluster lease key of the table's poll loop (lower-case, D-A2).</summary>
    public string PartitionKey => $"cdc:mssql:poll:{Table.Domain}.{Table.Schema}.{Table.TableName}".ToLowerInvariant();

    /// <summary><c>max(15 s, 3 x interval)</c> (D-A2).</summary>
    public TimeSpan LeaseTtl => TimeSpan.FromSeconds(Math.Max(15, 3 * PollInterval.TotalSeconds));

    /// <summary>Merges <c>TrackedTables</c> and <c>Tables</c> (<c>Tables</c> wins per table, case-insensitive) into one list.</summary>
    public static IReadOnlyList<CdcTableSettings> Resolve(MssqlChangeTrackingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var result = new Dictionary<string, CdcTableSettings>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in ParseTrackedTables(options.TrackedTables))
        {
            result[Key(table)] = Build(options, table, null);
        }

        foreach (var entry in options.Tables)
        {
            var parsed = ParseTrackedTables([entry.Table]);
            if (parsed.Count == 1)
            {
                result[Key(parsed[0])] = Build(options, parsed[0], entry);
            }
        }

        return result.Values.ToList();
    }

    /// <summary>The settings of one table, or null when it is not tracked.</summary>
    public static CdcTableSettings? Find(MssqlChangeTrackingOptions options, TableIdentifier table) =>
        Resolve(options).FirstOrDefault(s => string.Equals(Key(s.Table), Key(table), StringComparison.OrdinalIgnoreCase));

    private static string Key(TableIdentifier t) => $"{t.Domain}.{t.Schema}.{t.TableName}";

    private static CdcTableSettings Build(MssqlChangeTrackingOptions o, TableIdentifier table, CdcTrackedTableOptions? t)
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(MinInterval.TotalMilliseconds, t?.PollingIntervalMilliseconds ?? o.PollingIntervalMilliseconds));
        var rawBatch = t?.BatchSize ?? o.BatchSize;
        var batch = Math.Clamp(rawBatch <= 0 ? 500 : rawBatch, 1, 5000);
        var timeout = Math.Clamp(t?.QueryTimeoutSeconds ?? o.QueryTimeoutSeconds, 1, 300);
        return new CdcTableSettings(
            table,
            string.IsNullOrWhiteSpace(t?.DataSource) ? null : t!.DataSource,
            interval,
            batch,
            timeout,
            o.AllowNonSnapshotRead,
            t?.OnWatermarkExpired ?? o.OnWatermarkExpired);
    }

    /// <summary>Parses <c>domain.schema.table</c> (shorter forms default the schema to <c>dbo</c> and the domain to <c>default</c>); blank entries are skipped.</summary>
    public static List<TableIdentifier> ParseTrackedTables(IEnumerable<string>? tableStrings)
    {
        var list = new List<TableIdentifier>();
        if (tableStrings == null)
        {
            return list;
        }

        foreach (var str in tableStrings)
        {
            if (string.IsNullOrWhiteSpace(str))
            {
                continue;
            }

            var parts = str.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            switch (parts.Length)
            {
                case 3:
                    list.Add(new TableIdentifier(parts[0], parts[1], parts[2]));
                    break;
                case 2:
                    list.Add(new TableIdentifier(parts[0], "dbo", parts[1]));
                    break;
                case 1:
                    list.Add(new TableIdentifier("default", "dbo", parts[0]));
                    break;
            }
        }

        return list;
    }
}
