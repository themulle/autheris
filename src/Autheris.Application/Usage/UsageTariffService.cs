namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>Enforces tariff immutability, future-effective dates, validation, audit fail-closed, and startup seeding.</summary>
public sealed class UsageTariffService : IUsageTariffService
{
    private readonly IUsageRepository _repository;
    private readonly IAuditWriter _auditWriter;
    private readonly IOptions<UsageAccountingOptions> _options;
    private readonly ILogger<UsageTariffService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string, bool>? _isKnownDataSource;
    private readonly Func<string, string?>? _expectedCostUnitResolver;

    public UsageTariffService(
        IUsageRepository repository,
        IAuditWriter auditWriter,
        IOptions<UsageAccountingOptions> options,
        ILogger<UsageTariffService> logger,
        TimeProvider? timeProvider = null,
        Func<string, bool>? isKnownDataSource = null,
        Func<string, string?>? expectedCostUnitResolver = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _auditWriter = auditWriter ?? throw new ArgumentNullException(nameof(auditWriter));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isKnownDataSource = isKnownDataSource;
        _expectedCostUnitResolver = expectedCostUnitResolver;
    }

    public async Task<IReadOnlyList<UsageTariffVersion>> GetEffectiveTariffsAsync(
        TenantId tenant,
        DateOnly atUtc,
        string? dataSource = null,
        CancellationToken ct = default)
    {
        var rows = await _repository.GetTariffsAsync(tenant, atUtc, atUtc, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return rows;
        }

        string trimmed = dataSource.Trim();
        return rows
            .Where(r => string.Equals(r.DataSource, trimmed, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(r.DataSource, "*", StringComparison.Ordinal))
            .ToList();
    }

    public async Task<IReadOnlyList<UsageTariffVersion>> GetVersionsAsync(
        string tenantId,
        string dataSource,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            throw new UsageTariffValidationException("DataSource is required.");
        }

        if (!string.Equals(tenantId, "*", StringComparison.Ordinal) && !TenantId.TryParse(tenantId, out _))
        {
            throw new UsageTariffValidationException($"TenantId '{tenantId}' is invalid.");
        }

        string trimmedSource = dataSource.Trim();
        var lookupTenant = string.Equals(tenantId, "*", StringComparison.Ordinal)
            ? TenantId.Platform
            : new TenantId(tenantId);

        var rows = await _repository.GetTariffsAsync(lookupTenant, DateOnly.MinValue, DateOnly.MaxValue, ct).ConfigureAwait(false);
        return rows
            .Where(r => string.Equals(r.DataSource, trimmedSource, StringComparison.OrdinalIgnoreCase)
                        && (!string.Equals(tenantId, "*", StringComparison.Ordinal)
                            || string.Equals(r.TenantId, "*", StringComparison.Ordinal)))
            .ToList();
    }

