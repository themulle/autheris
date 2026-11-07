namespace Autheris.Tests.Unit.Security;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class EuAiActArticle10ComplianceSecurityTests
{
    private readonly IAuditChainExportSource _auditSource = Substitute.For<IAuditChainExportSource>();
    private readonly IAuditLogRepository _auditRepo = Substitute.For<IAuditLogRepository>();

    [Fact]
    public async Task GenerateCertificateAsync_ProducesTamperEvidentCertificateWithArticle10Fields()
    {
        // Arrange
        var tenantId = "tenant-fintech-01";
        var auditTailHash = "d8e8fca2dc0f896fd7cb4cb0031ba249";

        _auditSource.GetVerifiedChainAnchor()
            .Returns(new AuditChainAnchor(42L, auditTailHash, DateTimeOffset.UtcNow, "sig_abc"));

        var exporter = new EuAiActAuditExporter(global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, _auditSource);

        // Act
        var certificate = await exporter.GenerateCertificateAsync(tenantId);

        // Assert
        certificate.ShouldNotBeNull();
        certificate.TenantId.ShouldBe(tenantId);
        certificate.Specification.ShouldBe("EU AI Act Article 10 (Data Governance & Privacy-Preserving AI)");
        certificate.AuditChainTailHash.ShouldBe(auditTailHash);
        certificate.IntegritySealSha256.ShouldNotBeNullOrWhiteSpace();
        certificate.IntegritySealSha256.Length.ShouldBe(64);

        // Export to JSON check
        var json = exporter.ExportCertificateToJson(certificate);
        json.ShouldContain("EU AI Act Article 10");
        json.ShouldContain(auditTailHash);
        json.ShouldContain(certificate.IntegritySealSha256);
    }

    [Fact]
    public async Task GenerateCertificateAsync_WhenAuditTailMissing_UsesGenesisAnchorFallback()
    {
        // Arrange
        var tenantId = "tenant-health-02";

        _auditSource.GetVerifiedChainAnchor()
            .Returns((AuditChainAnchor?)null);

        var exporter = new EuAiActAuditExporter(global::Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance, _auditSource);

        // Act
        var certificate = await exporter.GenerateCertificateAsync(tenantId);

        // Assert
        certificate.AuditChainTailHash.ShouldStartWith("GENESIS_");
        certificate.IntegritySealSha256.ShouldNotBeNullOrWhiteSpace();
    }
}
