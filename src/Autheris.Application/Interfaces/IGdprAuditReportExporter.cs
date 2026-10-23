namespace Autheris.Application.Interfaces;

using System;
using Autheris.Domain.Model;

/// <param name="AuditSeal">
/// Keyed signature over the rendered report text (<c>hmac-sha256:&lt;sig&gt;</c>); <c>null</c> when no audit signer is configured
/// (the document then makes no tamper-evidence claim).
/// </param>
public sealed record GdprAuditExportResult(
    byte[] DocumentBytes,
    string ContentType,
    string FileName,
    string? AuditSeal,
    DateTimeOffset ExportedAt
);

public interface IGdprAuditReportExporter
{
    GdprAuditExportResult ExportReportToPdf(GdprDisclosureReport report);
    string ExportReportToAuditDocument(GdprDisclosureReport report);
}
