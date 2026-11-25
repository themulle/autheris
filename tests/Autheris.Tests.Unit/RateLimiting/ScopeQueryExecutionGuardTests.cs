namespace Autheris.Tests.Unit.RateLimiting;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Quota;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Endpoints.Interfaces;
using Autheris.Application.Sql.Endpoints.Services;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.State.Caching.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestSupport;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScopeQueryExecutionGuardTests
{
    internal static readonly TableIdentifier OrdersTableId = new("sales", "public", "orders");

    internal static ClaimsPrincipal CreatePrincipal(
        string userSid = "S-1-5-21-1053-USER",
        string tenantId = "tenant-1",
        string? scp = null,
        string? rfcScope = null,
        string? role = null,
        string? clientId = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid),
            new(ClaimTypes.NameIdentifier, userSid),
            new("objectSid", userSid),
            new(ClaimTypes.Name, userSid),
            new("tenant_id", tenantId)
        };

        if (!string.IsNullOrWhiteSpace(scp))
        {
            claims.Add(new Claim("scp", scp));
        }

        if (!string.IsNullOrWhiteSpace(rfcScope))
        {
            claims.Add(new Claim("scope", rfcScope));
        }

        if (!string.IsNullOrWhiteSpace(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            claims.Add(new Claim("client_id", clientId));
        }

        return PipelinePrincipals.MarkComplete(
            new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer", ClaimTypes.Name, ClaimTypes.Role)));
    }

    internal static GatewayOptions CreateGatewayOptions(
        Dictionary<string, ClientTierLimitOverride>? scopeLimits = null,
        Dictionary<string, ClientTierLimitOverride>? tierLimits = null,
        Dictionary<string, string>? roleTierMappings = null,
        int maxResultRowsGlobal = 500) =>
        new()
        {
            RateLimiting = new RateLimitingOptions
            {
                ClientTiers = new ClientTierOptions
                {
                    TierLimits = tierLimits ?? new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase),
                    RoleTierMappings = roleTierMappings ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                    ScopeLimits = scopeLimits ?? new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
                }
            },
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "sales",
                AllowedDataSources = ["sales"],
                MaxAllowedRows = maxResultRowsGlobal
            },
            Mcp = new McpOptions { MaxResultRows = maxResultRowsGlobal },
            DataSources = new SqlDataSourceOptions
            {
                Connections =
                {
                    ["sales"] = new DataSourceConnectionOptions
                    {
                        Provider = "PostgreSQL",
                        ConnectionString = "Host=localhost;Database=sales;Username=u;Password=p"
                    }
                }
            }
        };

    internal static TableMetadata CreateOrdersMetadata() => new()
    {
        Identifier = OrdersTableId,
        Table = new Table
        {
            TableName = "orders",
            SchemaName = "public",
            SourceName = "sales",
            SourceType = "PostgreSQL",
            IsActive = true,
            DataSourceType = DataSourceType.Sql
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "name", DataType = "varchar" }
        ],
        PrimaryKeyColumns = ["id"]
    };

    [Fact]
    public async Task ClientTierResolver_MultiScopeAndTierLimits_AppliesStrictestLowerBound()
    {
        var options = CreateGatewayOptions(
            tierLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["Enterprise"] = new()
                {
                    MaxCostPerQuery = 5000,
                    MaxConcurrentQueries = 20,
                    MaxQueryDurationSeconds = 120,
                    MaxResultRows = 5000
                }
            },
            roleTierMappings: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["PowerUser"] = "Enterprise"
            },
            scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["Data.Read"] = new()
                {
                    MaxCostPerQuery = 1500,
                    MaxConcurrentQueries = 5,
                    MaxQueryDurationSeconds = 45,
                    MaxResultRows = 250
                },
                ["api://autheris/Agent.Read"] = new()
                {
                    MaxCostPerQuery = 600,
                    MaxConcurrentQueries = 2,
                    MaxQueryDurationSeconds = 10,
                    MaxResultRows = 500
                }
            });

        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var principal = CreatePrincipal(role: "PowerUser", scp: "api://autheris/Agent.Read", rfcScope: "Data.Read");

        var resolved = await resolver.ResolveAsync(principal, apiKey: null, clientIp: "127.0.0.1");

        resolved.Tier.ShouldBe(ClientTier.Enterprise);
        resolved.Policy.MaxCostPerQuery.ShouldBe(600);
        resolved.Policy.MaxConcurrentQueries.ShouldBe(2);
        resolved.Policy.MaxQueryDurationSeconds.ShouldBe(10);
        resolved.Policy.MaxResultRows.ShouldBe(250);
    }

    [Fact]
    public void GatewayStartupValidator_RejectsInvalidScopeLimitBounds_AndAcceptsValidBounds()
    {
        var valid = CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent.Read"] = new()
            {
                MaxConcurrentQueries = 2,
                MaxQueryDurationSeconds = 15,
                MaxResultRows = 500
            }
        });
        Should.NotThrow(() => GatewayStartupValidator.ValidateClientTierComplexityOverrides(valid));

        foreach (var invalidOverride in new[]
        {
            new ClientTierLimitOverride { MaxConcurrentQueries = 0 },
            new ClientTierLimitOverride { MaxConcurrentQueries = 10_001 },
            new ClientTierLimitOverride { MaxQueryDurationSeconds = 0 },
            new ClientTierLimitOverride { MaxQueryDurationSeconds = 3_601 },
            new ClientTierLimitOverride { MaxResultRows = 0 },
            new ClientTierLimitOverride { MaxResultRows = 1_000_001 }
        })
        {
            var invalid = CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
            {
                ["Agent.Read"] = invalidOverride
            });

            var ex = Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateClientTierComplexityOverrides(invalid));
            ex.Message.ShouldContain("ScopeLimit");
            ex.Message.ShouldContain("Agent.Read");
        }
    }

    [Fact]
    public async Task Guard_MaxConcurrentQueries_BlocksExcessForSameSubject_FreesOnRelease_ReusesNested_AndLeavesOtherScopesUnaffected()
    {
        var options = CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent.Read"] = new() { MaxConcurrentQueries = 2 }
        });
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var guard = new ScopeQueryExecutionGuard(resolver);

        var agentUser = CreatePrincipal("S-1-5-21-USER-1", scp: "Agent.Read");
        var appScopeSameUser = CreatePrincipal("S-1-5-21-USER-1", scp: "Data.Read");

        var gate1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task1 = guard.RunAsync(agentUser, null, null, async (outerLease, ct) =>
        {
            outerLease.IsNested.ShouldBeFalse();
            // Nested call must reuse the ambient lease instead of consuming a second concurrency slot.
            var innerIsNested = await guard.RunAsync(agentUser, null, null, (innerLease, _) => Task.FromResult(innerLease.IsNested), ct);
            innerIsNested.ShouldBeTrue();
            started1.TrySetResult();
            await gate1.Task;
            return 1;
        });

        var task2 = guard.RunAsync(agentUser, null, null, async (_, _) =>
        {
            started2.TrySetResult();
            await gate2.Task;
            return 2;
        });

        await Task.WhenAll(started1.Task, started2.Task);

        // 3rd concurrent query with Agent.Read is rejected.
        var rejected = await Should.ThrowAsync<ScopeConcurrencyExceededException>(() =>
            guard.RunAsync(agentUser, null, null, (_, _) => Task.FromResult(3)));
        rejected.ErrorCode.ShouldBe(ApiErrorCodes.ScopeConcurrencyExceeded);

        // Same user with Data.Read (no Agent.Read limit) is unaffected.
        var otherScopeResult = await guard.RunAsync(appScopeSameUser, null, null, (_, _) => Task.FromResult(99));
        otherScopeResult.ShouldBe(99);

        // Releasing one slot allows the next Agent.Read query to run.
        gate1.TrySetResult();
        (await task1).ShouldBe(1);

        var afterRelease = await guard.RunAsync(agentUser, null, null, (_, _) => Task.FromResult(4));
        afterRelease.ShouldBe(4);

        gate2.TrySetResult();
        (await task2).ShouldBe(2);
    }

    [Fact]
    public async Task Guard_MaxQueryDurationSeconds_CancelsAtDeadline_AndClampsTimeoutAndRows()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 24, 5, 0, 0, TimeSpan.Zero));
        var options = CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Agent.Read"] = new() { MaxQueryDurationSeconds = 5, MaxResultRows = 25 }
        });
        var resolver = new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options));
        var guard = new ScopeQueryExecutionGuard(resolver, time);
        var principal = CreatePrincipal(scp: "Agent.Read");

        var inside = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observedCt = default;
        ScopeExecutionLease observedLease = default;

        var running = guard.RunAsync(principal, null, null, async (lease, ct) =>
        {
            observedLease = lease;
            observedCt = ct;
            inside.TrySetResult();
            var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var _ = ct.Register(static s => ((TaskCompletionSource)s!).TrySetCanceled(), canceled);
            await canceled.Task;
            return 0;
        });

        await inside.Task;
        observedCt.IsCancellationRequested.ShouldBeFalse();
        observedLease.ClampTimeoutSeconds(60).ShouldBe(5);
        observedLease.ClampRows(1000).ShouldBe(25);
        observedLease.ClampRowLimit(SqlRowLimit.For(options.WebSql)).Effective(100).ShouldBe(25);

        time.Advance(TimeSpan.FromSeconds(5));
        await Should.ThrowAsync<TimeoutException>(() => running);
        observedCt.IsCancellationRequested.ShouldBeTrue();

        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        await Should.ThrowAsync<OperationCanceledException>(() =>
            guard.RunAsync(principal, null, null, (_, _) => Task.FromResult(1), callerCts.Token));
    }

    [Fact]
    public async Task ErrorMappings_ScopeConcurrencyAndTimeout_Return429And504SafelyAcrossSurfaces()
    {
        var ex = new ScopeConcurrencyExceededException(2, "Agent.Read", retryAfterSeconds: 7);

        GatewayExceptionStatusMapper.Resolve(ex).ShouldBe(StatusCodes.Status429TooManyRequests);

        var sqlFailure = SqlFailureClassifier.Classify(ex);
        sqlFailure.Kind.ShouldBe(SqlFailureKind.Throttled);
        sqlFailure.Code.ShouldBe(ApiErrorCodes.ScopeConcurrencyExceeded);
        sqlFailure.FixedMessage.ShouldNotContain("Agent.Read");

        var sanitizer = new ErrorSanitizingFilter(new TestHostEnvironment("Production"), NullLogger<ErrorSanitizingFilter>.Instance);
        var gqlErr = sanitizer.OnError(ErrorBuilder.New().SetMessage(ex.Message).SetCode(ApiErrorCodes.ScopeConcurrencyExceeded).SetException(ex).Build());
        gqlErr.Code.ShouldBe(ApiErrorCodes.ScopeConcurrencyExceeded);
        gqlErr.Exception.ShouldBeNull();

        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        var handler = new GatewayExceptionHandler(NullLogger<GatewayExceptionHandler>.Instance);
        (await handler.TryHandleAsync(ctx, ex, CancellationToken.None)).ShouldBeTrue();
        ctx.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        ctx.Response.Headers["Retry-After"].ToString().ShouldBe("7");
        ctx.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        doc.RootElement.GetProperty("code").GetString().ShouldBe(ApiErrorCodes.ScopeConcurrencyExceeded);

        // TimeoutException from scope deadline expiry maps cleanly to 504 without leaking details.
        var timeoutEx = new TimeoutException("The query exceeded the maximum allowed execution duration for this scope.");
        SqlFailureClassifier.Classify(timeoutEx).Kind.ShouldBe(SqlFailureKind.Timeout);
        var gqlTimeoutErr = sanitizer.OnError(ErrorBuilder.New().SetMessage(timeoutEx.Message).SetException(timeoutEx).Build());
        gqlTimeoutErr.Code.ShouldBe("TIMEOUT");
        gqlTimeoutErr.Exception.ShouldBeNull();

        var timeoutCtx = new DefaultHttpContext();
        timeoutCtx.Response.Body = new MemoryStream();
        (await handler.TryHandleAsync(timeoutCtx, timeoutEx, CancellationToken.None)).ShouldBeTrue();
        timeoutCtx.Response.StatusCode.ShouldBe(StatusCodes.Status504GatewayTimeout);
        timeoutCtx.Response.Body.Position = 0;
        using var timeoutDoc = await JsonDocument.ParseAsync(timeoutCtx.Response.Body);
        timeoutDoc.RootElement.GetProperty("code").GetString().ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.Timeout);

        // Declarative SQL endpoints map both ScopeConcurrencyExceededException (429) and TimeoutException (504).
        var sp = new ServiceCollection().BuildServiceProvider();
        var sqlEpOpts = Options.Create(CreateGatewayOptions());
        var getThrottleCtx = new DefaultHttpContext { User = CreatePrincipal(), RequestServices = sp };
        getThrottleCtx.Response.Body = new MemoryStream();
        await SqlEndpointRoutes.HandleGetEndpoint(
            "ep1",
            getThrottleCtx,
            new ThrowingSqlEndpointExecutionService(ex),
            sqlEpOpts,
            NullLoggerFactory.Instance);
        getThrottleCtx.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        getThrottleCtx.Response.Headers["Retry-After"].ToString().ShouldBe("7");

        var postTimeoutCtx = new DefaultHttpContext { User = CreatePrincipal(), RequestServices = sp };
        postTimeoutCtx.Response.Body = new MemoryStream();
        await SqlEndpointRoutes.HandlePostEndpoint(
            "ep1",
            postTimeoutCtx,
            new ThrowingSqlEndpointExecutionService(timeoutEx),
            sqlEpOpts,
            NullLoggerFactory.Instance);
        postTimeoutCtx.Response.StatusCode.ShouldBe(StatusCodes.Status504GatewayTimeout);
    }

    [Fact]
    public void McpSessionPrincipals_RejectSpoofedClientIdAzpAndAppIdInAdditionalClaims()
    {
        McpProtocolHandler.IsAllowedAdditionalClaimType("client_id").ShouldBeFalse();
        McpProtocolHandler.IsAllowedAdditionalClaimType("CLIENT_ID").ShouldBeFalse();
        McpProtocolHandler.IsAllowedAdditionalClaimType("azp").ShouldBeFalse();
        McpProtocolHandler.IsAllowedAdditionalClaimType("appid").ShouldBeFalse();
        McpProtocolHandler.IsAllowedAdditionalClaimType("APPID").ShouldBeFalse();
        McpProtocolHandler.IsAllowedAdditionalClaimType("department").ShouldBeTrue();

        var additional = new Dictionary<string, string>
        {
            ["client_id"] = "spoof-client",
            ["azp"] = "spoof-azp",
            ["appid"] = "spoof-appid",
            ["department"] = "sales"
        };

        var appSession = new McpSessionContext(
            "s-app", "spn-real", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: null, Roles: ["Reader"], GroupSids: [], AdditionalClaims: additional);
        foreach (var principal in new[] { McpProtocolHandler.BuildPrincipalFromSession(appSession)!, GraphQlCallerPrincipal.BuildMcpSessionPrincipal(appSession) })
        {
            principal.FindFirst("client_id").ShouldBeNull();
            principal.FindFirst("azp").ShouldBeNull();
            principal.FindFirst("appid").ShouldBeNull();
            principal.FindFirst("department")?.Value.ShouldBe("sales");
        }

        var userSession = new McpSessionContext(
            "s-usr", "spn-real", "tenant-1", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5),
            UserSid: "S-1-5-21-USER", Roles: ["Reader"], GroupSids: [], AdditionalClaims: additional);
        foreach (var principal in new[] { McpProtocolHandler.BuildPrincipalFromSession(userSession)!, GraphQlCallerPrincipal.BuildMcpSessionPrincipal(userSession) })
        {
            principal.FindFirst("client_id")?.Value.ShouldBe("spn-real");
            principal.FindFirst("azp")?.Value.ShouldBe("spn-real");
            principal.FindFirst("appid").ShouldBeNull();
        }
    }

    private sealed class ThrowingSqlEndpointExecutionService(Exception exception) : ISqlEndpointExecutionService
    {
        public Task<GovernedSqlResult> ExecuteEndpointAsync(
            string endpointName,
            IReadOnlyDictionary<string, object?>? rawInputs,
            ClaimsPrincipal user,
            TenantId tenantId,
            SqlEndpointInvocation invocation,
            CancellationToken ct = default) => Task.FromException<GovernedSqlResult>(exception);
    }

    [Fact]
    public async Task Guard_ExtraUnmappedOidcScopes_ShareSameConcurrencyBucket()
    {
        var options = CreateGatewayOptions(scopeLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["mcp:query"] = new() { MaxConcurrentQueries = 1 }
        });
        var guard = new ScopeQueryExecutionGuard(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options)));
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var t1 = guard.RunAsync(CreatePrincipal("S-1-5-21-USER-OIDC", scp: "mcp:query"), null, null, async (_, _) =>
        {
            entered.TrySetResult();
            await held.Task;
            return 1;
        });

        await entered.Task;
        await Should.ThrowAsync<ScopeConcurrencyExceededException>(() =>
            guard.RunAsync(CreatePrincipal("S-1-5-21-USER-OIDC", scp: "mcp:query openid profile email"), null, null, (_, _) => Task.FromResult(2)));

        held.TrySetResult();
        (await t1).ShouldBe(1);
    }

    [Fact]
    public async Task Guard_UnauthenticatedNullIp_UsesBoundedSlotWithoutRandomGuidBypass_AndPropagatesHttpClientIp()
    {
        var options = CreateGatewayOptions(tierLimits: new Dictionary<string, ClientTierLimitOverride>(StringComparer.OrdinalIgnoreCase)
        {
            ["Free"] = new() { MaxConcurrentQueries = 1 }
        });
        var guard = new ScopeQueryExecutionGuard(new ClientTierResolver(NullLogger<ClientTierResolver>.Instance, Options.Create(options)));
        var heldUnassigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredUnassigned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Even with null principal/apiKey/clientIp outside HTTP, a bounded "anon_unassigned" bucket is enforced instead of random GUID churn.
        var t1 = guard.RunAsync(null, null, null, async (_, _) =>
        {
            enteredUnassigned.TrySetResult();
            await heldUnassigned.Task;
            return 1;
        });
        await enteredUnassigned.Task;
        await Should.ThrowAsync<ScopeConcurrencyExceededException>(() =>
            guard.RunAsync(null, null, null, (_, _) => Task.FromResult(2)));
        heldUnassigned.TrySetResult();
        (await t1).ShouldBe(1);

        // Inside IngressTransportMiddleware, RemoteIpAddress is propagated cleanly so distinct client IPs do not share slots while same-IP calls do.
        var services = new ServiceCollection()
            .AddScoped<Autheris.Application.DataSources.IIngressTransportContext, Autheris.Application.DataSources.IngressTransportContext>()
            .BuildServiceProvider();
        using var scope1 = services.CreateScope();
        var ctx1 = new DefaultHttpContext { RequestServices = scope1.ServiceProvider };
        ctx1.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");

        var heldIp1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredIp1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var httpTask1 = new IngressTransportMiddleware(async _ =>
        {
            await guard.RunAsync(null, null, null, async (_, _) =>
            {
                enteredIp1.TrySetResult();
                await heldIp1.Task;
                return 10;
            });
        }).InvokeAsync(ctx1);

        await enteredIp1.Task;

        using var scopeSameIp = services.CreateScope();
        var ctxSameIp = new DefaultHttpContext { RequestServices = scopeSameIp.ServiceProvider };
        ctxSameIp.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        await Should.ThrowAsync<ScopeConcurrencyExceededException>(() =>
            new IngressTransportMiddleware(async _ =>
            {
                await guard.RunAsync(null, null, null, (_, _) => Task.FromResult(11));
            }).InvokeAsync(ctxSameIp));

        using var scopeOtherIp = services.CreateScope();
        var ctxOtherIp = new DefaultHttpContext { RequestServices = scopeOtherIp.ServiceProvider };
        ctxOtherIp.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.20");
        await new IngressTransportMiddleware(async _ =>
        {
            await guard.RunAsync(null, null, null, (_, _) => Task.FromResult(12));
        }).InvokeAsync(ctxOtherIp);

        heldIp1.TrySetResult();
        await httpTask1;
    }
}
