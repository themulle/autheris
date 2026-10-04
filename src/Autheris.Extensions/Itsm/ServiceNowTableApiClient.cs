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
/// Outbound REST client for ServiceNow Table API (e.g. /api/now/table/change_request).
/// Creates automated access request and recertification tickets with full justification context.
/// </summary>
public sealed class ServiceNowTableApiClient(
    HttpClient httpClient,
    IOptions<GatewayOptions> options,
    ILogger<ServiceNowTableApiClient> logger) : IItsmWorkflowClient
{
    private readonly HttpClient _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly IOptions<GatewayOptions> _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly ILogger<ServiceNowTableApiClient> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public ItsmSystemType SystemType => ItsmSystemType.ServiceNow;

    public async Task<ItsmTicketResult> CreateAccessTicketAsync(ItsmTicketRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var itsmOpts = _options.Value.Itsm;
        var baseUrl = itsmOpts.ServiceNowBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new ItsmTicketResult(false, null, "CONFIGURATION_ERROR", "ServiceNowBaseUrl is not configured.");
        }

        var table = string.IsNullOrWhiteSpace(itsmOpts.ServiceNowTable) ? "change_request" : itsmOpts.ServiceNowTable;
        var endpoint = $"{baseUrl.TrimEnd('/')}/api/now/table/{table}";

        var payload = new
        {
            short_description = $"[Autheris Access Request] {request.TargetTable}",
            description = $"Tenant: {request.Tenant.Value}\n" +
                          $"Requester SID: {request.RequesterSid.Value}\n" +
                          $"Target Table: {request.TargetTable}\n" +
                          $"Requested Duration: {request.DurationDays} days\n" +
                          $"Justification: {request.Justification}\n" +
                          $"Triage Category: {request.TriageCategory?.ToString() ?? "Standard"}\n" +
                          $"Triage Confidence: {request.TriageConfidence?.ToString("P0") ?? "N/A"}",
            urgency = "2",
            priority = "2",
            correlation_id = $"GQL-{request.Tenant.Value}-{request.RequesterSid.Value}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"
        };

        var json = JsonSerializer.Serialize(payload);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(itsmOpts.ServiceNowUsername) && !string.IsNullOrWhiteSpace(itsmOpts.ServiceNowPassword))
        {
            var authHeader = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{itsmOpts.ServiceNowUsername}:{itsmOpts.ServiceNowPassword}"));
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", authHeader);
        }

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        try
        {
            var response = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
            var responseBody = await BoundedHttpContent.ReadBoundedStringAsync(response, "ServiceNow", ct: ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var sanitizedBody = responseBody.Length > 200 ? string.Concat(responseBody.AsSpan(0, 200), "...[truncated]") : responseBody;
                _logger.LogError("ServiceNow Table API returned HTTP {StatusCode}: {Body}", (int)response.StatusCode, sanitizedBody);
                return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", $"ServiceNow error (HTTP {(int)response.StatusCode}): {sanitizedBody}");
            }

            using var doc = JsonDocument.Parse(responseBody);
            var root = doc.RootElement;
            if (root.TryGetProperty("result", out var resultElement))
            {
                var sysId = resultElement.TryGetProperty("sys_id", out var sysIdProp) ? sysIdProp.GetString() : null;
                var number = resultElement.TryGetProperty("number", out var numProp) ? numProp.GetString() : sysId;

                var ticketRef = new ItsmTicketReference(
                    ItsmSystemType.ServiceNow,
                    number ?? sysId ?? "SN-TICKET",
                    $"{baseUrl.TrimEnd('/')}/nav_to.do?uri={table}.do?sys_id={sysId}");

                _logger.LogInformation("Successfully created ServiceNow ticket '{TicketNumber}' (sys_id: {SysId}).", ticketRef.TicketId, sysId);
                return new ItsmTicketResult(true, ticketRef, null, null);

            }

            return new ItsmTicketResult(false, null, "INVALID_RESPONSE", "Missing 'result' element in ServiceNow response.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send request to ServiceNow Table API: {Message}", ex.Message);
            return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", $"ITSM_UNAVAILABLE: {ex.Message}");
        }


    }
}
