using System.Net;
using System.Text;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Itsm;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Extensions.Tests;

/// <summary>
/// Tests for the ITSM workflow clients that remain after the consolidation (EXT-MOVE): <see cref="ServiceNowTableApiClient"/>
/// and <see cref="JiraCloudRestClient"/> (formerly in the gateway core). The former extension duplicates
/// (ServiceNowClient / JiraClient without authentication and with non-idempotent retries) were removed.
/// </summary>
public sealed class ItsmClientTests
{
    private sealed class MockHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handlerFunc) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastBody = request.Content == null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return handlerFunc(request);
        }
    }

    private static ItsmTicketRequest CreateRequest() => new(
        new TenantId("tenant-a"),
        new Sid("S-1-5-21-1234"),
        new TableIdentifier("finance", "dbo", "invoices"),
        "Need access for the annual audit",
        7,
        null,
        null);

    [Fact]
    public async Task ServiceNowClient_WithoutConfiguredBaseUrl_FailsClosedWithoutRequest()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var client = new ServiceNowTableApiClient(
            new HttpClient(handler),
            Options.Create(new GatewayOptions()),
            NullLogger<ServiceNowTableApiClient>.Instance);

        var result = await client.CreateAccessTicketAsync(CreateRequest());

        result.Success.ShouldBeFalse();
        result.TicketReference.ShouldBeNull();
        result.ErrorCode.ShouldBe("CONFIGURATION_ERROR");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task JiraClient_WithoutConfiguredBaseUrl_FailsClosedWithoutRequest()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        var client = new JiraCloudRestClient(
            new HttpClient(handler),
            Options.Create(new GatewayOptions()),
            NullLogger<JiraCloudRestClient>.Instance);

        var result = await client.CreateAccessTicketAsync(CreateRequest());

        result.Success.ShouldBeFalse();
        result.TicketReference.ShouldBeNull();
        result.ErrorCode.ShouldBe("CONFIGURATION_ERROR");
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task ServiceNowClient_SendsBasicAuthAndTableApiPayload()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"result":{"sys_id":"abc123","number":"CHG0001"}}""", Encoding.UTF8, "application/json")
        });

        var client = new ServiceNowTableApiClient(
            new HttpClient(handler),
            Options.Create(new GatewayOptions
            {
                Itsm = new ItsmOptions
                {
                    ServiceNowBaseUrl = "https://snow.corp.example",
                    ServiceNowUsername = "svc-gateway",
                    ServiceNowPassword = "pw"
                }
            }),
            NullLogger<ServiceNowTableApiClient>.Instance);

        var result = await client.CreateAccessTicketAsync(CreateRequest());

        result.Success.ShouldBeTrue();
        result.TicketReference.ShouldNotBeNull();
        result.TicketReference.TicketId.ShouldBe("CHG0001");
        var captured = handler.LastRequest;
        captured.ShouldNotBeNull();
        captured.RequestUri!.AbsoluteUri.ShouldBe("https://snow.corp.example/api/now/table/change_request");
        captured.Headers.Authorization.ShouldNotBeNull();
        captured.Headers.Authorization.Scheme.ShouldBe("Basic");
        captured.Headers.Authorization.Parameter.ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("svc-gateway:pw")));
        var body = handler.LastBody;
        body.ShouldNotBeNull();
        body.ShouldContain("\"short_description\"");
        body.ShouldContain("\"correlation_id\"");
    }

    [Fact]
    public async Task JiraClient_SendsBasicAuthAndAdfIssuePayload()
    {
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"10024","key":"SEC-882"}""", Encoding.UTF8, "application/json")
        });

        var client = new JiraCloudRestClient(
            new HttpClient(handler),
            Options.Create(new GatewayOptions
            {
                Itsm = new ItsmOptions
                {
                    JiraBaseUrl = "https://org.atlassian.net",
                    JiraEmail = "svc@org.example",
                    JiraApiToken = "jira-token"
                }
            }),
            NullLogger<JiraCloudRestClient>.Instance);

        var result = await client.CreateAccessTicketAsync(CreateRequest());

        result.Success.ShouldBeTrue();
        result.TicketReference.ShouldNotBeNull();
        result.TicketReference.TicketId.ShouldBe("SEC-882");
        var captured = handler.LastRequest;
        captured.ShouldNotBeNull();
        captured.RequestUri!.AbsoluteUri.ShouldBe("https://org.atlassian.net/rest/api/3/issue");
        captured.Headers.Authorization.ShouldNotBeNull();
        captured.Headers.Authorization.Scheme.ShouldBe("Basic");
        captured.Headers.Authorization.Parameter.ShouldBe(Convert.ToBase64String(Encoding.UTF8.GetBytes("svc@org.example:jira-token")));
        var body = handler.LastBody;
        body.ShouldNotBeNull();
        body.ShouldContain("\"fields\"");
        body.ShouldContain("\"type\":\"doc\"");
    }

    [Fact]
    public async Task ItsmClients_ServerError_IsReportedOnce_WithoutRetry()
    {
        // Ticket creation is not idempotent on the ITSM side: the clients must not retry on their own
        // (retries are owned by the outbox dispatcher in the core).
        var handler = new MockHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom")
        });

        var options = Options.Create(new GatewayOptions
        {
            Itsm = new ItsmOptions
            {
                ServiceNowBaseUrl = "https://snow.corp.example",
                JiraBaseUrl = "https://org.atlassian.net"
            }
        });

        var snow = new ServiceNowTableApiClient(new HttpClient(handler), options, NullLogger<ServiceNowTableApiClient>.Instance);
        var snowResult = await snow.CreateAccessTicketAsync(CreateRequest());
        snowResult.Success.ShouldBeFalse();
        snowResult.ErrorCode.ShouldBe("ITSM_UNAVAILABLE");
        handler.CallCount.ShouldBe(1);

        var jira = new JiraCloudRestClient(new HttpClient(handler), options, NullLogger<JiraCloudRestClient>.Instance);
        var jiraResult = await jira.CreateAccessTicketAsync(CreateRequest());
        jiraResult.Success.ShouldBeFalse();
        jiraResult.ErrorCode.ShouldBe("ITSM_UNAVAILABLE");
        handler.CallCount.ShouldBe(2);
    }
}
