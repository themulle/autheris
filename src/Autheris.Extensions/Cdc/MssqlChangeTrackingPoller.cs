namespace Autheris.Extensions.Cdc;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.State;
using Autheris.Application.Streaming;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Polls SQL Server Change Tracking (CHANGETABLE) of one tracked table and appends the changes to the durable, encrypted CDC stream
/// (F-CDC-02, PLAN-CDC-99 D-A4/D-A5). The source is read in ONE snapshot transaction (min valid version, current version and the batch
/// are consistent) with an explicit column list; the append is ONE governance transaction (fenced watermark upsert, head offset CAS,
/// events, row images) so a crash re-reads the same versions and nothing is ever produced twice. MSSQL changes are never published on
/// the <see cref="ICdcEventChannel"/> (AC-14); consumers are woken by the data-free <see cref="ICdcStreamSignal"/>.
/// </summary>
public sealed class MssqlChangeTrackingPoller : IMssqlChangeTrackingPoller
{
    private static readonly TimeSpan TouchInterval = TimeSpan.FromSeconds(60);

    private static readonly Counter<long> WatermarkExpiredTotal = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_watermark_expired_total",
        description: "Number of polls aborted because the stored CDC watermark is older than the minimum valid change tracking version");

    private static readonly Counter<long> EventsAppended = GatewayDiagnostics.Meter.CreateCounter<long>(
        "autheris_cdc_stream_events_appended_total",
        description: "Events appended to CDC streams");

    private static readonly Histogram<double> AppendDuration = GatewayDiagnostics.Meter.CreateHistogram<double>(
        "autheris_cdc_stream_append_duration_seconds",
        unit: "s",
        description: "Duration of CDC stream append transactions");

    private readonly ISqlConnectionFactory _connectionFactory;
    private readonly ICdcTableActivator _activator;
    private readonly ICdcStreamStore _store;
    private readonly CdcChangeEventBuilder _builder;
    private readonly ICdcStreamSignal _signal;
    private readonly IOptionsMonitor<GatewayOptions> _options;
    private readonly ILogger<MssqlChangeTrackingPoller> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastTouched = new(StringComparer.Ordinal);

    public MssqlChangeTrackingPoller(
        ISqlConnectionFactory connectionFactory,
        ICdcTableActivator activator,
        ICdcStreamStore store,
        CdcChangeEventBuilder builder,
        ICdcStreamSignal signal,
        IOptionsMonitor<GatewayOptions> options,
        ILogger<MssqlChangeTrackingPoller> logger,
        TimeProvider? timeProvider = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _activator = activator ?? throw new ArgumentNullException(nameof(activator));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    private static readonly System.Text.RegularExpressions.Regex IdentifierRegex =
        new(@"^[a-zA-Z_][a-zA-Z0-9_]*$", System.Text.RegularExpressions.RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

    public static void ValidateSqlIdentifier(string identifier, string paramName)
    {
        if (string.IsNullOrWhiteSpace(identifier) || !IdentifierRegex.IsMatch(identifier))
        {
            throw new ArgumentException($"Security error: Invalid SQL identifier '{identifier}'. Only alphanumeric characters and underscores are allowed.", paramName);
        }
    }

    /// <summary>
    /// Polls one table and appends its changes (and control events) to the stream; returns the number of appended events. Only complete
    /// change versions are appended (a batch cut inside one SYS_CHANGE_VERSION never advances past that version).
    /// <paramref name="fencingToken"/> is the table lease token; the append transaction rejects a stale one
    /// (<see cref="FencingTokenRejectedException"/>) and a lost head race (<see cref="CdcStreamConflictException"/>) appends nothing and
    /// returns 0 so the next cycle re-reads the head.
    /// </summary>
    /// <exception cref="CdcSourceConfigurationException">The table is not active; the message is the stable activation code.</exception>
    public async Task<int> PollTableChangesAsync(TableIdentifier table, long fencingToken, CancellationToken ct = default)
    {
        ValidateSqlIdentifier(table.Schema, nameof(table.Schema));
        ValidateSqlIdentifier(table.TableName, nameof(table.TableName));

        var settings = CdcTableSettings.Find(_options.CurrentValue.MssqlChangeTracking, table);
        if (settings is null)
        {
            return 0;
        }

        var activation = await EnsureActivationAsync(settings, ct).ConfigureAwait(false);
        if (activation is null)
        {
            return 0; // halted: only an administrative resync restarts the stream
        }

        var stream = activation.Stream;
        var stored = stream.SourceWatermark;
        var head = stream.HeadOffset;

        await using var conn = await _connectionFactory.CreateOpenConnectionAsync(activation.Source, ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(
            settings.AllowNonSnapshotRead ? IsolationLevel.ReadCommitted : IsolationLevel.Snapshot, ct).ConfigureAwait(false);

        var timeout = settings.QueryTimeoutSeconds;
        var minValid = await CdcSourceQueries.GetMinValidVersionAsync(conn, tx, table, timeout, ct).ConfigureAwait(false)
            ?? throw new CdcSourceConfigurationException(CdcActivationCodes.TrackingDisabled);
        var current = await CdcSourceQueries.GetCurrentVersionAsync(conn, tx, timeout, ct).ConfigureAwait(false)
            ?? throw new CdcSourceConfigurationException(CdcActivationCodes.TrackingDisabled);

        // first run: start at the current version instead of replaying the whole change history
        if (stored is null)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            await AppendAsync(activation, fencingToken, head, current, [], [], ct).ConfigureAwait(false);
            _logger.LogInformation("CDC for table {Table} starts at the current change tracking version {Version}; no history is replayed.", table, current);
            return 0;
        }

        // a watermark behind the retention horizon means lost changes: surface them explicitly, never skip silently
        if (stored.Value < minValid)
        {
            await tx.CommitAsync(ct).ConfigureAwait(false);
            return await HandleExpiredAsync(activation, fencingToken, stored.Value, minValid, current, ct).ConfigureAwait(false);
        }

        var rows = await CdcSourceQueries.ReadChangesAsync(
            conn, tx, table, activation.PrimaryKey, activation.Columns, stored.Value, settings.BatchSize, null, timeout, ct).ConfigureAwait(false);
        if (rows.Count >= settings.BatchSize)
        {
            var cutVersion = rows[^1].Version;
            var complete = rows.TakeWhile(r => r.Version < cutVersion).ToList();
            rows = complete.Count == 0
                // the whole batch is one version, which may continue beyond it: fetch that version completely
                ? await CdcSourceQueries.ReadChangesAsync(
                    conn, tx, table, activation.PrimaryKey, activation.Columns, stored.Value, settings.BatchSize, cutVersion, timeout, ct).ConfigureAwait(false)
                : complete;
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);

        if (rows.Count == 0)
        {
            await TouchAsync(stream.Id, ct).ConfigureAwait(false);
            return 0;
        }

        CdcBuiltBatch built;
        try
        {
            built = await _builder.BuildChangesAsync(activation, head, rows, ct).ConfigureAwait(false);
        }
        catch (CdcSourceConfigurationException ex) when (ex.Message == CdcActivationCodes.UnsupportedType)
        {
            await _store.SetStatusAsync(stream.Id, CdcStreamStatus.ActivationFailed, CdcActivationCodes.UnsupportedType, ct).ConfigureAwait(false);
            throw;
        }

        var appended = await AppendAsync(activation, fencingToken, head, rows.Max(r => r.Version), built.Events, built.ImageWrites, ct).ConfigureAwait(false);
        if (appended && stream.Status != CdcStreamStatus.Active)
        {
            await _store.SetStatusAsync(stream.Id, CdcStreamStatus.Active, null, ct).ConfigureAwait(false);
        }

        return appended ? built.Events.Count : 0;
    }

    private async Task<CdcActivation?> EnsureActivationAsync(CdcTableSettings settings, CancellationToken ct)
    {
        var result = await _activator.EnsureActiveAsync(settings, ct).ConfigureAwait(false);
        if (result.IsActive)
        {
            return result.Activation;
        }

        if (result.Code == CdcActivationCodes.WatermarkExpired)
        {
            return null;
        }

        throw new CdcSourceConfigurationException(result.Code ?? CdcActivationCodes.TableUnavailable);
    }

    private async Task<int> HandleExpiredAsync(CdcActivation activation, long token, long stored, long minValid, long current, CancellationToken ct)
    {
        WatermarkExpiredTotal.Add(1);
        var table = activation.Settings.Table;
        if (activation.Settings.OnWatermarkExpired == CdcOnWatermarkExpired.Halt)
        {
            await _store.SetStatusAsync(activation.Stream.Id, CdcStreamStatus.Halted, CdcActivationCodes.WatermarkExpired, ct).ConfigureAwait(false);
            _logger.LogError(
                "MSSQL Change Tracking watermark {Stored} of table {Table} is older than min valid version {MinValid}; the stream is halted until an administrator resyncs it.",
                stored, table, minValid);
            return 0;
        }

        var head = activation.Stream.HeadOffset;
        var control = await _builder.BuildControlAsync(
            activation, head + 1, CdcStreamEventKind.WatermarkExpired, new CdcControlPayload("watermark_expired", stored, minValid, current), current, ct).ConfigureAwait(false);
        _logger.LogWarning(
            "MSSQL Change Tracking watermark {Stored} of table {Table} is older than min valid version {MinValid}; restarting at {Current} (changes in between are lost, consumers see a WatermarkExpired event).",
            stored, table, minValid, current);
        return await AppendAsync(activation, token, head, current, [control], [], ct).ConfigureAwait(false) ? 1 : 0;
    }

    /// <returns>False when the head race was lost (nothing written); the fenced rejection propagates.</returns>
    private async Task<bool> AppendAsync(
        CdcActivation activation,
        long token,
        long head,
        long newWatermark,
        IReadOnlyList<CdcStreamEventRecord> events,
        IReadOnlyList<CdcRowImageWrite> images,
        CancellationToken ct)
    {
        var stream = activation.Stream;
        var batch = new CdcAppendBatch(stream.Id, CdcTableActivator.StreamSource, activation.Settings.Table, token, head, newWatermark, events, images);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await _store.AppendAsync(batch, ct).ConfigureAwait(false);
        }
        catch (CdcStreamConflictException)
        {
            _logger.LogDebug("Append to the CDC stream of table {Table} lost the head race; the head is re-read next cycle.", activation.Settings.Table);
            return false;
        }

        AppendDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        EventsAppended.Add(events.Count);
        _lastTouched[stream.Id.Value] = _time.GetUtcNow();

        if (events.Count > 0)
        {
            try
            {
                await _signal.NotifyAdvancedAsync(stream.Id, head + events.Count, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // best effort: tail readers also poll the head offset
                _logger.LogDebug(ex, "The CDC stream wake-up signal failed; readers pick the change up by polling.");
            }
        }

        return true;
    }

    /// <summary>Bounds the write load of idle tables: <c>last_polled</c> is updated at most once per minute.</summary>
    private async Task TouchAsync(CdcStreamId id, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_lastTouched.TryGetValue(id.Value, out var last) && now - last < TouchInterval)
        {
            return;
        }

        _lastTouched[id.Value] = now;
        await _store.TouchPolledAsync(id, now, ct).ConfigureAwait(false);
    }
}
