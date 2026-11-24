namespace Autheris.Application.Sql.Tree;

using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Quota;

public sealed partial class GovernedTreeQueryService
{
    public Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        CancellationToken ct = default) => ExecuteAsync(principal, root, requestHeaders, operationId, observer: null, ct);

    public Task<JsonDocument> ExecuteAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        ITreeExecutionObserver? observer,
        CancellationToken ct = default)
    {
        if (!ScopeQueryExecutionGuard.TryGetCurrent(out var scopeLease) || !scopeLease.CancellationToken.CanBeCanceled)
        {
            return ExecuteScopedAsync(principal, root, requestHeaders, operationId, observer, ct);
        }

        return ExecuteLinkedAsync(principal, root, requestHeaders, operationId, observer, scopeLease.CancellationToken, ct);
    }

    private async Task<JsonDocument> ExecuteLinkedAsync(
        ClaimsPrincipal? principal,
        TreeQueryNode root,
        IReadOnlyDictionary<string, string[]>? requestHeaders,
        string operationId,
        ITreeExecutionObserver? observer,
        CancellationToken scopeCt,
        CancellationToken ct)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, scopeCt);
        return await ExecuteScopedAsync(principal, root, requestHeaders, operationId, observer, linkedCts.Token).ConfigureAwait(false);
    }

    private static int ClampScopeCommandTimeout(int configuredTimeoutSeconds) =>
        ScopeQueryExecutionGuard.TryGetCurrent(out var scopeLease)
            ? scopeLease.ClampTimeoutSeconds(configuredTimeoutSeconds)
            : configuredTimeoutSeconds;
}
