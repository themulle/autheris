namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AuditWormExportTests
{
    private sealed class MockHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }

    [Fact]
    public async Task AuditWormExportService_ShouldExportLocalWormArchiveSuccessfully()
    {
        // Arrange
        var tempDir = Path.Combine(AppContext.BaseDirectory, "worm_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var auditRepo = Substitute.For<IAuditLogRepository>();
            auditRepo.VerifyAuditHashChainAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

            var now = DateTimeOffset.UtcNow;
            var entries = new List<AuditLogEntry>
            {
                new AuditLogEntry
                {
                    OccurredAt = now.AddHours(-2),
                    EventType = "QUERY_EXECUTION",
                    ActorSid = new Sid("S-1-5-21-999"),
                    TargetTable = "sales.orders",
                    Decision = "ALLOW",
                    TraceId = "trace-1",
                    DetailsJson = "{}",
                    PrevHash = "GENESIS_0000000000000000000000000000000000000000000000000000000000000000",
                    EntryHash = "HASH_AAAA111122223333444455556666777788889999000011112222333344445555"
                },
                new AuditLogEntry
                {
                    OccurredAt = now.AddHours(-1),
                    EventType = "CONSENT_APPROVED",
                    ActorSid = new Sid("S-1-5-21-888"),
                    TargetTable = "sales.orders",
                    Decision = "ALLOW",
                    TraceId = "trace-2",
                    DetailsJson = "{}",
                    PrevHash = "HASH_AAAA111122223333444455556666777788889999000011112222333344445555",
                    EntryHash = "HASH_BBBB111122223333444455556666777788889999000011112222333344445555"
                }
            };

            auditRepo.QueryAuditLogsAsync(Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<AuditLogEntry>>(entries));

            var options = Options.Create(new GatewayOptions
            {
                Audit = new AuditOptions
                {
                    Worm = new WormAuditOptions
                    {
                        Enabled = true,
                        StorageType = "Local",
                        ExportPath = tempDir,
                        RetentionDays = 365
                    }
                }
            });

            var httpClient = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
            var exporter = new AuditWormExportService(auditRepo, options, httpClient, NullLogger<AuditWormExportService>.Instance);

            // Act
            var result = await exporter.ExportAuditSnapshotAsync(now.AddHours(-3), now);

            // Assert
            result.Success.ShouldBeTrue();
            result.RecordCount.ShouldBe(2);
            result.RootHash.ShouldBe("GENESIS_0000000000000000000000000000000000000000000000000000000000000000");
            result.FinalHash.ShouldBe("HASH_BBBB111122223333444455556666777788889999000011112222333344445555");
            result.ChecksumSha256.ShouldNotBeNullOrWhiteSpace();
            File.Exists(result.DestinationLocation).ShouldBeTrue();

            using var doc = JsonDocument.Parse(result.ManifestJson);
            doc.RootElement.GetProperty("recordCount").GetInt32().ShouldBe(2);
            doc.RootElement.GetProperty("complianceRegulation").GetString()!.ShouldContain("SEC-Rule-17a-4");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                // Clear ReadOnly attributes before deleting
                foreach (var file in Directory.GetFiles(tempDir))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public async Task AuditWormExportService_ShouldAbortWhenHashChainIsCompromised()
    {
        // Arrange
        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.VerifyAuditHashChainAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(false));

        var options = Options.Create(new GatewayOptions());
        var httpClient = new HttpClient(new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var exporter = new AuditWormExportService(auditRepo, options, httpClient, NullLogger<AuditWormExportService>.Instance);

        // Act
        var result = await exporter.ExportAuditSnapshotAsync(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage!.ShouldContain("tampering detected");
    }

    [Fact]
    public async Task AuditWormExportService_ShouldSendS3ObjectLockHeaders()
    {
        // Arrange
        var putRequests = new List<HttpRequestMessage>();
        var handler = new MockHttpMessageHandler(req =>
        {
            putRequests.Add(req);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var auditRepo = Substitute.For<IAuditLogRepository>();
        auditRepo.VerifyAuditHashChainAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(true));

        var now = DateTimeOffset.UtcNow;
        var entries = new List<AuditLogEntry>
        {
            new AuditLogEntry
            {
                OccurredAt = now.AddHours(-1),
                EventType = "QUERY_EXECUTION",
                ActorSid = new Sid("S-1-5-21-999"),
                TargetTable = "sales.orders",
                Decision = "ALLOW",
                TraceId = "trace-1",
                DetailsJson = "{}",
                PrevHash = "PREV_HASH",
                EntryHash = "ENTRY_HASH"
            }
        };

        auditRepo.QueryAuditLogsAsync(Arg.Any<string?>(), Arg.Any<Sid?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<int>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditLogEntry>>(entries));

        var options = Options.Create(new GatewayOptions
        {
            Audit = new AuditOptions
            {
                Worm = new WormAuditOptions
                {
                    Enabled = true,
                    StorageType = "S3",
                    S3Endpoint = "http://minio:9000",
                    S3Bucket = "worm-bucket",
                    S3Prefix = "audit-lock/",
                    S3AccessKey = "AKIAEXAMPLEKEY",
                    S3SecretKey = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY",
                    ObjectLockMode = "COMPLIANCE",
                    EnforceObjectLock = true,
                    RetentionDays = 365
                }
            }
        });

        var httpClient = new HttpClient(handler);
        var exporter = new AuditWormExportService(auditRepo, options, httpClient, NullLogger<AuditWormExportService>.Instance);

        // Act
        var result = await exporter.ExportAuditSnapshotAsync(now.AddHours(-2), now);

        // Assert
        result.Success.ShouldBeTrue();
        putRequests.Count.ShouldBe(2); // payload + manifest

        foreach (var req in putRequests)
        {
            req.Method.ShouldBe(HttpMethod.Put);
            req.Headers.Contains("x-amz-object-lock-mode").ShouldBeTrue();
            req.Headers.GetValues("x-amz-object-lock-mode").First().ShouldBe("COMPLIANCE");
            req.Headers.Contains("x-amz-object-lock-retain-until-date").ShouldBeTrue();
            req.Headers.Contains("Authorization").ShouldBeTrue();
            req.Headers.GetValues("Authorization").First().ShouldStartWith("AWS4-HMAC-SHA256");
        }
    }
}
