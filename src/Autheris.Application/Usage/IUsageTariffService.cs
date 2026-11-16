namespace Autheris.Application.Usage;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;

/// <summary>Command to append an immutable future-dated tariff version.</summary>
public sealed record CreateUsageTariffVersionRequest(
    string TenantId,
    string DataSource,
    DateOnly ValidFrom,
    decimal BasePerQuery,
    decimal PerRow,
    decimal PerMib,
    decimal PerComputeSecond,
    decimal PerAnalysisCost,
    decimal SourceCostFactor,
    string? SourceCostUnit,
    string UnitLabel,
    short ComputeBasis,
    string CreatedBy,
    string Reason,
    DateOnly? ValidUntil = null,
    string? TraceId = null);

/// <summary>Thrown when tariff input fails validation (maps to HTTP 400).</summary>
public sealed class UsageTariffValidationException : ArgumentException
{
    public UsageTariffValidationException(string message)
        : base(message)
    {
    }

    public UsageTariffValidationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Manages immutable tariff versions, validation, auditing, and startup seeding.</summary>
public interface IUsageTariffService
{
    Task<IReadOnlyList<UsageTariffVersion>> GetEffectiveTariffsAsync(
        TenantId tenant,
        DateOnly atUtc,
        string? dataSource = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<UsageTariffVersion>> GetVersionsAsync(
        string tenantId,
        string dataSource,
        CancellationToken ct = default);

    Task<UsageTariffVersion> AddVersionAsync(
        CreateUsageTariffVersionRequest request,
        CancellationToken ct = default);

    Task<int> SeedInitialTariffsAsync(CancellationToken ct = default);
}
