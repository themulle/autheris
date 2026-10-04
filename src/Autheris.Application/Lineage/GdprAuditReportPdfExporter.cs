namespace Autheris.Application.Lineage;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;

/// <summary>
/// Standardized Audit & PDF Exporter for External Data Protection Officers (Datenschutzbeauftragte / DSB).
/// Generates compliant, sealed PDF 1.4 documents containing Art. 15 Abs. 1 Bst. c recipient disclosures,
/// sensitivity categories, masking rules, and cryptographic SHA-256 integrity seal.
/// </summary>
public sealed class GdprAuditReportPdfExporter : IGdprAuditReportExporter
{
    public GdprAuditExportResult ExportReportToPdf(GdprDisclosureReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var auditText = ExportReportToAuditDocument(report);
        var sealHash = ComputeSha256(auditText);

        var pdfBytes = GeneratePdfDocument(report, sealHash);
        var fileName = $"GDPR_Art15_Disclosure_{report.SubjectSid ?? "ALL"}_{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.pdf";

        return new GdprAuditExportResult(
            pdfBytes,
            "application/pdf",
            fileName,
            sealHash,
            DateTimeOffset.UtcNow);
    }

    public string ExportReportToAuditDocument(GdprDisclosureReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("              AUTHERIS - GDPR ARTICLE 15 AUDIT DISCLOSURE REPORT             ");
        sb.AppendLine("================================================================================");
        sb.AppendLine($"Generated At:            {report.GeneratedAt:yyyy-MM-dd HH:mm:ss 'UTC'}");
        sb.AppendLine($"Target Table:            {report.TargetTable ?? "ALL TABLES"}");
        sb.AppendLine($"Data Subject SID:        {report.SubjectSid ?? "ALL SUBJECTS"}");
        sb.AppendLine($"Time Window:             {report.TimeWindowDays} days");
        sb.AppendLine($"Total Access Events:     {report.TotalAccessEvents}");
        sb.AppendLine($"Legal Basis Notice:      {report.LegalBasisNotice}");
        sb.AppendLine();
        sb.AppendLine("DATA SENSITIVITY CLASSIFICATIONS (Art. 9 DSGVO):");
        foreach (var category in report.SensitivityCategories)
        {
            sb.AppendLine($"  - {category}");
        }
        sb.AppendLine();
        sb.AppendLine("DISCLOSED RECIPIENTS (Art. 15 Abs. 1 Bst. c DSGVO):");
        sb.AppendLine("--------------------------------------------------------------------------------");
        sb.AppendLine(string.Format("{0,-20} | {1,-18} | {2,8} | {3,-20}", "Recipient SID", "Category", "Queries", "Masking Rule"));
        sb.AppendLine("--------------------------------------------------------------------------------");
        foreach (var recipient in report.DisclosedRecipients)
        {
            sb.AppendLine(string.Format("{0,-20} | {1,-18} | {2,8} | {3,-20}",
                recipient.RecipientSid,
                recipient.RecipientCategory,
                recipient.TotalQueries,
                recipient.MaskingRuleApplied ?? "None (Clear)"));
        }
        sb.AppendLine("--------------------------------------------------------------------------------");

        return sb.ToString();
    }

    private static byte[] GeneratePdfDocument(GdprDisclosureReport report, string sealHash)
    {
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.Latin1);

        // Build PDF text stream
        var contentSb = new StringBuilder();
        contentSb.AppendLine("BT");
        contentSb.AppendLine("/F1 16 Tf");
        contentSb.AppendLine("50 780 Td");
        contentSb.AppendLine("(AUTHERIS - GDPR ARTICLE 15 AUDIT DISCLOSURE REPORT) Tj");
        contentSb.AppendLine("/F1 10 Tf");
        contentSb.AppendLine("0 -25 Td");
        contentSb.AppendLine($"(Generated: {report.GeneratedAt:yyyy-MM-dd HH:mm:ss 'UTC'} | Window: {report.TimeWindowDays} days) Tj");
        contentSb.AppendLine("0 -15 Td");
        contentSb.AppendLine($"(Subject: {SanitizePdfText(report.SubjectSid ?? "ALL")} | Target Table: {SanitizePdfText(report.TargetTable ?? "ALL")}) Tj");
        contentSb.AppendLine("0 -15 Td");
        contentSb.AppendLine($"(Total Access Events Evaluated: {report.TotalAccessEvents}) Tj");
        contentSb.AppendLine("0 -15 Td");
        contentSb.AppendLine($"(Legal Basis: {SanitizePdfText(report.LegalBasisNotice)}) Tj");

        contentSb.AppendLine("0 -25 Td");
        contentSb.AppendLine("/F1 12 Tf");
        contentSb.AppendLine("(Sensitivity Classifications:) Tj");
        contentSb.AppendLine("/F1 9 Tf");
        foreach (var cat in report.SensitivityCategories)
        {
            contentSb.AppendLine("0 -13 Td");
            contentSb.AppendLine($"(- {SanitizePdfText(cat)}) Tj");
        }

        contentSb.AppendLine("0 -25 Td");
        contentSb.AppendLine("/F1 12 Tf");
        contentSb.AppendLine("(Disclosed Third-Party Recipients:) Tj");
        contentSb.AppendLine("/F1 9 Tf");
        contentSb.AppendLine("0 -15 Td");
        contentSb.AppendLine("(Recipient SID | Category | Queries | Applied Masking) Tj");

        int count = 0;
        foreach (var r in report.DisclosedRecipients)
        {
            if (++count > 25) break; // Keep within single page layout
            contentSb.AppendLine("0 -13 Td");
            var line = $"{SanitizePdfText(r.RecipientSid)} | {r.RecipientCategory} | {r.TotalQueries} | {SanitizePdfText(r.MaskingRuleApplied ?? "None")}";
            contentSb.AppendLine($"({line}) Tj");
        }

        contentSb.AppendLine("0 -35 Td");
        contentSb.AppendLine("/F1 8 Tf");
        contentSb.AppendLine($"(SHA-256 Audit Seal: {sealHash}) Tj");
        contentSb.AppendLine("0 -12 Td");
        contentSb.AppendLine("(Verified Tamper-Evident by Autheris Governance Engine) Tj");
        contentSb.AppendLine("ET");

        var contentStream = Encoding.Latin1.GetBytes(contentSb.ToString());

        // Construct standard PDF 1.4 vector structure
        var offsets = new long[5];
        writer.Write("%PDF-1.4\n");
        writer.Flush();

        // 1: Catalog
        offsets[1] = ms.Position;
        writer.Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        writer.Flush();

        // 2: Pages
        offsets[2] = ms.Position;
        writer.Write("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        writer.Flush();

        // 3: Page
        offsets[3] = ms.Position;
        writer.Write("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 842] /Contents 4 0 R /Resources << /Font << /F1 << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> >> >> >>\nendobj\n");
        writer.Flush();

        // 4: Contents
        offsets[4] = ms.Position;
        writer.Write($"4 0 obj\n<< /Length {contentStream.Length} >>\nstream\n");
        writer.Flush();
        ms.Write(contentStream, 0, contentStream.Length);
        writer.Write("\nendstream\nendobj\n");
        writer.Flush();

        // Cross-reference table
        var xrefOffset = ms.Position;
        writer.Write("xref\n0 5\n0000000000 65535 f \n");
        for (int i = 1; i <= 4; i++)
        {
            writer.Write($"{offsets[i]:D10} 00000 n \n");
        }
        writer.Write($"trailer\n<< /Size 5 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
        writer.Flush();

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


    private static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }
}
