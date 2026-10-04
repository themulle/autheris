namespace Autheris.Extensions.Itsm;

using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Autheris.Domain.Interfaces;

/// <summary>
/// Outbound REST client for Jira Cloud REST API v3 (/rest/api/3/issue).
/// Creates structured access governance issues with project key, summary, and description.
/// </summary>
public sealed class JiraCloudRestClient(
    HttpClient httpClient,
    IOptions<GatewayOptions> options,
    ILogger<JiraCloudRestClient> logger) : IItsmWorkflowClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IOptions<GatewayOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<JiraCloudRestClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public ItsmSystemType SystemType => ItsmSystemType.Jira;

    public async Task<ItsmTicketResult> CreateAccessTicketAsync(ItsmTicketRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var itsmOpts = _options.Value.Itsm;
        var baseUrl = itsmOpts.JiraBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ItsmTicketResult(false, null, "CONFIGURATION_ERROR", "JiraBaseUrl is not configured.");
        }

        var endpoint = $"{baseUrl.TrimEnd('/')}/rest/api/3/issue";
        var projectKey = string.IsNullOrWhiteSpace(itsmOpts.JiraProjectKey) ? "SEC" : itsmOpts.JiraProjectKey;
        var issueType = string.IsNullOrWhiteSpace(itsmOpts.JiraIssueType) ? "Task" : itsmOpts.JiraIssueType;

        var descriptionText = $"Requester SID: {request.RequesterSid.Value}\n" +
                              $"Tenant: {request.Tenant.Value}\n" +
                              $"Target Table: {request.TargetTable}\n" +
                              $"Requested Duration: {request.DurationDays} days\n" +
                              $"Justification: {request.Justification}\n" +
                              $"Triage Category: {request.TriageCategory?.ToString() ?? "Standard"}\n" +
                              $"Triage Confidence: {request.TriageConfidence?.ToString("P0") ?? "N/A"}";

        // Jira Cloud v3 ADF (Atlassian Document Format) or simplified content
        var payload = new
        {
            fields = new
            {
                project = new { key = projectKey },
                summary = $"[Autheris Access Request] {request.TargetTable} for {request.RequesterSid.Value}",
                description = new
                {
                    type = "doc",
                    version = 1,
                    content = new object[]
                    {
                        new
                        {
                            type = "paragraph",
                            content = new object[]
                            {
                                new { type = "text", text = descriptionText }
                            }
                        }
                    }
                },
                issuetype = new { name = issueType }
            }
        };

        var json = JsonSerializer.Serialize(payload);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(itsmOpts.JiraEmail) && !string.IsNullOrWhiteSpace(itsmOpts.JiraApiToken))
        {
            var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{itsmOpts.JiraEmail}:{itsmOpts.JiraApiToken}"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        }

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var response = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
            var responseBody = await BoundedHttpContent.ReadBoundedStringAsync(response, "Jira", ct: ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var sanitizedBody = responseBody.Length > 200 ? string.Concat(responseBody.AsSpan(0, 200), "...[truncated]") : responseBody;
                _logger.LogError("Jira Cloud REST API returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, sanitizedBody);
                return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", $"Jira error (HTTP {(int)response.StatusCode}): {sanitizedBody}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            var issueKey = root.TryGetProperty("key", out var keyProp) ? keyProp.GetString() : null;
            var issueId = root.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;

            var ticketRef = new ItsmTicketReference(
                ItsmSystemType.Jira,
                issueKey ?? issueId ?? "JIRA-TICKET",
                $"{baseUrl.TrimEnd('/')}/browse/{issueKey ?? issueId}");

            _logger.LogInformation("Successfully created Jira issue '{IssueKey}' (id: {IssueId}).", ticketRef.TicketId, issueId);
            return new ItsmTicketResult(true, ticketRef, null, null);

        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send request to Jira Cloud REST API: {Message}", ex.Message);
            return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", $"ITSM_UNAVAILABLE: {ex.Message}");
        }


    }
}
