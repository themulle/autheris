using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Application.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

/// <summary>
/// Wave 1 Gateway Hardening Tests:
/// - Issue #44: Pre-Auth IP Rate Limit Exemption for Authenticated Bearer Tokens
/// - Issue #42: Protocol Kill-Switches (OData, Arrow, Flight SQL, Iceberg)
/// - Issue #41: Kerberos / Negotiate Scheme Cleanup & 401 Error Handling
/// - Issue #31: Caller Context in Audit Log
/// - Issue #30: Distinguishable Error Codes
/// </summary>
public sealed class Wave1GatewayHardeningTests
{
    [Fact]
    public async Task Issue44_PreAuthRateLimiter_Exempts_BearerToken_Requests()
    {
        // Arrange
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new RateLimitResult(false, 45)); // Limiter would reject unauthenticated calls with 45s retry

        var nextCalled = false;
        var options = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                PreAuthIpRateLimit = new PreAuthIpRateLimitOptions
                {
                    PermitLimit = 10,
                    WindowSeconds = 60,
                    ExemptAuthenticatedRequests = true
                }
            }
        };

        var middleware = new PreAuthIpRateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(options),
            limiter);

        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";
        context.Request.Headers["Authorization"] = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.e30.t-IDcSemACt8x4iTMCda8Yhe3iZaWbvV5XKSTbuAn0M";

        // Act
        await middleware.InvokeAsync(context);

        // Assert: Authenticated request should bypass pre-auth limiter and call next
        nextCalled.ShouldBeTrue();
        await limiter.DidNotReceiveWithAnyArgs().CheckPreAuthIpAsync(default!, default!, default);
    }

    [Fact]
    public async Task Issue44_PreAuthRateLimiter_Enforces_Limit_On_Unauthenticated_Requests()
    {
        // Arrange
        var limiter = Substitute.For<IRateLimiterService>();
        limiter.CheckPreAuthIpAsync(Arg.Any<string>(), Arg.Any<PreAuthIpRateLimitOptions>(), Arg.Any<System.Threading.CancellationToken>())
            .Returns(new RateLimitResult(false, 42)); // 42s remaining

        var nextCalled = false;
        var options = new GatewayOptions
        {
            RateLimiting = new RateLimitingOptions
            {
                PreAuthIpRateLimit = new PreAuthIpRateLimitOptions
                {
                    PermitLimit = 10,
                    WindowSeconds = 60,
                    ExemptAuthenticatedRequests = true
                }
            }
        };

        var middleware = new PreAuthIpRateLimitingMiddleware(
            _ => { nextCalled = true; return Task.CompletedTask; },
            Options.Create(options),
            limiter);

        var context = new DefaultHttpContext();
        context.Request.Path = "/graphql";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert: Unauthenticated request should be rate-limited and return 429 with dynamic Retry-After: 42
        nextCalled.ShouldBeFalse();
        context.Response.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        context.Response.Headers.RetryAfter.ToString().ShouldBe("42");
    }

    [Fact]
    public async Task Issue42_McpDatasetCatalog_HidesDisabledProtocols()
    {
        // Arrange
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();

        metaRepo.GetAllTablesAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns(new System.Collections.Generic.List<TableMetadata>());

        var disabledOptions = new GatewayOptions
        {
            OData = new ODataOptions { Enabled = false },
            Arrow = new ArrowExportOptions { Enabled = false, EnableFlightSql = false },
            Lakehouse = new LakehouseOptions { Enabled = false }
        };

        var catalog = new Autheris.Application.Mcp.Services.McpDatasetCatalog(
            metaRepo,
            consentRepo,
            execService,
            options: Options.Create(disabledOptions));

        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        // Act
        var result = await catalog.ListDatasetsAsync(principal, null, null, default);

        // Assert: Endpoints should NOT contain OData, Arrow, or Iceberg
        result.Endpoints.ShouldNotBeNull();
        result.Endpoints!.ShouldNotContain(e => e.Protocol.Contains("OData"));
        result.Endpoints.ShouldNotContain(e => e.Protocol.Contains("Arrow"));
        result.Endpoints.ShouldNotContain(e => e.Protocol.Contains("Iceberg"));
        result.Endpoints.ShouldContain(e => e.Protocol == "GraphQL");
    }

    [Fact]
    public async Task Issue42_McpDatasetCatalog_ShowsEnabledProtocols()
    {
        // Arrange
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var execService = Substitute.For<IGatewayExecutionService>();

        metaRepo.GetAllTablesAsync(Arg.Any<System.Threading.CancellationToken>())
            .Returns(new System.Collections.Generic.List<TableMetadata>());

        var enabledOptions = new GatewayOptions
        {
            OData = new ODataOptions { Enabled = true },
            Arrow = new ArrowExportOptions { Enabled = true, EnableFlightSql = true },
            Lakehouse = new LakehouseOptions { Enabled = true }
        };

        var catalog = new Autheris.Application.Mcp.Services.McpDatasetCatalog(
            metaRepo,
            consentRepo,
            execService,
            options: Options.Create(enabledOptions));

        var principal = new ClaimsPrincipal(new ClaimsIdentity());

        // Act
        var result = await catalog.ListDatasetsAsync(principal, null, null, default);

        // Assert: Endpoints should contain enabled protocols
        result.Endpoints.ShouldNotBeNull();
        result.Endpoints!.ShouldContain(e => e.Protocol.Contains("OData"));
        result.Endpoints.ShouldContain(e => e.Protocol.Contains("Arrow IPC export"));
        result.Endpoints.ShouldContain(e => e.Protocol.Contains("Arrow Flight SQL"));
        result.Endpoints.ShouldContain(e => e.Protocol.Contains("Iceberg REST Catalog"));
    }

    [Fact]
    public void Issue41_AuthenticationOptions_DefaultsToKerberosDisabled()
    {
        var auth = new AuthenticationOptions();
        auth.EnableKerberos.ShouldBeFalse();
        auth.IsKerberosConfigured.ShouldBeFalse();
    }

    [Fact]
    public async Task Issue41_NegotiateHardening_HandlesAuthenticationFailedGracefully()
    {
        var options = new Microsoft.AspNetCore.Authentication.Negotiate.NegotiateOptions();
        Autheris.Api.Security.NegotiateHardening.Configure(options, requireKerberosOnly: true);

        options.Events.ShouldNotBeNull();
        options.Events.OnAuthenticationFailed.ShouldNotBeNull();

        var httpContext = new DefaultHttpContext();
        var scheme = new Microsoft.AspNetCore.Authentication.AuthenticationScheme("Negotiate", "Negotiate", typeof(Microsoft.AspNetCore.Authentication.Negotiate.NegotiateHandler));
        var failedContext = new Microsoft.AspNetCore.Authentication.Negotiate.AuthenticationFailedContext(httpContext, scheme, options)
        {
            Exception = new System.Security.Authentication.AuthenticationException("Malformed SPNEGO token")
        };

        await options.Events.OnAuthenticationFailed(failedContext);

        failedContext.Result.ShouldNotBeNull();
        failedContext.Result.Succeeded.ShouldBeFalse();
        failedContext.Result.Failure.ShouldNotBeNull();
        failedContext.Result.Failure!.Message.ShouldContain("Malformed SPNEGO token");
    }

    [Fact]
    public void Issue31_CallerContext_ExtractedAndSanitizedFromHeaders()
    {
        // Arrange
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Caller-Application"] = "  FinancialAnalyticsService  \r\n";
        context.Request.Headers["X-Caller-Version"] = "v2.4.1";
        context.Request.Headers["X-Caller-Query-Name"] = "GetQuarterlyRevenue";

        // Act
        var secContext = Autheris.Api.Security.SecurityContextFactory.CreateFromHttpContext(context);

        // Assert
        secContext.Caller.ShouldNotBeNull();
        secContext.Caller!.Application.ShouldBe("financialanalyticsservice");
        secContext.Caller.Version.ShouldBe("v2.4.1");
        secContext.Caller.QueryName.ShouldBe("getquarterlyrevenue");
    }

    [Fact]
    public void Issue31_CallerContext_IncludedInAuditLogEntryEffectiveDetailsJson()
    {
        // Arrange
        var caller = new Autheris.Domain.Security.CallerContext("ReportingWorker", "1.0.0", "DailySync");
        var entry = new AuditLogEntry
        {
            TenantId = Autheris.Domain.Common.TenantId.LegacySingleTenant,
            EventType = "TABLE_QUERY",
            Decision = "ALLOW",
            DetailsJson = JsonSerializer.Serialize(new { row_count = 100 }),
            Caller = caller
        };

        // Act
        var effectiveJson = entry.EffectiveDetailsJson;

        // Assert
        effectiveJson.ShouldContain("\"caller\"");
        effectiveJson.ShouldContain("\"app\":\"ReportingWorker\"");
        effectiveJson.ShouldContain("\"ver\":\"1.0.0\"");
        effectiveJson.ShouldContain("\"query\":\"DailySync\"");
        effectiveJson.ShouldContain("\"row_count\":100");
    }

    [Fact]
    public void Issue30_DistinguishableErrorCodes_PreservedInErrorSanitizingFilter()
    {
        // Arrange
        var env = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        env.EnvironmentName.Returns("Production"); // Non-development test
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ErrorSanitizingFilter>>();
        var filter = new ErrorSanitizingFilter(env, logger);

        // 1. Column Not Released
        var colError = HotChocolate.ErrorBuilder.New()
            .SetMessage("Column 'ssn' is not released")
            .SetException(new Autheris.Domain.Exceptions.ColumnNotReleasedException("ssn"))
            .Build();
        var sanitizedColError = filter.OnError(colError);
        sanitizedColError.Code.ShouldBe("COLUMN_NOT_RELEASED");

        // 2. Filter on Masked Column
        var filterError = HotChocolate.ErrorBuilder.New()
            .SetMessage("Filter on masked column 'salary'")
            .SetException(new Autheris.Domain.Exceptions.FilterOnMaskedColumnException("salary"))
            .Build();
        var sanitizedFilterError = filter.OnError(filterError);
        sanitizedFilterError.Code.ShouldBe("FILTER_ON_MASKED_COLUMN");

        // 3. Cost Limit Exceeded
        var costError = HotChocolate.ErrorBuilder.New()
            .SetMessage("Cost limit")
            .SetException(new Autheris.Domain.Exceptions.CostLimitExceededException())
            .Build();
        var sanitizedCostError = filter.OnError(costError);
        sanitizedCostError.Code.ShouldBe("COST_LIMIT");

        // 4. Row Limit Exceeded
        var rowError = HotChocolate.ErrorBuilder.New()
            .SetMessage("Row limit")
            .SetException(new Autheris.Domain.Exceptions.RowLimitExceededException())
            .Build();
        var sanitizedRowError = filter.OnError(rowError);
        sanitizedRowError.Code.ShouldBe("ROW_LIMIT");

        // 5. Anti-oracle check: TableNotFoundException in Production must be masked as FORBIDDEN / "Access denied."
        var notFoundError = HotChocolate.ErrorBuilder.New()
            .SetMessage("Table not found")
            .SetException(new Autheris.Domain.Exceptions.TableNotFoundException(new Autheris.Domain.Common.TableIdentifier("corp", "dbo", "secret_table")))
            .Build();
        var sanitizedNotFoundError = filter.OnError(notFoundError);
        sanitizedNotFoundError.Code.ShouldBe("FORBIDDEN");
        sanitizedNotFoundError.Message.ShouldBe("Access denied.");
    }
}
