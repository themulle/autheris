namespace Autheris.Application.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

public interface IOpenJevClient
{
    Task<JustificationTriageResult> ClassifyJustificationAsync(
        TenantId tenant,
        Sid userSid,
        TableIdentifier table,
        string justificationText,
        CancellationToken ct = default);
}
