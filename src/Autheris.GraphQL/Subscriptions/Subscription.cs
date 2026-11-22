namespace Autheris.GraphQL.Subscriptions;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
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
                    .SetMessage("Forbidden: Tenant mismatch.")
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
        var now = timeProvider.GetUtcNow();
        if (Autheris.Domain.Identity.GroupResolutionClaims.IsExpired(principal, now) ||
            Autheris.Domain.Identity.GroupResolutionClaims.IsMarkedDegraded(principal))
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage("Unauthorized: Group resolution expired or degraded.")
                    .SetCode("AUTH_GROUPS_DEGRADED")
                    .Build());
        }

        var authExpiresUtc = httpContext?.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc;
        var expiry = WebSocketAuthInterceptor.ResolveSessionExpiry(
            principal, authExpiresUtc, now, WebSocketAuthInterceptor.DefaultMaxSessionLifetime);
        if (Autheris.Domain.Identity.GroupResolutionClaims.ReadExpiresAt(principal) is { } groupExpiry && groupExpiry < expiry)
        {
            expiry = groupExpiry;
        }

        var remaining = expiry - now;
        if (remaining <= TimeSpan.Zero)
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage("Unauthorized: Authentication token expired.")
                    .SetCode("AUTH_TOKEN_EXPIRED")
                    .Build());
        }

        var revocation = services.GetService<ITokenRevocationService>();
        var revokedAtStart = false;
        if (revocation != null)
        {
            try
            {
                revokedAtStart = await revocation.IsRevokedAsync(principal, ct).ConfigureAwait(false);
            }
            catch (TokenRevocationUnavailableException)
            {
                // SR-L-10: fail closed when the revocation status cannot be determined.
                throw new GraphQLException(
                    ErrorBuilder.New()
                        .SetMessage("Service unavailable: token revocation status could not be determined.")
                        .SetCode("TOKEN_REVOCATION_UNAVAILABLE")
                        .Build());
            }
        }

        if (revokedAtStart)
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
        var watchPolicy = RevocationWatchPolicy.From(services.GetService<IOptions<RevocationWatchOptions>>());
        var failureCounter = new RevocationFailureCounter(watchPolicy);
        ILogger? watchLogger = services.GetService<ILogger<Subscription>>();

        var epochProvider = services.GetService<Autheris.Application.Governance.Caching.GovernanceEpochProvider>();
        var tenantClaim = Autheris.Domain.Tenancy.TenantClaimReader.Read(principal);
        TenantId? subTenant = tenantClaim.Kind == Autheris.Domain.Tenancy.ClaimTenantKind.Valid ? (TenantId?)tenantClaim.Tenant : null;
        long? observedEpoch = epochProvider != null && subTenant is { } validTenant
            ? await epochProvider.GetEpochAsync(validTenant, ct).ConfigureAwait(false)
            : (long?)null;
        var epochLock = new Lock();

        using var epochAdvanceSubscription = epochProvider?.OnTenantEpochAdvanced(changedTenant =>
        {
            if (subTenant is null || string.Equals(changedTenant, subTenant.Value.Value, StringComparison.OrdinalIgnoreCase))
            {
                _ = RevalidateSubscriptionAccessAsync(guardCts, services, principal, subTenant, tableId, watchLogger);
            }
        });

        var hasGroupExpiry = Autheris.Domain.Identity.GroupResolutionClaims.ReadExpiresAt(principal) is not null;
        using var revocationTimer = (revocation == null && (epochProvider == null || subTenant is null) && !hasGroupExpiry) ? null : timeProvider.CreateTimer(static state =>
        {
            var (cts, svc, epProvider, tenantOpt, p, tableOpt, counter, logger, lastEpochBox, syncLock, sProvider, tp) =
                ((CancellationTokenSource, ITokenRevocationService?, Autheris.Application.Governance.Caching.GovernanceEpochProvider?, TenantId?, ClaimsPrincipal, TableIdentifier?, RevocationFailureCounter, ILogger?, long[], Lock, IServiceProvider, TimeProvider))state!;
            if (!CheckPrincipalExpiry(cts, p, tp))
            {
                return;
            }

            if (svc != null)
            {
                _ = CheckRevocationAsync(cts, svc, p, counter, logger);
            }

            if (epProvider != null && tenantOpt is { } t)
            {
                _ = PollEpochChangeAsync(cts, epProvider, t, sProvider, p, tableOpt, lastEpochBox, syncLock, logger);
            }
        }, (guardCts, revocation, epochProvider, subTenant, principal, tableId, failureCounter, watchLogger, new[] { observedEpoch ?? 0L }, epochLock, services, timeProvider), interval, interval);

        // GQL-3 / GQL-4: topic admission (catalog + access decision, cdc_all for admins), per-subject limit, audit.
        var governor = services.GetService<CdcSubscriptionGovernor>()
            ?? throw new GraphQLException(ErrorBuilder.New().SetMessage("Subscriptions are not available.").SetCode("UNAVAILABLE").Build());
        CdcSubscriptionGovernor.Lease lease;
        try
        {
            lease = await governor.AdmitAsync(
                principal,
                topic,
                tableId,
                services.GetService<ITableAccessResolver>(),
                services.GetRequiredService<IAuditLogRepository>(),
                ct,
                guardCts,
                services.GetService<Autheris.Application.DataSources.IDataSourceTransportGate>()).ConfigureAwait(false);
        }
        catch (Autheris.Domain.Exceptions.TransportNotAllowedException)
        {
            throw new GraphQLException(
                ErrorBuilder.New()
                    .SetMessage("Access to the data source is not allowed over this transport.")
                    .SetCode(Autheris.Domain.Exceptions.TransportNotAllowedException.Code)
                    .Build());
        }

        await using var leaseDisposer = lease;

        var sourceStream = eventChannel.SubscribeAsync(topic, guardCts.Token);

        // 99 I1 (D-A13): MSSQL-tracked tables are read from the durable stream through the shared tail hub; other sources keep using the channel.
        var streamStore = services.GetService<ICdcStreamStore>();
        var tailHub = services.GetService<ICdcStreamTailHub>();
        if (streamStore != null && tailHub != null)
        {
            var streamIds = await CdcStreamEventSource.ResolveStreamsAsync(streamStore, topic, CdcSubscriptionGovernor.AllTopic, ct).ConfigureAwait(false);
            if (streamIds.Count > 0)
            {
                sourceStream = CdcStreamEventSource.MergeAsync(
                    sourceStream, streamIds, tailHub,
                    services.GetService<IOptions<GatewayOptions>>()?.Value?.CdcStream?.SubscriberBufferSize ?? 1000, guardCts.Token);
            }
        }

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
            catch (CdcResyncRequiredException ex)
            {
                // The subscriber fell behind the stream (full buffer): end explicitly instead of silently losing events.
                throw new GraphQLException(
                    ErrorBuilder.New()
                        .SetMessage("The subscription fell behind the change stream; resubscribe.")
                        .SetCode(CdcResyncRequiredException.Code)
                        .SetExtension("lastDeliveredOffset", ex.LastDeliveredOffset)
                        .Build());
            }

            if (!hasNext || guardCts.IsCancellationRequested)
            {
                break;
            }

            var cdcEvent = enumerator.Current;
            var decision = await enforcer.EvaluateAndMaskAsync(cdcEvent, subscriber, ct);
            if (!decision.IsAllowed || decision.MaskedPayload == null)
            {
                if (!string.Equals(decision.FilterReason, "Filtered by row-level security", StringComparison.Ordinal) &&
                    (decision.FilterReason?.Contains("access", StringComparison.OrdinalIgnoreCase) == true ||
                     decision.FilterReason?.Contains("consent", StringComparison.OrdinalIgnoreCase) == true ||
                     decision.FilterReason?.Contains("expired", StringComparison.OrdinalIgnoreCase) == true ||
                     decision.FilterReason?.Contains("Token", StringComparison.OrdinalIgnoreCase) == true))
                {
                    break;
                }

                continue; // Zero leakage: row-filtered events dropped
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

    internal static async Task CheckRevocationAsync(
        CancellationTokenSource cts,
        ITokenRevocationService svc,
        ClaimsPrincipal principal,
        RevocationFailureCounter failures,
        ILogger? logger = null)
    {
        try
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var revoked = await svc.IsRevokedAsync(principal, CancellationToken.None).ConfigureAwait(false);
            failures.RecordSuccess();
            if (revoked)
            {
                cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
            // Stream already ended.
        }
        catch (Exception ex)
        {
            // SR-L-18: fail closed after the configured number of consecutive failures (store unavailable or
            // unexpected error); a single transient error keeps the stream alive.
            if (failures.RecordFailure())
            {
                logger?.LogWarning(
                    ex,
                    "Subscription stream closed: token revocation status could not be verified ({Failures} consecutive failures, fail-closed). CorrelationId={CorrelationId}",
                    failures.ConsecutiveFailures,
                    Guid.NewGuid().ToString("N"));
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // Stream already ended.
                }
            }
        }
    }

    internal static bool CheckPrincipalExpiry(
        CancellationTokenSource cts,
        ClaimsPrincipal principal,
        TimeProvider timeProvider)
    {
        if (cts.IsCancellationRequested)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (principal.Identity?.IsAuthenticated != true ||
            Autheris.Domain.Identity.GroupResolutionClaims.IsExpired(principal, now) ||
            Autheris.Domain.Identity.GroupResolutionClaims.IsMarkedDegraded(principal))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            return false;
        }

        return true;
    }

    internal static async Task PollEpochChangeAsync(
        CancellationTokenSource cts,
        Autheris.Application.Governance.Caching.GovernanceEpochProvider epochProvider,
        TenantId tenant,
        IServiceProvider services,
        ClaimsPrincipal principal,
        TableIdentifier? tableId,
        long[] lastEpochBox,
        Lock syncLock,
        ILogger? logger)
    {
        try
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
            if (!CheckPrincipalExpiry(cts, principal, timeProvider))
            {
                return;
            }

            var current = await epochProvider.GetEpochAsync(tenant, CancellationToken.None).ConfigureAwait(false);
            bool changed;
            lock (syncLock)
            {
                changed = current != lastEpochBox[0];
                if (changed)
                {
                    lastEpochBox[0] = current;
                }
            }

            if (changed)
            {
                await RevalidateSubscriptionAccessAsync(cts, services, principal, tenant, tableId, logger).ConfigureAwait(false);
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Subscription stream closed: governance epoch verification failed (fail-closed).");
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    internal static async Task RevalidateSubscriptionAccessAsync(
        CancellationTokenSource cts,
        IServiceProvider services,
        ClaimsPrincipal principal,
        TenantId? tenant,
        TableIdentifier? tableId,
        ILogger? logger)
    {
        try
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            var timeProvider = services.GetService<TimeProvider>() ?? TimeProvider.System;
            if (!CheckPrincipalExpiry(cts, principal, timeProvider))
            {
                return;
            }

            var revocation = services.GetService<ITokenRevocationService>();
            if (revocation != null && await revocation.IsRevokedAsync(principal, CancellationToken.None).ConfigureAwait(false))
            {
                cts.Cancel();
                return;
            }

            bool allowed = tableId is { } tid
                ? await EvaluateSingleTableAccessAsync(services, principal, tenant, tid).ConfigureAwait(false)
                : await EvaluateWildcardSubscriptionAccessAsync(services, principal, tenant).ConfigureAwait(false);

            if (!allowed)
            {
                cts.Cancel();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Subscription stream terminated after governance policy revalidation denied or failed.");
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task<bool> EvaluateSingleTableAccessAsync(
        IServiceProvider services,
        ClaimsPrincipal principal,
        TenantId? tenant,
        TableIdentifier tid)
    {
        var pdp = services.GetService<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>();
        var metadataRepo = services.GetService<ITableMetadataRepository>();
        if (pdp != null && metadataRepo != null && tenant is { } tenantId)
        {
            var meta = await metadataRepo.GetTableMetadataAsync(tid, CancellationToken.None).ConfigureAwait(false);
            if (meta is not null && meta.Table.IsActive)
            {
                var ctx = Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(principal, tenantId);
                var decision = await pdp.DecideAsync(
                    new Autheris.Application.Policy.Pdp.AccessRequest(
                        ctx, meta, Autheris.Application.Policy.Pdp.AccessOperation.Read, Autheris.Application.Policy.Pdp.AccessTransport.Streaming),
                    CancellationToken.None).ConfigureAwait(false);
                return decision.IsAllowed;
            }

            return false;
        }

        if (services.GetService<ITableAccessResolver>() is { } resolver)
        {
            var resolved = await resolver.ResolveTableAccessAsync(principal, tid, null, null, CancellationToken.None).ConfigureAwait(false);
            return resolved.Decision.IsAllowed;
        }

        return false;
    }

    private static async Task<bool> EvaluateWildcardSubscriptionAccessAsync(
        IServiceProvider services,
        ClaimsPrincipal principal,
        TenantId? tenant)
    {
        var roles = principal.GetUserRoles();
        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);
        if (!isGovAdmin && !isClusterAdmin)
        {
            return false;
        }

        var targets = new HashSet<TableIdentifier>();
        var streamStore = services.GetService<ICdcStreamStore>();
        if (streamStore != null)
        {
            var streams = await streamStore.ListStreamsAsync(CancellationToken.None).ConfigureAwait(false);
            foreach (var s in streams)
            {
                if (s.Status == CdcStreamStatus.Active)
                {
                    targets.Add(s.Table);
                }
            }
        }

        var metadataRepo = services.GetService<ITableMetadataRepository>();
        if (targets.Count == 0 && metadataRepo != null)
        {
            var activeTables = await Autheris.Application.Catalog.CatalogTablePageReader.ReadAllAsync(
                metadataRepo,
                new Autheris.Application.Catalog.CatalogTableQuery(IncludeInactive: false),
                t => t.Table.IsActive &&
                     (tenant is not { } validTenant ||
                      string.IsNullOrWhiteSpace(t.OwnerTenantId) ||
                      string.Equals(t.OwnerTenantId, validTenant.Value, StringComparison.OrdinalIgnoreCase)),
                ct: CancellationToken.None).ConfigureAwait(false);
            foreach (var t in activeTables)
            {
                targets.Add(t.Identifier);
            }
        }

        if (targets.Count == 0)
        {
            return services.GetService<Autheris.Application.Policy.Pdp.IAccessDecisionPoint>() != null ||
                   services.GetService<ITableAccessResolver>() != null;
        }

        foreach (var target in targets)
        {
            if (!await EvaluateSingleTableAccessAsync(services, principal, tenant, target).ConfigureAwait(false))
            {
                return false;
            }
        }

        return true;
    }
}
