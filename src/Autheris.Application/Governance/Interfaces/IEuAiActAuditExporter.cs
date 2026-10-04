namespace Autheris.Application.Governance.Interfaces;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Model;

/// <summary>
/// F-AI-12-B: Generates tamper-evident, verifiable EU AI Act Article 10 compliance certificates.
/// </summary>
public interface IEuAiActAuditExporter
{
    Task<EuAiActArticle10Certificate> GenerateCertificateAsync(string tenantId, CancellationToken ct = default);

    string ExportCertificateToJson(EuAiActArticle10Certificate certificate);
}
