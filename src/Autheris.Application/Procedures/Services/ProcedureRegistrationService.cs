namespace Autheris.Application.Procedures.Services;

using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// F-SQL-02: Loads the declarations, activates them after a successful catalog validation and re-validates them
/// periodically so schema drift (renamed parameters, removed policies, ...) disables the endpoint (fail-closed, 503).
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _loader.LoadFromDirectory();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // Review P-3: a broken procedure directory must not stop the host; no endpoint is registered (fail-closed).
            _logger?.LogError(ex, "Stored procedure declarations could not be loaded.");
        }

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

    /// <summary>Validates all pending entries and all entries whose last validation is older than the configured interval.</summary>
    internal async Task ValidateDueAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(_options.Value.SqlEndpoints.Procedures.RevalidationIntervalMinutes);
        var now = DateTimeOffset.UtcNow;

        var due = _registry.GetAll()
            .Where(r => r.State == ProcedureState.Pending
                || (r.Definition.ValidationMode == ProcedureValidationMode.Catalog && (r.ValidatedAt == null
                    || now - r.ValidatedAt >= (r.State == ProcedureState.Disabled ? DisabledRetryInterval : interval))))
            .ToList();

        if (due.Count == 0)
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var validator = due.Any(r => r.Definition.ValidationMode == ProcedureValidationMode.Catalog)
            ? scope.ServiceProvider.GetRequiredService<StoredProcedureCatalogValidator>()
            : null;
        var audit = scope.ServiceProvider.GetService<IAuditLogRepository>();

        foreach (var entry in due)
        {
            ct.ThrowIfCancellationRequested();

            if (entry.Definition.ValidationMode == ProcedureValidationMode.Declared)
            {
                var referenced = entry.Definition.ReferencedTables.Count > 0
                    ? entry.Definition.ReferencedTables
                    : (entry.Definition.ResultTable != null ? [entry.Definition.ResultTable] : []);

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
                    _logger?.LogInformation("Declared procedure endpoint '{Endpoint}' activated (contract-first).", entry.Definition.Name);
                }

                continue;
            }

            var result = await validator!.ValidateAsync(entry.Definition, ct).ConfigureAwait(false);

            if (result.IsValid)
            {
                // Review P-7: compare-and-swap; a definition replaced or removed during validation is not touched.
                if (!_registry.TryMarkActive(entry.Definition, result))
                {
                    continue;
                }

                if (entry.State != ProcedureState.Active)
                {
                    _logger?.LogInformation("Procedure endpoint '{Endpoint}' activated.", entry.Definition.Name);
                }

                continue;
            }

            string reason = string.Join(" | ", result.Errors.OrderBy(e => e, StringComparer.Ordinal));
            bool wasAlreadyDisabled = entry.State == ProcedureState.Disabled;

            if (!_registry.TryMarkDisabled(entry.Definition, reason))
            {
                continue; // replaced or removed (hot reload) while validating; the new entry is validated next round
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
                        TenantId = TenantId.LegacySingleTenant,
                        EventType = "PROCEDURE_DISABLED",
                        ActorSid = new Sid("system:procedure-validator"),
                        TargetTable = entry.Definition.ProcedureName,
                        Decision = "DENY",
                        TraceId = Guid.NewGuid().ToString("N"),
                        DetailsJson = JsonSerializer.Serialize(new { endpoint = entry.Definition.Name, errors = result.Errors })
                    },
                    ct).ConfigureAwait(false);
            }
        }
    }
}