    public async Task<UsageTariffVersion> AddVersionAsync(
        CreateUsageTariffVersionRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var now = _timeProvider.GetUtcNow();
        var todayUtc = DateOnly.FromDateTime(now.UtcDateTime);
        var failures = ValidateCreateRequest(request, todayUtc);
        if (failures.Count > 0)
        {
            throw new UsageTariffValidationException(string.Join(" ", failures));
        }

        string tenantId = request.TenantId.Trim();
        string dataSource = request.DataSource.Trim();
        string reason = request.Reason.Trim();
        string createdBy = request.CreatedBy.Trim();
        string? normalizedUnit = request.SourceCostFactor > 0m ? request.SourceCostUnit!.Trim() : null;

        var lookupTenant = string.Equals(tenantId, "*", StringComparison.Ordinal)
            ? TenantId.Platform
            : new TenantId(tenantId);

        var existing = await _repository.GetTariffsAsync(lookupTenant, DateOnly.MinValue, DateOnly.MaxValue, ct).ConfigureAwait(false);
        var previous = existing
            .Where(v => string.Equals(v.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(v.DataSource, dataSource, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Version)
            .FirstOrDefault();

        if (previous is not null && request.ValidFrom < previous.ValidFrom)
        {
            throw new UsageTariffValidationException(
                $"ValidFrom ({request.ValidFrom:yyyy-MM-dd}) must not precede the latest tariff version's ValidFrom ({previous.ValidFrom:yyyy-MM-dd}).");
        }

        int oldVersion = previous?.Version ?? 0;
        int newVersion = oldVersion + 1;

        var auditTenant = string.Equals(tenantId, "*", StringComparison.Ordinal)
            ? TenantId.Platform
            : new TenantId(tenantId);

        var auditEntry = new AuditLogEntry
        {
            TenantId = auditTenant,
            OccurredAt = now,
            EventType = AuditEventTypes.UsageTariffVersionAdded,
            ActorSid = new Sid(createdBy),
            TargetTable = $"USAGE_TARIFF/{dataSource}",
            Decision = "ALLOW",
            TraceId = string.IsNullOrWhiteSpace(request.TraceId) ? Guid.NewGuid().ToString("N") : request.TraceId,
            DetailsJson = JsonSerializer.Serialize(new
            {
                tenantId,
                dataSource,
                oldVersion,
                newVersion,
                validFrom = request.ValidFrom,
                validUntil = request.ValidUntil,
                unitLabel = request.UnitLabel,
                caller = createdBy,
                reason
            })
        };

        // Required audit BEFORE commit; failure aborts fail-closed without mutating USAGE_TARIFF.
        try
        {
            await _auditWriter.RecordRequiredAsync(auditEntry, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (AuditUnavailableException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AuditUnavailableException("The audit store is temporarily unavailable (fail-closed).", ex);
        }

        var version = new UsageTariffVersion(
            TenantId: tenantId,
            DataSource: dataSource,
            Version: 0,
            ValidFrom: request.ValidFrom,
            ValidUntil: request.ValidUntil,
            BasePerQuery: request.BasePerQuery,
            PerRow: request.PerRow,
            PerMib: request.PerMib,
            PerComputeSecond: request.PerComputeSecond,
            PerAnalysisCost: request.PerAnalysisCost,
            SourceCostFactor: request.SourceCostFactor,
            SourceCostUnit: normalizedUnit,
            UnitLabel: request.UnitLabel,
            ComputeBasis: request.ComputeBasis,
            CreatedBy: createdBy,
            CreatedAt: now,
            Reason: reason);

        return await _repository.AddTariffVersionAsync(version, ct).ConfigureAwait(false);
    }

    public async Task<int> SeedInitialTariffsAsync(CancellationToken ct = default)
    {
        var initial = _options.Value.InitialTariffs;
        if (initial is null || initial.Count == 0)
        {
            return 0;
        }

        var now = _timeProvider.GetUtcNow();
        var todayUtc = DateOnly.FromDateTime(now.UtcDateTime);
        int seeded = 0;

        for (int i = 0; i < initial.Count; i++)
        {
            var entry = initial[i];
            if (entry is null)
            {
                continue;
            }

            var failures = new List<string>();
            UsageTariffValidation.ValidateRatesAndMetadata(
                entry.TenantId,
                entry.DataSource,
                entry.BasePerQuery,
                entry.PerRow,
                entry.PerMib,
                entry.PerComputeSecond,
                entry.PerAnalysisCost,
                entry.SourceCostFactor,
                entry.SourceCostUnit,
                entry.UnitLabel,
                entry.ComputeBasis,
                entry.Reason,
                failures);
            ValidateDataSourceAndCollector(entry.DataSource, entry.SourceCostFactor, entry.SourceCostUnit, failures);

            if (failures.Count > 0)
            {
                throw new UsageTariffValidationException(
                    $"Invalid InitialTariffs[{i}]: {string.Join(" ", failures)}");
            }

            string tenantId = entry.TenantId.Trim();
            string dataSource = entry.DataSource.Trim();
            var lookupTenant = string.Equals(tenantId, "*", StringComparison.Ordinal)
                ? TenantId.Platform
                : new TenantId(tenantId);

            var existing = await _repository.GetTariffsAsync(lookupTenant, DateOnly.MinValue, DateOnly.MaxValue, ct).ConfigureAwait(false);
            bool alreadyExists = existing.Any(v =>
                string.Equals(v.TenantId, tenantId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(v.DataSource, dataSource, StringComparison.OrdinalIgnoreCase));

            if (alreadyExists)
            {
                _logger.LogWarning(
                    "Gateway:UsageAccounting:InitialTariffs entry for tenant '{TenantId}' and data source '{DataSource}' was ignored because USAGE_TARIFF already contains at least one version.",
                    tenantId,
                    dataSource);
                continue;
            }

            var validFrom = entry.ValidFrom == default ? todayUtc : entry.ValidFrom;
            string? normalizedUnit = entry.SourceCostFactor > 0m ? entry.SourceCostUnit!.Trim() : null;

            await _repository.AddTariffVersionAsync(new UsageTariffVersion(
                TenantId: tenantId,
                DataSource: dataSource,
                Version: 0,
                ValidFrom: validFrom,
                ValidUntil: null,
                BasePerQuery: entry.BasePerQuery,
                PerRow: entry.PerRow,
                PerMib: entry.PerMib,
                PerComputeSecond: entry.PerComputeSecond,
                PerAnalysisCost: entry.PerAnalysisCost,
                SourceCostFactor: entry.SourceCostFactor,
                SourceCostUnit: normalizedUnit,
                UnitLabel: entry.UnitLabel,
                ComputeBasis: entry.ComputeBasis,
                CreatedBy: "system:initial-seed",
                CreatedAt: now,
                Reason: entry.Reason.Trim()), ct).ConfigureAwait(false);

            seeded++;
        }

        return seeded;
    }

    private List<string> ValidateCreateRequest(CreateUsageTariffVersionRequest request, DateOnly todayUtc)
    {
        var failures = new List<string>();
        UsageTariffValidation.ValidateRatesAndMetadata(
            request.TenantId,
            request.DataSource,
            request.BasePerQuery,
            request.PerRow,
            request.PerMib,
            request.PerComputeSecond,
            request.PerAnalysisCost,
            request.SourceCostFactor,
            request.SourceCostUnit,
            request.UnitLabel,
            request.ComputeBasis,
            request.Reason,
            failures);

        var earliestAllowed = todayUtc.AddDays(1);
        if (request.ValidFrom < earliestAllowed)
        {
            failures.Add($"ValidFrom must be on or after tomorrow UTC ({earliestAllowed:yyyy-MM-dd}); retroactive tariffs are forbidden.");
        }

        if (request.ValidUntil.HasValue && request.ValidUntil.Value <= request.ValidFrom)
        {
            failures.Add("ValidUntil must be strictly after ValidFrom when specified.");
        }

        if (string.IsNullOrWhiteSpace(request.CreatedBy) || request.CreatedBy.Trim().Length > UsageTariffValidation.MaxCreatedByLength)
        {
            failures.Add($"CreatedBy is required and must be at most {UsageTariffValidation.MaxCreatedByLength} characters.");
        }

        ValidateDataSourceAndCollector(request.DataSource, request.SourceCostFactor, request.SourceCostUnit, failures);
        return failures;
    }

    private void ValidateDataSourceAndCollector(
        string? dataSource,
        decimal sourceCostFactor,
        string? sourceCostUnit,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(dataSource))
        {
            return;
        }

        string trimmedSource = dataSource.Trim();
        if (!string.Equals(trimmedSource, "*", StringComparison.Ordinal)
            && _isKnownDataSource is not null
            && !_isKnownDataSource(trimmedSource))
        {
            failures.Add($"DataSource '{trimmedSource}' is not a configured data source.");
        }

        if (sourceCostFactor > 0m && !string.IsNullOrWhiteSpace(sourceCostUnit))
        {
            string? expectedUnit = _expectedCostUnitResolver?.Invoke(trimmedSource);
            if (!string.IsNullOrWhiteSpace(expectedUnit)
                && !string.Equals(sourceCostUnit.Trim(), expectedUnit.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"SourceCostUnit '{sourceCostUnit}' does not match the collector's unit '{expectedUnit}' for data source '{trimmedSource}'.");
            }
        }
    }
}
