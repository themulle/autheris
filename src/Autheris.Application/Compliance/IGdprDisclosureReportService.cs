namespace Autheris.Application.Compliance;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IGdprDisclosureReportService
{
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
