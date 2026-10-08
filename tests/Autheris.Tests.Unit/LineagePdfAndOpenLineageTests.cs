namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Lineage;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lineage;
using Autheris.Infrastructure.Lineage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class LineagePdfAndOpenLineageTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public void GdprAuditReportPdfExporter_ShouldGenerateValidPdfDocumentAndAuditSeal()
    {
        // Arrange
        var exporter = new GdprAuditReportPdfExporter();
        var report = new GdprDisclosureReport(
            TargetTable: "finance.dbo.salary",
            SubjectSid: "S-1-5-21-employee-42",
            GeneratedAt: DateTimeOffset.UtcNow,
            TimeWindowDays: 365,
            TotalAccessEvents: 3,
            DisclosedRecipients:
            [
                new GdprRecipientAccessRecord("S-1-5-21-hr-manager", "InteractiveUser", "AUDIT", DateTimeOffset.UtcNow.AddDays(-10), DateTimeOffset.UtcNow, 2, ["salary", "iban"], "iban: MASK_IBAN"),
                new GdprRecipientAccessRecord("SP-payroll-svc", "ServicePrincipal", "PAYROLL", DateTimeOffset.UtcNow.AddDays(-5), DateTimeOffset.UtcNow, 1, ["salary"], null)
            ],

            SensitivityCategories:
            [
                "GDPR_ARTICLE_9 (Special Category: Health/Biometric/Financial Data)",
                "PII (Personally Identifiable Information)"
            ],
            LegalBasisNotice: "Art. 15(1)(c) GDPR (DSGVO)"
        );

        // Act
        var result = exporter.ExportReportToPdf(report);

        // Assert
        result.ShouldNotBeNull();
        result.ContentType.ShouldBe("application/pdf");
        result.FileName.ShouldStartWith("GDPR_Art15_Disclosure_");
        result.FileName.ShouldEndWith(".pdf");
        result.Sha256AuditSeal.ShouldNotBeNullOrWhiteSpace();
        result.DocumentBytes.Length.ShouldBeGreaterThan(100);

        // Validate PDF 1.4 header and trailer
        var pdfAscii = Encoding.ASCII.GetString(result.DocumentBytes);
        pdfAscii.ShouldStartWith("%PDF-1.4");
        pdfAscii.ShouldContain("%%EOF");
        pdfAscii.ShouldContain(result.Sha256AuditSeal);
    }

    [Fact]
    public async Task OpenLineageClient_ShouldPushEventToEndpoint()
    {
        // Arrange
        string? capturedBody = null;
        var mockHandler = new DelegatingHandlerStub(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldBe("/api/v1/lineage");
            return new HttpResponseMessage(HttpStatusCode.Created);
        });

        var httpClient = new HttpClient(mockHandler);
        var graphStore = Substitute.For<ILineageGraphStore>();
        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                OpenLineageEndpoint = "http://openlineage.internal/api/v1/lineage",
                OpenLineageApiKey = "api-token-test"
            }
        });

        var client = new OpenLineageClient(httpClient, graphStore, options, NullLogger<OpenLineageClient>.Instance);

        var runEvent = new OpenLineageRunEvent(
            EventType: "COMPLETE",
            EventTime: DateTimeOffset.UtcNow,
            Producer: "https://github.com/themulle/gql/autheris",
            SchemaUrl: "https://openlineage.io/spec/1-0-2/OpenLineage.json",
            Job: new { @namespace = "tenant-1", name = "sync-job" },
            Inputs: [new OpenLineageDataset("finance", "dbo.invoices")],
            Outputs: [new OpenLineageDataset("bi", "dashboard.sales")]
        );

        // Act
        var success = await client.PushLineageEventAsync(runEvent);

        // Assert
        success.ShouldBeTrue();
        capturedBody.ShouldNotBeNull();
        capturedBody.ShouldContain("dbo.invoices");
        capturedBody.ShouldContain("dashboard.sales");
    }
}
