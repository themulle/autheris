namespace Autheris.Application.Lineage;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface ILineageImpactAnalyzerService
{
    /// <summary>
    /// Traverses the dependency graph safely with cycle detection (iterative with visited set).
    /// Masks ownerEmail on insufficient permissions. SLA: 10,000 nodes at p99 <= 15 ms.
    /// </summary>
    Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        TenantId tenant,
        Guid consentId,
        CallerSecurityContext callerContext,
        CancellationToken ct = default);

    /// <summary>
    /// Analyzes static downstream dependencies (dashboards, pipelines, services) and
    /// correlates them with runtime consumers from audit logs (who read the table in the last N days).
    /// Supports impact analysis before schema changes (breaking change risk).
    /// </summary>
    Task<TableConsumersReport> GetTableConsumersAsync(
        TableIdentifier table,
        int timeWindowDays = 30,
        CallerSecurityContext? callerContext = null,
        CancellationToken ct = default);
}

