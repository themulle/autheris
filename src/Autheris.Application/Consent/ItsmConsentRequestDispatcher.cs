namespace Autheris.Application.Consent;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Workflows;
using Autheris.Domain.Model;

/// <summary>Synchronous ITSM hand-off through <see cref="ItsmWorkflowDispatcher"/>.</summary>
public sealed class ItsmConsentRequestDispatcher : IConsentRequestDispatcher
{
    private readonly ItsmWorkflowDispatcher _dispatcher;

    public ItsmConsentRequestDispatcher(ItsmWorkflowDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task<ItsmTicketResult> DispatchAsync(ItsmTicketRequest request, ItsmSystemType preferredSystem, CancellationToken ct = default)
        => _dispatcher.DispatchTicketRequestAsync(request, preferredSystem, ct);
}
