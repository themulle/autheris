namespace Autheris.Tests.Unit.Lineage;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

/// <summary>MISC-12: the seal is a keyed signature over the exact rendered text; every recipient is rendered; no signer, no claim.</summary>
public sealed class GdprAuditReportPdfExporterTests
{
    private sealed class HmacSigner(byte[] key) : IAuditAnchorSigner
    {
        public string Sign(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(HMACSHA256.HashData(key, data));

        public bool Verify(ReadOnlySpan<byte> data, string signature) => string.Equals(Sign(data), signature, StringComparison.Ordinal);
    }

    private static GdprDisclosureReport Report(int recipients, bool complete = true, IReadOnlyList<string>? reasons = null) => new(
        "finance.dbo.salary",
        "S-1-5-21-employee-42",
        new DateTimeOffset(2026, 10, 22, 8, 0, 0, TimeSpan.Zero),
        365,
        recipients,
        Enumerable.Range(1, recipients)
            .Select(i => new GdprRecipientAccessRecord($"S-1-5-21-reader-{i:D3}", "InteractiveUser", "AUDIT", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 1, ["salary"], null))
            .ToList(),
        ["PII (Personally Identifiable Information)"],
        "Art. 15(1)(c) GDPR (DSGVO)",
        complete,
        "TableLevel",
        reasons ?? []);

    private static string Pdf(GdprAuditExportResult result) => Encoding.Latin1.GetString(result.DocumentBytes);

    [Fact]
    [Trait("Category", "Security")]
    public void Seal_IsKeyedSignature_NotPlainSha256()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var exporter = new GdprAuditReportPdfExporter(new HmacSigner(key));
        var report = Report(2);

        var result = exporter.ExportReportToPdf(report);

        var text = exporter.ExportReportToAuditDocument(report);
        var plainSha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        result.AuditSeal.ShouldNotBeNull();
        result.AuditSeal.ShouldStartWith("hmac-sha256:");
        result.AuditSeal.ShouldNotContain(plainSha256);
        result.AuditSeal.ShouldBe("hmac-sha256:" + Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(text))));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Seal_ChangesWhenRecipientRemoved()
    {
        var exporter = new GdprAuditReportPdfExporter(new HmacSigner(RandomNumberGenerator.GetBytes(32)));

        var full = exporter.ExportReportToPdf(Report(3));
        var reduced = exporter.ExportReportToPdf(Report(2));

        full.AuditSeal.ShouldNotBe(reduced.AuditSeal);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void AllSixtyRecipientsRendered()
    {
        var exporter = new GdprAuditReportPdfExporter(new HmacSigner(RandomNumberGenerator.GetBytes(32)));

        var pdf = Pdf(exporter.ExportReportToPdf(Report(60)));

        for (var i = 1; i <= 60; i++)
        {
            pdf.ShouldContain($"S-1-5-21-reader-{i:D3}");
        }

        pdf.ShouldContain("/Count 2");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void NoSigner_NoTamperEvidentClaim()
    {
        var result = new GdprAuditReportPdfExporter().ExportReportToPdf(Report(1));

        result.AuditSeal.ShouldBeNull();
        var pdf = Pdf(result);
        pdf.ShouldNotContain("Tamper-Evident");
        pdf.ShouldNotContain("Audit Seal");
        pdf.ShouldContain("no integrity seal");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void IncompleteReport_PrintsNotice()
    {
        var exporter = new GdprAuditReportPdfExporter();

        var pdf = Pdf(exporter.ExportReportToPdf(Report(1, complete: false, reasons: ["The audit event cap of 100000 was reached; older recipients may be missing."])));

        pdf.ShouldContain("INCOMPLETE REPORT");
        pdf.ShouldContain("audit event cap");
    }
}
