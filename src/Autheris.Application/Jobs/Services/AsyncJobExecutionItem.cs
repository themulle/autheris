namespace Autheris.Application.Jobs.Services;

using System.Security.Claims;
using System.Threading;
using Autheris.Domain.Model;

public sealed record AsyncJobExecutionItem(
    AsyncJobDescriptor Job,
    AsyncQueryJobRequest Request,
    ClaimsPrincipal User,
    CancellationTokenSource Cts);
