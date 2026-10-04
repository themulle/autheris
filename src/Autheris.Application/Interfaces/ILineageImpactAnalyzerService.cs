namespace Autheris.Application.Interfaces;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface ILineageImpactAnalyzerService
{
    /// <summary>
    /// Traversiert den Abhängigkeitsgraphen zyklensicher (iterativ mit visited-Set).
    /// Maskiert ownerEmail bei unzureichender Berechtigung. SLA: 10.000 Knoten in p99 <= 15 ms.
    /// </summary>
    Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        TenantId tenant,
        Guid consentId,
        CallerSecurityContext callerContext,
        CancellationToken ct = default);

    /// <summary>
    /// Analysiert statische Downstream-Abhängigkeiten (Dashboards, Pipelines, Services) und
    /// verknüpft sie mit Laufzeit-Konsumenten aus den Audit-Logs (wer hat Tabelle in den letzten N Tagen gelesen?).
    /// Dient der Impact-Analyse vor Schema-Änderungen (Breaking Change Risk).
    /// </summary>
    Task<TableConsumersReport> GetTableConsumersAsync(
        TableIdentifier table,
        int timeWindowDays = 30,
        CallerSecurityContext? callerContext = null,
        CancellationToken ct = default);

    /// <summary>
    /// Erstellt eine DSGVO-Art.-15-Auskunft (Right of Access) über alle Empfänger und Kategorien von Empfängern,
    /// die im angegebenen Zeitraum Daten einer Tabelle oder eines Betroffenen über das Gateway abgefragt haben.
    /// </summary>
    Task<GdprDisclosureReport> GetGdprDataDisclosureReportAsync(
        TableIdentifier? table,
        Sid? subjectSid,
        int timeWindowDays = 365,
        CallerSecurityContext? callerContext = null,
        CancellationToken ct = default);
}

