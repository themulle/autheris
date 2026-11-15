namespace Autheris.Application.Procedures.Services;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02: Loads declared procedure contracts and activates them once all declared tables and policies satisfy
/// static declared-only governance rules (fail-closed, 503).
/// </summary>
public sealed class ProcedureRegistrationService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DisabledRetryInterval = TimeSpan.FromMinutes(1);

    private readonly ProcedureDefinitionLoader _loader;
    private readonly IProcedureRegistry _registry;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptions<GatewayOptions> _options;
    private readonly ILogger<ProcedureRegistrationService>? _logger;
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;

    public ProcedureRegistrationService(
        ProcedureDefinitionLoader loader,
        IProcedureRegistry registry,
        IServiceScopeFactory scopeFactory,
        IOptions<GatewayOptions> options,
        ILogger<ProcedureRegistrationService>? logger = null)
    {
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
    }

    /// <summary>
    /// Loads the declarations synchronously so a legacy <c>*.proc.sql</c> file stops the host start.
    /// Any other load failure only logs; no endpoint is registered then (fail-closed).
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _loader.LoadFromDirectory();
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException or InvalidOperationException))
        {
            _logger?.LogError(ex, "Stored procedure declarations could not be loaded.");
        }

        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await ValidateDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger?.LogError(ex, "Stored procedure validation round failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }

    /// <summary>Re-scans the directory, then validates all pending entries and all entries whose last validation is older than the configured interval.</summary>
    internal async Task ValidateDueAsync(CancellationToken ct)
    {
        // the directory is re-scanned once per revalidation interval, before validating: new and changed declarations
        // enter Pending, removed ones leave the registry.
        var interval = TimeSpan.FromMinutes(_options.Value.SqlEndpoints.Procedures.RevalidationIntervalMinutes);
        var now = DateTimeOffset.UtcNow;
        if (now - _lastScan >= interval)
        {
            _lastScan = now;
            _loader.Rescan();
        }

        var due = _registry.GetAll()
            .Where(r => r.State == ProcedureState.Pending
                || r.ValidatedAt == null
                || now - r.ValidatedAt >= (r.State == ProcedureState.Disabled ? DisabledRetryInterval : interval))
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var audit = scope.ServiceProvider.GetService<IAuditLogRepository>();
        var tableRepository = scope.ServiceProvider.GetService<ITableMetadataRepository>();
        var virtualFilters = scope.ServiceProvider.GetService<IVirtualFilterSnapshotProvider>();
        VirtualFilterSnapshot? filterSnapshot = null;
        bool snapshotLoaded = false;

        foreach (var entry in due)
        {
            ct.ThrowIfCancellationRequested();

            var referenced = entry.Definition.ReferencedTables;
            if (string.IsNullOrWhiteSpace(entry.Definition.ResultTable)
                || entry.Definition.DeclaredOutputs.Count == 0
                || referenced.Count == 0)
            {
                _registry.TryMarkPending(entry.Definition, "DECLARED_CONTRACT_INCOMPLETE");
                continue;
            }

            if (tableRepository != null)
            {
                string? missingTable = null;
                foreach (var tableKey in referenced.Concat([entry.Definition.ResultTable!]).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!entry.Definition.TryResolveTable(tableKey, out var tableId))
                    {
                        missingTable = tableKey;
                        break;
                    }

                    var meta = await tableRepository.GetTableMetadataAsync(tableId, ct).ConfigureAwait(false);
                    if (meta == null || meta.Columns.Count == 0)
                    {
                        missingTable = tableKey;
                        break;
                    }
                }

                if (missingTable != null)
                {
                    _registry.TryMarkPending(entry.Definition, $"TABLE_NOT_REGISTERED: {missingTable}");
                    continue;
                }
            }

            if (entry.Definition.RlsMode == ProcedureRlsMode.SessionContext
                && entry.Definition.RowScopeKey.Count == 0
                && virtualFilters != null)
            {
                if (!snapshotLoaded)
                {
                    filterSnapshot = await virtualFilters.GetAsync(ct).ConfigureAwait(false);
                    snapshotLoaded = true;
                }

                if (filterSnapshot != null && ProcedureDefinitionLoader.HasActiveRowFilters(filterSnapshot, entry.Definition))
                {
                    await MarkDisabledAsync(entry, ["ROW_SCOPE_REQUIRED"], audit, ct).ConfigureAwait(false);
                    continue;
                }
            }

            var sources = entry.Definition.DeclaredOutputSources.Count > 0
                ? entry.Definition.DeclaredOutputSources
                : null;

            var declaredResult = new ProcedureValidationResult(
                IsValid: true,
                Errors: [],
                ResultColumns: entry.Definition.DeclaredOutputs,
                ReferencedTables: referenced,
                ParameterSqlTypes: entry.Definition.Parameters.ToDictionary(p => p.Name, p => p.SqlType, StringComparer.OrdinalIgnoreCase),
                ResultColumnSources: sources);

            // Review P-7: only the exact definition that was snapshotted may be activated.
            if (!_registry.TryMarkActive(entry.Definition, declaredResult))
            {
                continue;
            }

            if (entry.State != ProcedureState.Active)
            {
                _logger?.LogInformation("Declared procedure endpoint '{Endpoint}' activated.", entry.Definition.Name);
            }
        }
    }

    private async Task MarkDisabledAsync(
        RegisteredProcedure entry,
        IReadOnlyList<string> errors,
        IAuditLogRepository? audit,
        CancellationToken ct)
    {
        string reason = string.Join(" | ", errors.OrderBy(e => e, StringComparer.Ordinal));
        bool wasAlreadyDisabled = entry.State == ProcedureState.Disabled;

        if (!_registry.TryMarkDisabled(entry.Definition, reason))
        {
            return;
        }

        if (!wasAlreadyDisabled)
        {
            _logger?.LogWarning("Procedure endpoint '{Endpoint}' disabled: {Reason}", entry.Definition.Name, reason);
        }
        else if (!string.Equals(entry.DisabledReason, reason, StringComparison.Ordinal))
        {
            _logger?.LogDebug("Procedure endpoint '{Endpoint}' disabled reason changed: {Reason}", entry.Definition.Name, reason);
        }
        else
        {
            _logger?.LogDebug("Procedure endpoint '{Endpoint}' still disabled: {Reason}", entry.Definition.Name, reason);
        }

        if (audit != null && entry.State != ProcedureState.Disabled)
        {
            await audit.RecordAuditEventAsync(
                new AuditLogEntry
                {
                    TenantId = TenantId.Platform, // startup validation audit (DT-A-6)
                    EventType = "PROCEDURE_DISABLED",
                    ActorSid = new Sid("system:procedure-validator"),
                    TargetTable = entry.Definition.ProcedureName,
                    Decision = "DENY",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = JsonSerializer.Serialize(new { endpoint = entry.Definition.Name, errors })
                },
                ct).ConfigureAwait(false);
        }
    }
}
