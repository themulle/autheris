namespace Autheris.Tests.Integration.TestSupport;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.GraphQL.Catalog;
using HotChocolate.Execution;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Fixture readiness for tests that write the catalog after start-up: raise the catalog change, then wait for the
/// rebuilt executor to be registered. HotChocolate swaps the executor in asynchronously, so requests sent before the
/// swap would hit the previous schema. No request is retried and nothing sleeps; the wait is driven by executor events.
/// </summary>
internal static class GraphQlSchemaReadiness
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task AwaitCatalogSchemaAsync(
        IServiceProvider services,
        IReadOnlyCollection<string> requiredQueryFields,
        TimeSpan timeout)
    {
        var module = services.GetRequiredService<CatalogGraphQlTypeModule>();
        var provider = services.GetRequiredService<IRequestExecutorProvider>();
        var events = services.GetRequiredService<IRequestExecutorEvents>();

        // Subscribe before raising the change so no Created event is missed.
        var created = new SemaphoreSlim(0);
        using var subscription = events.Subscribe(new CreatedObserver(created));
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await module.CheckForCatalogChangesAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"The GraphQL catalog check did not finish within {timeout.TotalSeconds:0} s.");
        }

        List<string> missing;
        while (true)
        {
            IRequestExecutor executor;
            try
            {
                executor = await provider.GetExecutorAsync(null, cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"The GraphQL executor was not built within {timeout.TotalSeconds:0} s.");
            }

            var fields = executor.Schema.QueryType.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            missing = requiredQueryFields.Where(f => !fields.Contains(f)).ToList();
            if (missing.Count == 0)
            {
                return;
            }

            try
            {
                await created.WaitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException(
                    $"The GraphQL schema did not contain the catalog fields [{string.Join(", ", missing)}] within {timeout.TotalSeconds:0} s.");
            }
        }
    }

    private sealed class CreatedObserver(SemaphoreSlim created) : IObserver<RequestExecutorEvent>
    {
        public void OnNext(RequestExecutorEvent value)
        {
            if (value.Type == RequestExecutorEventType.Created)
            {
                created.Release();
            }
        }

        public void OnError(Exception error) { }

        public void OnCompleted() { }
    }
}
