namespace Autheris.Application.Workflows;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

public sealed class ItsmWorkflowDispatcher
{
    private readonly IEnumerable<IItsmWorkflowClient> _clients;
    private readonly IOptions<GatewayOptions>? _options;
    private readonly ILogger<ItsmWorkflowDispatcher> _logger;

    public ItsmWorkflowDispatcher(
        IEnumerable<IItsmWorkflowClient> clients,
        ILogger<ItsmWorkflowDispatcher> logger,
        IOptions<GatewayOptions>? options = null)
    {
        _clients = clients;
        _logger = logger;
        _options = options;
    }

    public async Task<ItsmTicketResult> DispatchTicketRequestAsync(
        ItsmTicketRequest request,
        ItsmSystemType preferredSystem = ItsmSystemType.ServiceNow,
        CancellationToken ct = default)
    {
        var client = _clients.FirstOrDefault(c => c.SystemType == preferredSystem)
            ?? _clients.FirstOrDefault();

        if (client == null)
        {
            if (_options?.Value.AreExternalSystemsMockedIfUnreachable == true)
            {
                _logger.LogWarning("[INSECURE GETTING STARTED] No ITSM client configured for {System}; returning mock ticket.", preferredSystem);
                var mockTicketId = $"MOCK-{preferredSystem.ToString().ToUpperInvariant()}-{Guid.NewGuid():N}"[..18];
                return new ItsmTicketResult(true, new ItsmTicketReference(preferredSystem, mockTicketId, $"https://mock-itsm.local/tickets/{mockTicketId}"), null, null);
            }

            _logger.LogError("No ITSM workflow client registered for system {System}", preferredSystem);
            return new ItsmTicketResult(false, null, "ITSM_UNAVAILABLE", $"No ITSM client configured for {preferredSystem}.");
        }

        try
        {
            var result = await client.CreateAccessTicketAsync(request, ct).ConfigureAwait(false);
            if (!result.Success && _options?.Value.AreExternalSystemsMockedIfUnreachable == true)
            {
                _logger.LogWarning("[INSECURE GETTING STARTED] External ITSM call failed ({Error}); falling back to mock ticket.", result.ErrorMessage);
                var mockTicketId = $"MOCK-{preferredSystem.ToString().ToUpperInvariant()}-{Guid.NewGuid():N}"[..18];
                return new ItsmTicketResult(true, new ItsmTicketReference(preferredSystem, mockTicketId, $"https://mock-itsm.local/tickets/{mockTicketId}"), null, null);
            }
            return result;
        }
        catch (Exception ex) when (_options?.Value.AreExternalSystemsMockedIfUnreachable == true)
        {
            _logger.LogWarning(ex, "[INSECURE GETTING STARTED] External ITSM call threw exception; falling back to mock ticket.");
            var mockTicketId = $"MOCK-{preferredSystem.ToString().ToUpperInvariant()}-{Guid.NewGuid():N}"[..18];
            return new ItsmTicketResult(true, new ItsmTicketReference(preferredSystem, mockTicketId, $"https://mock-itsm.local/tickets/{mockTicketId}"), null, null);
        }
    }
}
