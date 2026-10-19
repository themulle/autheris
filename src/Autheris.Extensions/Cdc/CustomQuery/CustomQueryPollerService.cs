namespace Autheris.Extensions.Cdc.CustomQuery;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Hosting;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Governed multi-watermark query poller service running on distributed cluster leases (PLAN-CDC-101 D-5, D-6, WP-101-4).
/// Fetches batches under read-only transaction semantics, classifies operations, and appends atomically to ICdcStreamStore.
/// </summary>
public class CustomQueryPollerService : ClusterPartitionedService
{
    private readonly IOptionsMonitor<CustomQueryPollerOptions> _options;
    private readonly ICdcStreamStore _streamStore;
    private readonly CdcChangeEventBuilder _eventBuilder;
    private readonly ISqlConnectionFactory? _sqlConnectionFactory;
    private readonly IDataSourceConnectionFactory? _dataSourceConnectionFactory;
    private readonly ICdcStreamSignal _signal;
    private readonly ICdcStreamCrypto? _crypto;
    private readonly ICdcPayloadCodec? _codec;
    private readonly ILogger<CustomQueryPollerService> _logger;

    public CustomQueryPollerService(
        IOptionsMonitor<CustomQueryPollerOptions> options,
        ICdcStreamStore streamStore,
        CdcChangeEventBuilder eventBuilder,
        ISqlConnectionFactory sqlConnectionFactory,
        ICdcStreamSignal signal,
        IDistributedClusterStateProvider clusterState,
        ILogger<CustomQueryPollerService> logger,
        ICdcStreamCrypto? crypto = null,
        ICdcPayloadCodec? codec = null,
        IDataSourceConnectionFactory? dataSourceFactory = null,
        TimeProvider? timeProvider = null)
        : base(clusterState, logger, timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _streamStore = streamStore ?? throw new ArgumentNullException(nameof(streamStore));
        _eventBuilder = eventBuilder ?? throw new ArgumentNullException(nameof(eventBuilder));
        _sqlConnectionFactory = sqlConnectionFactory;
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _crypto = crypto;
        _codec = codec;
        _dataSourceConnectionFactory = dataSourceFactory;
    }

