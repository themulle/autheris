namespace Autheris.Application.Lineage;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Audit and PDF exporter for external Data Protection Officers (Datenschutzbeauftragte / DSB).
/// Generates PDF 1.4 documents containing the Art. 15 Abs. 1 Bst. c recipient disclosures, sensitivity categories and masking rules.
/// MISC-12: the rendered text model is the single source for both the document and the seal; the seal is a keyed signature from
/// <see cref="IAuditAnchorSigner"/> over exactly that text. Without a signer the document carries no seal and no tamper-evidence claim.
/// All recipients are rendered (paginated), and an incomplete report says so.
/// </summary>
public sealed class GdprAuditReportPdfExporter(IAuditAnchorSigner? signer = null) : IGdprAuditReportExporter
{
    /// <summary>Header and PDF prefix of a keyed seal value.</summary>
    public const string SealPrefix = "hmac-sha256:";

    private const int LinesPerPage = 48;

    public GdprAuditExportResult ExportReportToPdf(GdprDisclosureReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var lines = BuildLines(report);
        var seal = Seal(lines);
        var pdfBytes = GeneratePdfDocument(lines, seal);
        var fileName = $"GDPR_Art15_Disclosure_{SanitizeFileName(report.SubjectSid ?? "ALL")}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.pdf";

        return new GdprAuditExportResult(
            pdfBytes,
            "application/pdf",
            fileName,
            seal,
            DateTimeOffset.UtcNow);
    }

    public string ExportReportToAuditDocument(GdprDisclosureReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        return RenderText(BuildLines(report));
    }

    /// <summary>The single rendered text model (one entry per line); the document and the seal are both derived from it.</summary>
    private static List<string> BuildLines(GdprDisclosureReport report)
    {
        var lines = new List<string>
        {
            "AUTHERIS - GDPR ARTICLE 15 AUDIT DISCLOSURE REPORT",
            $"Generated At: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss 'UTC'}",
            $"Target Table: {report.TargetTable ?? "ALL TABLES"}",
            $"Data Subject SID: {report.SubjectSid ?? "ALL SUBJECTS"}",
            $"Time Window: {report.TimeWindowDays} days",
            $"Total Access Events: {report.TotalAccessEvents}",
            $"Evidence Level: {report.EvidenceLevel}",
            $"Complete: {(report.IsComplete ? "yes" : "NO")}",
            $"Legal Basis Notice: {report.LegalBasisNotice}"
        };

        if (!report.IsComplete)
        {
            lines.Add("INCOMPLETE REPORT: the evidence below is not complete.");
            foreach (var reason in report.IncompletenessReasons ?? [])
            {
                lines.Add($"  - {reason}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("DATA SENSITIVITY CLASSIFICATIONS (Art. 9 DSGVO):");
        lines.AddRange(report.SensitivityCategories.Select(c => $"  - {c}"));
        lines.Add(string.Empty);
        lines.Add("POTENTIAL RECIPIENTS (accessed tables containing the subject's data; Art. 15 Abs. 1 Bst. c DSGVO):");
        lines.Add("Recipient SID | Category | Queries | Masking Rule");
        foreach (var r in report.DisclosedRecipients)
        {
            lines.Add($"{r.RecipientSid} | {r.RecipientCategory} | {r.TotalQueries} | {r.MaskingRuleApplied ?? "None (Clear)"}");
        }

        return lines;
    }

    private static string RenderText(IReadOnlyList<string> lines) => string.Join("\n", lines) + "\n";

    private string? Seal(IReadOnlyList<string> lines)
    {
        if (signer == null)
        {
            return null;
        }

        return SealPrefix + signer.Sign(Encoding.UTF8.GetBytes(RenderText(lines)));
    }

    private static byte[] GeneratePdfDocument(IReadOnlyList<string> lines, string? seal)
    {
        var footer = seal == null
            ? new[] { "Unsigned document: no integrity seal (no audit signer configured)." }
            : new[] { $"Audit Seal ({SealPrefix.TrimEnd(':')}): {seal}", "Signed by the Autheris audit signer over the text of this report." };

        var allLines = lines.Concat([string.Empty]).Concat(footer).ToList();
        var pageLines = allLines.Chunk(LinesPerPage).ToList();
        if (pageLines.Count == 0)
        {
            pageLines.Add([]);
        }

        // Object layout: 1 Catalog, 2 Pages, 3 Font, then (Page, Contents) per page.
        var pageCount = pageLines.Count;
        var objectCount = 3 + (pageCount * 2);
        var offsets = new long[objectCount + 1];

        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1);

        void Write(string text)
        {
            writer.Write(text);
            writer.Flush();
        }

        Write("%PDF-1.4\n");

        offsets[1] = ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = ms.Position;
        var kids = string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{4 + (i * 2)} 0 R"));
        Write($"2 0 obj\n<< /Type /Pages /Kids [{kids}] /Count {pageCount} >>\nendobj\n");

        offsets[3] = ms.Position;
        Write("3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj\n");

        for (var i = 0; i < pageCount; i++)
        {
            var pageObject = 4 + (i * 2);
            var contentObject = pageObject + 1;

            var content = new StringBuilder();
            content.AppendLine("BT");
            content.AppendLine("/F1 9 Tf");
            content.AppendLine("11 TL");
            content.AppendLine("40 800 Td");
            foreach (var line in pageLines[i])
            {
                content.AppendLine($"({SanitizePdfText(line)}) '");
            }

            content.AppendLine("ET");
            var contentBytes = Encoding.Latin1.GetBytes(content.ToString());

            offsets[pageObject] = ms.Position;
            Write($"{pageObject} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 842] /Contents {contentObject} 0 R /Resources << /Font << /F1 3 0 R >> >> >>\nendobj\n");

            offsets[contentObject] = ms.Position;
            Write($"{contentObject} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n");
            ms.Write(contentBytes, 0, contentBytes.Length);
            Write("\nendstream\nendobj\n");
        }

        var xrefOffset = ms.Position;
        Write($"xref\n0 {objectCount + 1}\n0000000000 65535 f \n");
        for (var i = 1; i <= objectCount; i++)
        {
            Write($"{offsets[i]:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {objectCount + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    private static string SanitizePdfText(string text)
    {
        return text
            .Replace("\\", "\\\\")
            .Replace("(", "\\(")
            .Replace(")", "\\)")
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    private static string SanitizeFileName(string value) =>
        new(value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
}
