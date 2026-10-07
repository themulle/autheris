namespace Autheris.GraphQL.Subscriptions;

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Domain.Model;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class Subscription
{
    [Subscribe(With = nameof(SubscribeToTableEventsAsync))]
    public StreamCdcEvent OnTableChanged(
        [EventMessage] StreamCdcEvent message) => message;

    public async IAsyncEnumerable<StreamCdcEvent> SubscribeToTableEventsAsync(
        string table,
        string? tenantId,
        [Service] ICdcEventChannel eventChannel,
        [Service] IStreamRlsPolicyEnforcer enforcer,
        [Service] IServiceProvider services,
        ClaimsPrincipal? principal,
        [EnumeratorCancellation] CancellationToken ct)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage("Unauthorized: Realtime table subscriptions require an authenticated identity.")
                    .SetCode("AUTH_NOT_AUTHENTICATED")
                    .Build());
        }

        var callerTenant = principal.GetTenantId();
        if (!string.IsNullOrWhiteSpace(tenantId) &&
            !string.Equals(callerTenant.Value, tenantId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage($"Forbidden: Tenant mismatch. Authenticated tenant '{callerTenant.Value}' cannot subscribe to tenant '{tenantId}'.")
                    .SetCode("AUTH_TENANT_MISMATCH")
                    .Build());
        }

        if (string.IsNullOrWhiteSpace(table))
        {
            throw new GraphQLException(ErrorBuilder.New().SetMessage("A table is required.").SetCode("INVALID_QUERY").Build());
        }

        var (topic, tableId) = CdcSubscriptionGovernor.ResolveTopic(table);

        // SEC A-3: Subscriptions over HTTP SSE / multipart bypass the WebSocket interceptor, so the stream itself
        // enforces token expiry / max session lifetime (M-14) and periodic revocation checks.
        var httpContext = services.GetService<IHttpContextAccessor>()?.HttpContext;
        var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
        var authExpiresUtc = httpContext?.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        var expiry = WebSocketAuthInterceptor.ResolveSessionExpiry(
            principal, authExpiresUtc, timeProvider.GetUtcNow(), WebSocketAuthInterceptor.DefaultMaxSessionLifetime);
        var remaining = expiry - timeProvider.GetUtcNow();
        if (remaining <= TimeSpan.Zero)
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage("Unauthorized: Authentication token expired.")
                    .SetCode("AUTH_TOKEN_EXPIRED")
                    .Build());
        }

        var revocation = services.GetService<ITokenRevocationService>();
        if (revocation != null && await revocation.IsRevokedAsync(principal, ct).ConfigureAwait(false))
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage("Unauthorized: Authentication token revoked.")
                    .SetCode("AUTH_TOKEN_REVOKED")
                    .Build());
        }

        var seconds = services.GetService<IOptions<GatewayOptions>>()?.Value?.GraphQL?.SubscriptionRevalidationSeconds ?? 0;
        var interval = seconds > 0 ? TimeSpan.FromSeconds(seconds) : WebSocketAuthInterceptor.DefaultRevalidationInterval;

        using var guardCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var expiryCts = new CancellationTokenSource(remaining, timeProvider);
        using var expiryReg = expiryCts.Token.Register(static s => ((CancellationTokenSource)s!).Cancel(), guardCts);
        using var revocationTimer = revocation == null ? null : timeProvider.CreateTimer(static state =>
        {
            var (cts, svc, p) = ((CancellationTokenSource, ITokenRevocationService, ClaimsPrincipal))state!;
            _ = CheckRevocationAsync(cts, svc, p);
        }, (guardCts, revocation, principal), interval, interval);

        // GQL-3 / GQL-4: topic admission (catalog + access decision, cdc_all for admins), per-subject limit, audit.
        var governor = services.GetService<CdcSubscriptionGovernor>()
            ?? throw new GraphQLException(ErrorBuilder.New().SetMessage("Subscriptions are not available.").SetCode("UNAVAILABLE").Build());
        await using var lease = await governor.AdmitAsync(
            principal,
            topic,
            tableId,
            services.GetService<ITableAccessResolver>(),
            services.GetService<IAuditLogRepository>(),
            ct).ConfigureAwait(false);

        var sourceStream = eventChannel.SubscribeAsync(topic, guardCts.Token);
        var subscriber = principal;

        await using var enumerator = sourceStream.GetAsyncEnumerator(guardCts.Token);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (guardCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                break; // session expired or token revoked: end the stream
            }

            if (!hasNext || guardCts.IsCancellationRequested)
            {
                break;
            }

            var cdcEvent = enumerator.Current;
            var decision = await enforcer.EvaluateAndMaskAsync(cdcEvent, subscriber, ct);
            if (!decision.IsAllowed || decision.MaskedPayload == null)
            {
                continue; // Zero leakage: unauthorized events dropped
            }

            lease.CountDelivered();
            yield return new StreamCdcEvent(
                EventId: cdcEvent.EventId,
                Table: cdcEvent.Table.ToQualifiedName(),
                Operation: cdcEvent.Operation.ToString(),
                TenantId: cdcEvent.TenantId,
                PayloadJson: JsonSerializer.Serialize(decision.MaskedPayload),
                Timestamp: cdcEvent.Timestamp
            );
        }
    }

    private static async Task CheckRevocationAsync(CancellationTokenSource cts, ITokenRevocationService svc, ClaimsPrincipal principal)
    {
        try
        {
            if (!cts.IsCancellationRequested && await svc.IsRevokedAsync(principal, CancellationToken.None).ConfigureAwait(false))
            {
                cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
            // Stream already ended.
        }
        catch (Exception)
        {
            // Fail open on transient check errors; the next tick retries (same as the WebSocket watch).
        }
    }
}
