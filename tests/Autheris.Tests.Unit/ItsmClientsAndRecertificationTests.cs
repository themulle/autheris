namespace Autheris.Tests.Unit;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Workflows;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Itsm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public class ItsmClientsAndRecertificationTests
{
    private sealed class DelegatingHandlerStub(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(handler(request));
        }
    }

    [Fact]
    public async Task ServiceNowTableApiClient_ShouldCreateTicketAndReturnTicketId()
    {
        // Arrange
        var mockHandler = new DelegatingHandlerStub(req =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldContain("/api/now/table/change_request");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""
                    {
                        "result": {
                            "sys_id": "sys_abc123456",
                            "number": "CHG003001"
                        }
                    }
                    """)
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var options = Options.Create(new GatewayOptions
        {
            Itsm = new ItsmOptions
            {
                ServiceNowBaseUrl = "https://instance.service-now.com",
                ServiceNowUsername = "admin",
                ServiceNowPassword = "password",
                ServiceNowTable = "change_request"
            }
        });

        var client = new ServiceNowTableApiClient(httpClient, options, NullLogger<ServiceNowTableApiClient>.Instance);
        var request = new ItsmTicketRequest(
            new TenantId("tenant-1"),
            new Sid("S-1-5-21-requester"),
            new TableIdentifier("finance", "dbo", "invoices"),
            "Need access for annual audit",
            14,
            JustificationCategory.LegitimateAudit,
            0.95);

        // Act
        var result = await client.CreateAccessTicketAsync(request);

        // Assert
        result.Success.ShouldBeTrue();
        result.TicketReference.ShouldNotBeNull();
        result.TicketReference.TicketId.ShouldBe("CHG003001");
        result.TicketReference.TicketUrl.ShouldContain("sys_abc123456");
    }

    [Fact]
    public async Task JiraCloudRestClient_ShouldCreateIssueAndReturnIssueKey()
    {
        // Arrange
        var mockHandler = new DelegatingHandlerStub(req =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri!.AbsolutePath.ShouldContain("/rest/api/3/issue");
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("""
                    {
                        "id": "10024",
                        "key": "SEC-882"
                    }
                    """)
            };
        });

        var httpClient = new HttpClient(mockHandler);
        var options = Options.Create(new GatewayOptions
        {
            Itsm = new ItsmOptions
            {
                JiraBaseUrl = "https://org.atlassian.net",
                JiraEmail = "service@org.com",
                JiraApiToken = "secret-token",
                JiraProjectKey = "SEC"
            }
        });

        var client = new JiraCloudRestClient(httpClient, options, NullLogger<JiraCloudRestClient>.Instance);
        var request = new ItsmTicketRequest(
            new TenantId("tenant-1"),
            new Sid("S-1-5-21-requester"),
            new TableIdentifier("sales", "dbo", "deals"),
            "Quarterly sales analysis",
            7,
            JustificationCategory.LegitimateAudit,
            0.9);

        // Act
        var result = await client.CreateAccessTicketAsync(request);

        // Assert
        result.Success.ShouldBeTrue();
        result.TicketReference.ShouldNotBeNull();
        result.TicketReference.TicketId.ShouldBe("SEC-882");
        result.TicketReference.TicketUrl.ShouldContain("SEC-882");
    }

    [Fact]
    public async Task ConsentRecertificationWorkflowService_ShouldScanAndEnqueueExpiringConsents()
    {
        // Arrange
        var consentRepo = Substitute.For<IConsentRepository>();
        var outboxRepo = Substitute.For<IItsmOutboxRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var options = Options.Create(new GatewayOptions());

        var consentId = Guid.NewGuid();
        var expiringConsent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("crm", "dbo", "leads"),
            GranteeSid = new Sid("S-1-5-21-lead-user"),
            TenantId = new TenantId("tenant-alpha"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1) // Expires in 24 hours
        };

        consentRepo.GetExpiringConsentsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([expiringConsent]));

        var workflow = new ConsentRecertificationWorkflowService(
            consentRepo,
            outboxRepo,
            options,
            NullLogger<ConsentRecertificationWorkflowService>.Instance,
            auditRepo);

        // Act
        var enqueuedCount = await workflow.ScanAndTriggerExpiringConsentRecertificationsAsync();

        // Assert
        enqueuedCount.ShouldBe(1);
        await outboxRepo.Received(1).EnqueueAsync(
            Arg.Is<ItsmOutboxMessage>(m => m.EventType == "ConsentRecertificationRequired" && m.RequestId == consentId.ToString()),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsentRecertificationWorkflowService_ExtendConsentExpiry_ShouldExtendAndAudit()
    {
        // Arrange
        var consentRepo = Substitute.For<IConsentRepository>();
        var outboxRepo = Substitute.For<IItsmOutboxRepository>();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var options = Options.Create(new GatewayOptions());

        var consentId = Guid.NewGuid();
        var existingConsent = new Consent
        {
            Id = consentId,
            TableId = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("crm", "dbo", "leads"),
            GranteeSid = new Sid("S-1-5-21-lead-user"),
            TenantId = new TenantId("tenant-alpha"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
            ValidTo = DateTimeOffset.UtcNow.AddHours(2)
        };

        consentRepo.GetConsentByIdAsync(consentId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Consent?>(existingConsent));

        var workflow = new ConsentRecertificationWorkflowService(
            consentRepo,
            outboxRepo,
            options,
            NullLogger<ConsentRecertificationWorkflowService>.Instance,
            auditRepo);

        // Act
        var success = await workflow.ExtendConsentExpiryAsync(
            consentId,
            TimeSpan.FromDays(30),
            new Sid("S-1-5-21-data-steward"),
            "Recertification approved after quarterly access audit.");

        // Assert
        success.ShouldBeTrue();
        await consentRepo.Received(1).ExtendConsentExpiryAsync(
            consentId,
            Arg.Is<DateTimeOffset>(d => d > DateTimeOffset.UtcNow.AddDays(29)),
            Arg.Any<CancellationToken>());
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(a => a.EventType == "CONSENT_RECERTIFIED_AND_EXTENDED"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConsentRecertificationWorkflowService_ShouldDeduplicateRecertificationTickets()
    {
        // Arrange
        var consentRepo = Substitute.For<IConsentRepository>();
        var outboxRepo = Substitute.For<IItsmOutboxRepository>();
        var options = Options.Create(new GatewayOptions());

        var consentId = Guid.NewGuid();
        var expiringList = new List<Consent>
        {
            new Consent
            {
                Id = consentId,
                TableIdentifier = new TableIdentifier("crm", "dbo", "leads"),
                GranteeSid = new Sid("S-1-5-21-lead-user"),
                TenantId = new TenantId("tenant-alpha"),
                ValidFrom = DateTimeOffset.UtcNow.AddDays(-10),
                ValidTo = DateTimeOffset.UtcNow.AddHours(2)
            }
        };

        consentRepo.GetExpiringConsentsAsync(Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(expiringList));

        outboxRepo.GetPendingMessagesAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ItsmOutboxMessage>>([]));

        var workflow = new ConsentRecertificationWorkflowService(
            consentRepo,
            outboxRepo,
            options,
            NullLogger<ConsentRecertificationWorkflowService>.Instance);

        // Act 1: First scan should dispatch 1 ticket
        var firstCount = await workflow.ScanAndTriggerExpiringConsentRecertificationsAsync();

        // Act 2: Second scan within 24h should skip duplicate
        var secondCount = await workflow.ScanAndTriggerExpiringConsentRecertificationsAsync();

        // Assert
        firstCount.ShouldBe(1);
        secondCount.ShouldBe(0);
        await outboxRepo.Received(1).EnqueueAsync(Arg.Any<ItsmOutboxMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SqliteGovernanceRepository_GetExpiringConsentsAsync_ShouldReadStringEnumsCorrectly()
    {
        // Arrange: Real in-memory SQLite database
        using var repo = new Autheris.Infrastructure.Persistence.SqliteGovernanceRepository(new StubEpochValidationService());

        var table = new TableIdentifier("crm", "dbo", "customers");
        var tableEntity = new Table
        {
            SourceName = "crm",
            SchemaName = "dbo",
            TableName = "customers",
            DataSourceType = DataSourceType.Sql
        };
        var tableMeta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = table,
            Table = tableEntity,
            Columns = []
        });

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = tableMeta.Table.Id,
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-user-123"),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddHours(4),
            IsRevoked = false,
            TenantId = new TenantId("tenant-primary")
        };

        await repo.CreateConsentAsync(consent);

        // Act
        var expiring = await repo.GetExpiringConsentsAsync(DateTimeOffset.UtcNow.AddDays(1));

        // Assert
        expiring.ShouldNotBeEmpty();
        var fetched = expiring.ShouldHaveSingleItem();
        fetched.Id.ShouldBe(consent.Id);
        fetched.Effect.ShouldBe(ConsentEffect.Allow);
        fetched.GranteeType.ShouldBe(GranteeType.User);
        fetched.GranteeSid.ShouldBe(new Sid("S-1-5-21-user-123"));
    }

    private sealed class StubEpochValidationService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long cachedEpoch, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(1L);

        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(new Dictionary<TableIdentifier, long>());
    }
}
