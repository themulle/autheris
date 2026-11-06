namespace Autheris.Application.Runtime.Jobs.Services;

using System.Security.Claims;
using System.Threading;
using Autheris.Domain.Model;

public sealed record AsyncJobExecutionItem(
    AsyncJobDescriptor Job,
    AsyncQueryJobRequest Request,
    ClaimsPrincipal User,
    CancellationTokenSource Cts,
    // Issue #447 (SEC-ATR-05): the ingress of the submitting request, carried with the queued item; None means the worker scope stays unset and is denied.
    Autheris.Domain.Options.DataSources.DataSourceTransportFlags Ingress = Autheris.Domain.Options.DataSources.DataSourceTransportFlags.None);
