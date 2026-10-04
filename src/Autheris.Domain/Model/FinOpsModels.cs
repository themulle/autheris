namespace Autheris.Domain.Model;

using System;
using System.Collections.Generic;

/// <summary>
/// F-AI-08: FOCUS Specification v1.2 compliant cost and usage record.
/// Standardizes cloud and AI compute billing for chargeback and showback.
/// </summary>
public sealed record FocusCostRecord(
    string ChargePeriodStart,
    string ChargePeriodEnd,
    decimal BilledCost,
    decimal EffectiveCost,
    string Currency,
    double ConsumedQuantity,
    string ConsumedUnit,
    string SubAccountId, // Maps to TenantId
    string ResourceId,   // Operation or Tool Name
    string ServiceName,  // "Autheris"
    string PricingCategory, // "AI-Inference" or "DatabaseCompute"
    IReadOnlyDictionary<string, string>? Tags = null
);

/// <summary>
/// F-AI-08: Budget status and cap governance record for a tenant.
/// </summary>
public sealed record BudgetStatus(
    bool IsExceeded,
    bool IsWarning,
    decimal CurrentSpend,
    decimal BudgetLimit,
    string TenantId
);
