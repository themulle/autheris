namespace Autheris.Application.Interfaces;

using System;
using Autheris.Domain.Model;

public sealed record GdprAuditExportResult(
    byte[] DocumentBytes,
    string ContentType,
    string FileName,
    string Sha256AuditSeal,
    DateTimeOffset ExportedAt
);

public interface IGdprAuditReportExporter
{
    GdprAuditExportResult ExportReportToPdf(GdprDisclosureReport report);
    string ExportReportToAuditDocument(GdprDisclosureReport report);
}
