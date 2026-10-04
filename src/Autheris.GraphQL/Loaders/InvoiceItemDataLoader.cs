using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using GreenDonut;
using Microsoft.AspNetCore.Http;

namespace Autheris.GraphQL.Loaders;

public sealed class InvoiceItemDataLoader : BatchDataLoader<string, List<InvoiceItemRecord>>
{
    private readonly IGatewayExecutionService _executionService;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public InvoiceItemDataLoader(
        IGatewayExecutionService executionService,
        IHttpContextAccessor httpContextAccessor,
        IBatchScheduler batchScheduler,
        DataLoaderOptions? options = null)
        : base(batchScheduler, options ?? new DataLoaderOptions())
    {
        _executionService = executionService;
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<IReadOnlyDictionary<string, List<InvoiceItemRecord>>> LoadBatchAsync(
        IReadOnlyList<string> keys,
        CancellationToken cancellationToken)
    {
        var principal = _httpContextAccessor?.HttpContext?.User ?? new ClaimsPrincipal();
        return await _executionService.LoadInvoiceItemsBatchAsync(principal, keys, cancellationToken);
    }
}
