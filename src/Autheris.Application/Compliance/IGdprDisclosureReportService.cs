namespace Autheris.Application.Compliance;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IGdprDisclosureReportService
{
    /// <summary>
    /// Generates a GDPR Art. 15 disclosure report (Right of Access) listing all recipients and recipient categories
    /// that queried data for a table or data subject through the gateway within the specified time window.
    /// </summary>
    Task<GdprDisclosureReport> GetGdprDataDisclosureReportAsync(
        TableIdentifier? table,
        Sid? subjectSid,
        int timeWindowDays = 365,
        CallerSecurityContext? callerContext = null,
        CancellationToken ct = default);
}
