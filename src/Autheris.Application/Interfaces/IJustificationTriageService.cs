namespace Autheris.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IJustificationTriageService
{
    /// <summary>
    /// Führt Triage durch. Gewährt NIEMALS automatischen Consent außer bei explizitem LOW_SENSITIVITY Opt-In.
    /// Latenzgrenze: <= 120 ms.
    /// </summary>
    Task<JustificationTriageResult> TriageJustificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string justificationText,
        CancellationToken ct = default);
}
