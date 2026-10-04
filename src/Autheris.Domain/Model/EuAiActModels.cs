namespace Autheris.Domain.Model;

using System;

/// <summary>
/// F-AI-12-B: Canonical EU AI Act Article 10 Compliance Certificate model.
/// Provides verifiable, tamper-evident proof of data governance, bias mitigation,
/// k-anonymity suppression, and differential privacy guarantees.
/// </summary>
public sealed record EuAiActArticle10Certificate(
    string CertificateId,
    string TenantId,
    DateTimeOffset IssuedAtUtc,
    string Specification,
    DifferentialPrivacyMetricsSummary DifferentialPrivacyMetrics,
    AuditLineageSummary AuditLineage,
    string AuditChainTailHash,
    string IntegritySealSha256
);

public sealed record DifferentialPrivacyMetricsSummary(
    double TotalDailyEpsilonBudget,
    double ConsumedEpsilon,
    double RemainingEpsilon,
    DateTimeOffset LastResetUtc
);

public sealed record AuditLineageSummary(
    int TotalAuditEvents,
    int TotalPerturbations,
    int SmallCohortSuppressionCount
);
