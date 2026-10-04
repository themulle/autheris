namespace Autheris.Tests.Unit.Security;

using System;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Governance.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class EuAiActArticle10ComplianceSecurityTests
{
    private readonly IDifferentialPrivacyEngine _dpEngine = Substitute.For<IDifferentialPrivacyEngine>();
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

        _dpEngine.GetBudgetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PrivacyBudget(
                ClientId: tenantId,
                TotalDailyEpsilonBudget: 10.0,
                ConsumedEpsilon: 3.5,
                LastResetUtc: DateTimeOffset.UtcNow
            ));

        var exporter = new EuAiActAuditExporter(_dpEngine, _auditSource);

        // Act
        var certificate = await exporter.GenerateCertificateAsync(tenantId);

        // Assert
        certificate.ShouldNotBeNull();
        certificate.TenantId.ShouldBe(tenantId);
        certificate.Specification.ShouldBe("EU AI Act Article 10 (Data Governance & Privacy-Preserving AI)");
        certificate.DifferentialPrivacyMetrics.TotalDailyEpsilonBudget.ShouldBe(10.0);
        certificate.DifferentialPrivacyMetrics.ConsumedEpsilon.ShouldBe(3.5);
        certificate.DifferentialPrivacyMetrics.RemainingEpsilon.ShouldBe(6.5);
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

        _dpEngine.GetBudgetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new PrivacyBudget(tenantId, 10.0, 0.0, DateTimeOffset.UtcNow));

        var exporter = new EuAiActAuditExporter(_dpEngine, _auditSource);

        // Act
        var certificate = await exporter.GenerateCertificateAsync(tenantId);

        // Assert
        certificate.AuditChainTailHash.ShouldStartWith("GENESIS_");
        certificate.IntegritySealSha256.ShouldNotBeNullOrWhiteSpace();
    }
}