    public CustomQueryPollerService(
        IOptionsMonitor<CustomQueryPollerOptions> options,
        ICdcStreamStore streamStore,
        CdcChangeEventBuilder eventBuilder,
        IDataSourceConnectionFactory dataSourceConnectionFactory,
        ICdcStreamSignal signal,
        IDistributedClusterStateProvider clusterState,
        ILogger<CustomQueryPollerService> logger,
        ICdcStreamCrypto? crypto = null,
        ICdcPayloadCodec? codec = null,
        TimeProvider? timeProvider = null)
        : base(clusterState, logger, timeProvider)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _streamStore = streamStore ?? throw new ArgumentNullException(nameof(streamStore));
        _eventBuilder = eventBuilder ?? throw new ArgumentNullException(nameof(eventBuilder));
        _dataSourceConnectionFactory = dataSourceConnectionFactory ?? throw new ArgumentNullException(nameof(dataSourceConnectionFactory));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _crypto = crypto;
        _codec = codec;
    }

    protected override IReadOnlyList<ClusterPartition> GetPartitions()
    {
        var opt = _options.CurrentValue;
        if (!opt.Enabled) return [];

        return opt.Pollers.Select(p => new ClusterPartition(
            $"cdc:query:poll:{p.StreamName.ToLowerInvariant()}",
            new ClusterSchedule(
                true,
                TimeSpan.FromMilliseconds(Math.Max(250, p.PollingIntervalMilliseconds)),
                TimeSpan.Zero,
                TimeSpan.FromSeconds(Math.Max(15, (p.PollingIntervalMilliseconds * 3) / 1000)),
                TimeSpan.Zero)
        )).ToList();
    }

    protected override async Task ExecutePartitionAsync(ClusterPartition partition, ClusterJobContext context, CancellationToken ct)
    {
        var streamName = partition.Key.Replace("cdc:query:poll:", string.Empty, StringComparison.OrdinalIgnoreCase);
        var settings = _options.CurrentValue.Pollers.FirstOrDefault(p => p.StreamName.Equals(streamName, StringComparison.OrdinalIgnoreCase));
        if (settings is null) return;

        await PollStreamBatchAsync(settings, context.FencingToken, ct).ConfigureAwait(false);
    }

    public async Task<int> PollStreamBatchAsync(CustomQueryPollerStreamOptions poller, long fencingToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(poller);

        var tableId = TableIdentifier.Parse(poller.Table);
        var stream = await _streamStore.EnsureStreamAsync("query_poller", tableId, ct).ConfigureAwait(false);

        // 1. Acquire read-only database connection
        await using var conn = await OpenSourceConnectionAsync(poller, ct).ConfigureAwait(false);

        // 2. Load active watermark vector
        var vectorRecord = await _streamStore.GetWatermarkVectorAsync(stream.Id, ct).ConfigureAwait(false);
        var vector = vectorRecord?.Vector ?? MultiWatermarkVector.Empty;

        // 3. Prepare Command & Enforce Read-Only Isolation & Command Timeout (SEC-CDC101-03)
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = poller.QueryTemplate;
        var timeout = Math.Min(poller.QueryTimeoutSeconds > 0 ? poller.QueryTimeoutSeconds : 30, 60);
        cmd.CommandTimeout = timeout;

        await EnforceReadOnlySemanticsAsync(conn, cmd, ct).ConfigureAwait(false);

        // 4. Strictly bind parameters (Zero dynamic SQL concatenation)
        QueryPollerParameterBinder.BindParameters(cmd, poller.Watermarks, vector, poller.BatchSize);

        // 5. Stream read batch with SequentialAccess
        var rawRows = new List<Dictionary<string, object?>>();
        await using (var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }
                rawRows.Add(row);
            }
        }

        if (rawRows.Count == 0)
        {
            await _streamStore.TouchPolledAsync(stream.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
            return 0;
        }

        // 6. Process rows: classification, image comparison, deduplication, and vector advancement
        var (sourceRows, nextVector) = await ProcessBatchRowsAsync(stream, poller, rawRows, vector, ct).ConfigureAwait(false);
        if (sourceRows.Count == 0)
        {
            // All rows were duplicates; if the vector advanced, we can still persist the advanced vector
            return 0;
        }

        // 7. Envelope encrypt event batch using CdcChangeEventBuilder
        var tableSettings = new CdcTableSettings(
            tableId,
            poller.DataSource,
            TimeSpan.FromMilliseconds(poller.PollingIntervalMilliseconds),
            poller.BatchSize,
            poller.QueryTimeoutSeconds,
            AllowNonSnapshotRead: false,
            CdcOnWatermarkExpired.Restart);

        var columns = rawRows[0].Keys.ToList();
        var activation = new CdcActivation(
            tableSettings,
            stream,
            poller.KeyColumns,
            columns,
            poller.TenantColumn,
            poller.OwnerTenantId,
            new DataSourceConnectionOptions { ConnectionString = string.Empty });

        var built = await _eventBuilder.BuildChangesAsync(activation, stream.HeadOffset, sourceRows, ct).ConfigureAwait(false);

        // 8. Commit atomic append batch with vector state and fencing token (SEC-CDC101-04)
        var appendBatch = new CdcAppendBatch(
            stream.Id,
            "query_poller",
            tableId,
            fencingToken,
            stream.HeadOffset,
            NewWatermark: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            built.Events,
            built.ImageWrites,
            WatermarkState: nextVector.ToJson());

        await _streamStore.AppendAsync(appendBatch, ct).ConfigureAwait(false);

        // 9. Signal downstream subscribers
        await _signal.NotifyAdvancedAsync(stream.Id, built.NewHead, ct).ConfigureAwait(false);
        return built.Events.Count;
    }

    private async Task<(List<CdcSourceRow> Rows, MultiWatermarkVector NextVector)> ProcessBatchRowsAsync(
        CdcStreamDescriptor stream,
        CustomQueryPollerStreamOptions poller,
        List<Dictionary<string, object?>> rawRows,
        MultiWatermarkVector currentVector,
        CancellationToken ct)
    {
        var keyColumns = poller.KeyColumns;
        var sourceRows = new List<CdcSourceRow>(rawRows.Count);
        var candidateVector = currentVector;

        // Load prior images if crypto and store are available
        Dictionary<string, IReadOnlyDictionary<string, object?>?> priorImages = new(StringComparer.Ordinal);
        if (_crypto != null && _codec != null)
        {
            var fps = new List<byte[]>();
            var fpKeys = new List<string>();
            foreach (var r in rawRows)
            {
                var kv = ExtractKeyValues(r, keyColumns);
                var fp = await _crypto.FingerprintAsync(stream.Id, kv, ct).ConfigureAwait(false);
                fps.Add(fp);
                fpKeys.Add(Convert.ToHexString(fp));
            }

            var storedImages = await _streamStore.GetRowImagesAsync(stream.Id, fps, ct).ConfigureAwait(false);
            foreach (var img in storedImages)
            {
                var plaintext = await _crypto.UnprotectImageAsync(stream.Id, img, ct).ConfigureAwait(false);
                var deserialized = _codec.DeserializeImage(plaintext);
                priorImages[Convert.ToHexString(img.KeyFingerprint)] = deserialized;
            }
        }

        for (var i = 0; i < rawRows.Count; i++)
        {
            var row = rawRows[i];
            var keyValues = ExtractKeyValues(row, keyColumns);
            var keyDict = ExtractKeyDictionary(row, keyColumns);

            IReadOnlyDictionary<string, object?>? prior = null;
            if (_crypto != null)
            {
                var hexFp = Convert.ToHexString(await _crypto.FingerprintAsync(stream.Id, keyValues, ct).ConfigureAwait(false));
                priorImages.TryGetValue(hexFp, out prior);
            }

            var operation = CdcOperationClassifier.ClassifyRow(row, poller.SoftDelete, prior, poller.Watermarks);

            // Deduplication: if payload is identical to prior image and not a delete, suppress
            if (operation != CdcOperation.Delete && prior != null && CdcOperationClassifier.IsPayloadIdentical(row, prior))
            {
                // Advance vector monotonically even on suppressed row
                candidateVector = AdvanceVectorFor(candidateVector, poller.Watermarks, row);
                continue;
            }

            candidateVector = AdvanceVectorFor(candidateVector, poller.Watermarks, row);

            var sourceRow = new CdcSourceRow(
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                operation,
                keyValues,
                keyDict,
                operation == CdcOperation.Delete ? null : row);

            sourceRows.Add(sourceRow);
        }

        return (sourceRows, candidateVector);
    }

    private static MultiWatermarkVector AdvanceVectorFor(
        MultiWatermarkVector vector,
        IReadOnlyList<WatermarkDimensionDefinition> dimensions,
        IReadOnlyDictionary<string, object?> row)
    {
        var updated = vector;
        foreach (var dim in dimensions)
        {
            var colName = string.IsNullOrWhiteSpace(dim.Column) ? dim.Name : dim.Column;
            if (row.TryGetValue(colName, out var val) && val is not null and not DBNull)
            {
                var strVal = FormatDimensionValue(val, dim.Type);
                if (!string.IsNullOrWhiteSpace(strVal))
                {
                    updated = updated.Advance(dim.Name, strVal, dim.Type);
                }
            }
        }
        return updated;
    }

    private static string FormatDimensionValue(object val, WatermarkDimensionType type)
    {
        if (val is DateTimeOffset dto) return dto.ToString("O", CultureInfo.InvariantCulture);
        if (val is DateTime dt) return new DateTimeOffset(dt).ToString("O", CultureInfo.InvariantCulture);
        return val.ToString() ?? string.Empty;
    }

    private static List<object?> ExtractKeyValues(IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> keyColumns)
    {
        var list = new List<object?>(keyColumns.Count);
        foreach (var pk in keyColumns)
        {
            row.TryGetValue(pk, out var val);
            list.Add(val);
        }
        return list;
    }

    private static Dictionary<string, object?> ExtractKeyDictionary(IReadOnlyDictionary<string, object?> row, IReadOnlyList<string> keyColumns)
    {
        var dict = new Dictionary<string, object?>(keyColumns.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var pk in keyColumns)
        {
            row.TryGetValue(pk, out var val);
            dict[pk] = val;
        }
        return dict;
    }

    private async Task<DbConnection> OpenSourceConnectionAsync(CustomQueryPollerStreamOptions poller, CancellationToken ct)
    {
        if (_dataSourceConnectionFactory != null)
        {
            return await _dataSourceConnectionFactory.CreateConnectionAsync(poller.DataSource ?? string.Empty, ct).ConfigureAwait(false);
        }

        if (_sqlConnectionFactory != null)
        {
            return await _sqlConnectionFactory.CreateOpenConnectionAsync(
                new DataSourceConnectionOptions { ConnectionString = poller.DataSource ?? string.Empty }, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("No database connection factory is configured for CustomQueryPollerService.");
    }

    private static async Task EnforceReadOnlySemanticsAsync(DbConnection conn, DbCommand cmd, CancellationToken ct)
    {
        var typeName = conn.GetType().Name.ToLowerInvariant();
        var connStr = conn.ConnectionString?.ToLowerInvariant() ?? string.Empty;
        var ds = conn.DataSource?.ToLowerInvariant() ?? string.Empty;

        if (typeName.Contains("sqlite") || connStr.Contains("sqlite") || ds.Contains("sqlite"))
        {
            using var pragma = conn.CreateCommand();
            pragma.CommandText = "PRAGMA query_only = ON;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        else if (typeName.Contains("npgsql") || typeName.Contains("postgres") || connStr.Contains("postgres") || ds.Contains("postgres"))
        {
            using var setRo = conn.CreateCommand();
            setRo.CommandText = "SET TRANSACTION READ ONLY;";
            try
            {
                await setRo.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Ignore if not in an active transaction block
            }
        }
    }
}
