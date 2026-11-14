namespace Autheris.Extensions.Cdc.CustomQuery;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Runtime.Hosting;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Validation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Governed multi-watermark query poller service running on distributed cluster leases.
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
    private readonly IOptionsMonitor<GatewayOptions>? _gatewayOptions;
    private readonly CustomQueryPollerValidator _validator;
    private readonly ITableMetadataRepository? _tableMetadataRepository;
    private readonly Lock _stateLock = new();
    private readonly Dictionary<string, CustomQueryPollerStreamOptions> _activePollers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _validatedTemplateHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly IDisposable? _optionsChangeRegistration;
    private bool _enabled;

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
        TimeProvider? timeProvider = null,
        IOptionsMonitor<GatewayOptions>? gatewayOptions = null,
        CustomQueryPollerValidator? validator = null,
        ITableMetadataRepository? tableMetadataRepository = null)
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
        _gatewayOptions = gatewayOptions;
        _validator = validator ?? new CustomQueryPollerValidator();
        _tableMetadataRepository = tableMetadataRepository;

        ReconcileOptionsSnapshot(_options.CurrentValue, invalidateCache: true);
        _optionsChangeRegistration = _options.OnChange(next => OnOptionsReloaded(next));
    }

    [ActivatorUtilitiesConstructor]
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
        TimeProvider? timeProvider = null,
        IOptionsMonitor<GatewayOptions>? gatewayOptions = null,
        CustomQueryPollerValidator? validator = null,
        ITableMetadataRepository? tableMetadataRepository = null)
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
        _gatewayOptions = gatewayOptions;
        _validator = validator ?? new CustomQueryPollerValidator();
        _tableMetadataRepository = tableMetadataRepository;

        ReconcileOptionsSnapshot(_options.CurrentValue, invalidateCache: true);
        _optionsChangeRegistration = _options.OnChange(next => OnOptionsReloaded(next));
    }

    private void OnOptionsReloaded(CustomQueryPollerOptions nextOptions)
    {
        if (nextOptions is null)
        {
            return;
        }

        ReconcileOptionsSnapshot(nextOptions, invalidateCache: true);
    }

    private void ReconcileOptionsSnapshot(CustomQueryPollerOptions opt, bool invalidateCache)
    {
        lock (_stateLock)
        {
            if (invalidateCache)
            {
                _validatedTemplateHashes.Clear();
            }

            _enabled = opt.Enabled;
            var nextActive = new Dictionary<string, CustomQueryPollerStreamOptions>(StringComparer.OrdinalIgnoreCase);

            foreach (var poller in opt.Pollers)
            {
                if (poller is null || string.IsNullOrWhiteSpace(poller.StreamName))
                {
                    continue;
                }

                var validationResult = _validator.ValidatePollerOptions(poller);
                var dialectOk = _gatewayOptions is null || TryResolveDialect(poller, out _);
                if (!validationResult.IsValid || !dialectOk)
                {
                    _logger.LogError(
                        "Rejected invalid custom query poller configuration for stream '{StreamName}'. Keeping prior valid definition if present.",
                        poller.StreamName);

                    if (_activePollers.TryGetValue(poller.StreamName, out var previousValid))
                    {
                        nextActive[poller.StreamName] = previousValid;
                    }

                    continue;
                }

                var templateHash = CustomQueryPollerValidator.ComputeTemplateHash(poller);
                nextActive[poller.StreamName] = poller;
                _validatedTemplateHashes[poller.StreamName] = templateHash;
            }

            _activePollers.Clear();
            foreach (var kv in nextActive)
            {
                _activePollers[kv.Key] = kv.Value;
            }
        }
    }

    protected override IReadOnlyList<ClusterPartition> GetPartitions()
    {
        lock (_stateLock)
        {
            if (!_enabled) return [];

            return _activePollers.Values.Select(p => new ClusterPartition(
                $"cdc:query:poll:{p.StreamName.ToLowerInvariant()}",
                new ClusterSchedule(
                    true,
                    TimeSpan.FromMilliseconds(Math.Max(250, p.PollingIntervalMilliseconds)),
                    TimeSpan.Zero,
                    TimeSpan.FromSeconds(Math.Max(15, (p.PollingIntervalMilliseconds * 3) / 1000)),
                    TimeSpan.Zero)
            )).ToList();
        }
    }

    protected override async Task ExecutePartitionAsync(ClusterPartition partition, ClusterJobContext context, CancellationToken ct)
    {
        var streamName = partition.Key.Replace("cdc:query:poll:", string.Empty, StringComparison.OrdinalIgnoreCase);
        CustomQueryPollerStreamOptions? settings;
        lock (_stateLock)
        {
            _activePollers.TryGetValue(streamName, out settings);
        }

        if (settings is null) return;

        await PollStreamBatchAsync(settings, context.FencingToken, ct).ConfigureAwait(false);
    }

    public async Task<int> PollStreamBatchAsync(CustomQueryPollerStreamOptions poller, long fencingToken, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(poller);

        EnsureValidated(poller);
        var dialect = ResolveDialectOrThrow(poller);

        var tableId = TableIdentifier.Parse(poller.Table);
        await ValidateOwnerTenantAsync(poller, tableId, ct).ConfigureAwait(false);

        var stream = await _streamStore.EnsureStreamAsync("query_poller", tableId, ct).ConfigureAwait(false);

        // 1. Acquire read-only database connection
        await using var conn = await OpenSourceConnectionAsync(poller, ct).ConfigureAwait(false);

        // 2. Load active watermark vector
        var vectorRecord = await _streamStore.GetWatermarkVectorAsync(stream.Id, ct).ConfigureAwait(false);
        var vector = vectorRecord?.Vector ?? MultiWatermarkVector.Empty;

        // 3. Prepare Command & Enforce Read-Only Isolation & Command Timeout
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = poller.QueryTemplate;
        var timeout = Math.Min(poller.QueryTimeoutSeconds > 0 ? poller.QueryTimeoutSeconds : 30, 60);
        cmd.CommandTimeout = timeout;

        var rawRows = new List<Dictionary<string, object?>>();
        DbTransaction? readOnlyTransaction = null;
        try
        {
            readOnlyTransaction = await EnforceReadOnlySemanticsAsync(conn, cmd, dialect, ct).ConfigureAwait(false);

            // 4. Strictly bind parameters (Zero dynamic SQL concatenation)
            QueryPollerParameterBinder.BindParameters(cmd, poller.Watermarks, vector, poller.BatchSize);

            // 5. Stream read batch with SequentialAccess
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
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSanitizedPollFailure(poller.StreamName, ex);
            throw;
        }
        finally
        {
            await RollbackAndDisposeAsync(readOnlyTransaction).ConfigureAwait(false);
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
            // EXT-09: When all rows were duplicates, persist the advanced vector if it changed; otherwise touch polled
            if (!nextVector.Equals(vector))
            {
                var emptyBatch = new CdcAppendBatch(
                    stream.Id,
                    "query_poller",
                    tableId,
                    fencingToken,
                    stream.HeadOffset,
                    NewWatermark: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Events: [],
                    ImageWrites: [],
                    WatermarkState: nextVector.ToJson());

                await _streamStore.AppendAsync(emptyBatch, ct).ConfigureAwait(false);
            }
            else
            {
                await _streamStore.TouchPolledAsync(stream.Id, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
                _logger.LogWarning("Poller batch for stream '{StreamId}' fully duplicate without vector progress.", stream.Id);
            }

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

        // 8. Commit atomic append batch with vector state and fencing token
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
            if (operation != CdcOperation.Delete && prior != null && CdcOperationClassifier.IsPayloadIdentical(row, prior, poller.Watermarks))
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

    public override void Dispose()
    {
        _optionsChangeRegistration?.Dispose();
        base.Dispose();
    }

    private void EnsureValidated(CustomQueryPollerStreamOptions poller)
    {
        var templateHash = CustomQueryPollerValidator.ComputeTemplateHash(poller);
        var key = poller.StreamName ?? string.Empty;

        lock (_stateLock)
        {
            if (_validatedTemplateHashes.TryGetValue(key, out var cachedHash) &&
                string.Equals(cachedHash, templateHash, StringComparison.Ordinal))
            {
                return;
            }
        }

        _validator.ValidateOrThrow(poller);

        lock (_stateLock)
        {
            _validatedTemplateHashes[key] = templateHash;
        }
    }

    private bool TryResolveDialect(CustomQueryPollerStreamOptions poller, out DatabaseDialect dialect)
    {
        dialect = default;
        if (string.IsNullOrWhiteSpace(poller.DataSource) || _gatewayOptions is null)
        {
            return false;
        }

        var connections = _gatewayOptions.CurrentValue.DataSources?.Connections;
        if (connections is null ||
            !connections.TryGetValue(poller.DataSource, out var dsOptions) ||
            string.IsNullOrWhiteSpace(dsOptions.ConnectionString))
        {
            return false;
        }

        return CustomQueryPollerValidator.TryResolveSupportedDialect(dsOptions.Provider, out dialect);
    }

    private DatabaseDialect ResolveDialectOrThrow(CustomQueryPollerStreamOptions poller)
    {
        if (string.IsNullOrWhiteSpace(poller.DataSource))
        {
            throw new InvalidOperationException($"Poller '{poller.StreamName}' must specify a configured DataSource.");
        }

        var connections = _gatewayOptions?.CurrentValue.DataSources?.Connections;
        if (connections is null ||
            !connections.TryGetValue(poller.DataSource, out var dsOptions) ||
            string.IsNullOrWhiteSpace(dsOptions.ConnectionString))
        {
            throw new InvalidOperationException(
                $"Data source '{poller.DataSource}' for poller '{poller.StreamName}' is not configured in Gateway:DataSources:Connections.");
        }

        if (!CustomQueryPollerValidator.TryResolveSupportedDialect(dsOptions.Provider, out var dialect))
        {
            throw new NotSupportedException(
                $"Data source '{poller.DataSource}' provider '{dsOptions.Provider}' is not supported by CustomQueryPollerService.");
        }

        return dialect;
    }

    private async Task ValidateOwnerTenantAsync(CustomQueryPollerStreamOptions poller, TableIdentifier tableId, CancellationToken ct)
    {
        if (_tableMetadataRepository is null)
        {
            return;
        }

        var tableMeta = await _tableMetadataRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
        if (tableMeta is not null && !string.IsNullOrWhiteSpace(tableMeta.OwnerTenantId))
        {
            if (string.IsNullOrWhiteSpace(poller.OwnerTenantId) ||
                !string.Equals(poller.OwnerTenantId, tableMeta.OwnerTenantId, StringComparison.OrdinalIgnoreCase))
            {
                throw new GovernedCdcConfigurationException(
                    $"Poller '{poller.StreamName}' OwnerTenantId does not match the registered owner tenant of table '{poller.Table}'.");
            }
        }
    }

    private async Task<DbConnection> OpenSourceConnectionAsync(CustomQueryPollerStreamOptions poller, CancellationToken ct)
    {
        if (_dataSourceConnectionFactory != null)
        {
            return await _dataSourceConnectionFactory.CreateConnectionAsync(poller.DataSource ?? string.Empty, ct).ConfigureAwait(false);
        }

        if (_sqlConnectionFactory != null)
        {
            var dataSourceName = poller.DataSource ?? string.Empty;
            var connections = _gatewayOptions?.CurrentValue.DataSources?.Connections;
            if (connections is null || !connections.TryGetValue(dataSourceName, out var dsOptions))
            {
                throw new InvalidOperationException(
                    $"Data source '{dataSourceName}' is not configured in Gateway:DataSources:Connections.");
            }

            return await _sqlConnectionFactory.CreateOpenConnectionAsync(dsOptions, ct).ConfigureAwait(false);
        }

        throw new InvalidOperationException("No database connection factory is configured for CustomQueryPollerService.");
    }

    private void LogSanitizedPollFailure(string? streamName, Exception ex)
    {
        var sqlState = (ex as DbException)?.SqlState;
        _logger.LogError(
            "Custom query poller execution failed for stream '{StreamName}'. ErrorType={ErrorType}, SqlState={SqlState}.",
            streamName ?? "<unnamed>",
            ex.GetType().Name,
            string.IsNullOrWhiteSpace(sqlState) ? "none" : sqlState);
    }

    private static async Task RollbackAndDisposeAsync(DbTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Dispose always follows so an already-faulted connection does not mask the root error.
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Enforces dialect-specific read-only transaction semantics strictly from the configured <see cref="DatabaseDialect"/>.
    /// Returned transactions are attached to <paramref name="cmd"/> and must always be rolled back and disposed (never committed).
    /// </summary>
    internal static async Task<DbTransaction?> EnforceReadOnlySemanticsAsync(
        DbConnection conn,
        DbCommand cmd,
        DatabaseDialect dialect,
        CancellationToken ct)
    {
        switch (dialect)
        {
            case DatabaseDialect.PostgreSql:
                return await BeginReadOnlyStatementTransactionAsync(conn, cmd, "SET TRANSACTION READ ONLY;", ct).ConfigureAwait(false);

            case DatabaseDialect.Oracle:
                return await BeginReadOnlyStatementTransactionAsync(conn, cmd, "SET TRANSACTION READ ONLY", ct).ConfigureAwait(false);

            case DatabaseDialect.SqlServer:
            {
                // SQL Server relies on read-only login permissions / ApplicationIntent=ReadOnly and an explicit transaction unconditionally rolled back.
                var transaction = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
                cmd.Transaction = transaction;
                return transaction;
            }

            case DatabaseDialect.Sqlite:
            {
                await using var pragma = conn.CreateCommand();
                pragma.CommandText = "PRAGMA query_only = ON;";
                await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return null;
            }

            case DatabaseDialect.Databricks:
            default:
                throw new NotSupportedException($"Database dialect '{dialect}' is not supported for read-only custom query polling.");
        }
    }

    private static async Task<DbTransaction> BeginReadOnlyStatementTransactionAsync(
        DbConnection conn,
        DbCommand cmd,
        string readOnlyStatement,
        CancellationToken ct)
    {
        var transaction = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var setReadOnly = conn.CreateCommand();
            setReadOnly.Transaction = transaction;
            setReadOnly.CommandText = readOnlyStatement;
            await setReadOnly.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            cmd.Transaction = transaction;
            return transaction;
        }
        catch
        {
            await RollbackAndDisposeAsync(transaction).ConfigureAwait(false);
            throw;
        }
    }
}
