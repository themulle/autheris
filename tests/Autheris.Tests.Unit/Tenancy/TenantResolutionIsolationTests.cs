using Autheris.Tests.Unit.TestSupport;
using Autheris.Tests.Common.Fakes;
namespace Autheris.Tests.Unit.Tenancy;

using Autheris.Tests.Common;
using System;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public class TenantResolutionIsolationTests
{
    [Fact]
    public async Task TenantResolution_AuthenticatedUser_WithMismatchedHeader_Returns403()
    {
        // Arrange
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new SecurityContextResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-alice"),
            new Claim(ClaimTypes.Name, "alice")
        };
        var identity = new ClaimsIdentity(claims, "Basic");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "tenant-beta";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        responseText.ShouldContain("CROSS_TENANT_ACCESS_FORBIDDEN");
    }

    [Fact]
    public async Task TenantResolution_AuthenticatedUser_WithoutTenantClaim_SpoofingHeader_Returns403()
    {
        // Arrange: User is authenticated but has NO tenant claim
        RequestDelegate next = _ => Task.CompletedTask;
        var middleware = new SecurityContextResolutionMiddleware(next);

        var context = new DefaultHttpContext();
        var claims = new[]
        {
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-bob"),
            new Claim(ClaimTypes.Name, "bob"),
            new Claim(ClaimTypes.Role, "StandardUser")
        };
        var identity = new ClaimsIdentity(claims.Append(DefaultTenantCaller.Claim), "TestScheme");
        context.User = new ClaimsPrincipal(identity);
        context.Request.Headers["X-Tenant-ID"] = "victim-tenant";
        context.Response.Body = new MemoryStream();

        // Act
        await middleware.InvokeAsync(context);

        // Assert: Access is forbidden because non-admin cannot specify arbitrary tenant without a claim
        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var responseText = await reader.ReadToEndAsync();
        responseText.ShouldContain("CROSS_TENANT_ACCESS_FORBIDDEN");
    }

    [Fact]
    public async Task BasicAuthenticationHandler_EmitsTenantClaim()
    {
        // Arrange
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users =
                    [
                        new BasicAuthUserConfig
                        {
                            Username = "tenant_user",
                            Password = "secretPassword123",
                            TenantId = "tenant-finance"
                        }
                    ]
                }
            }
        };

        var mockEnv = Substitute.For<IWebHostEnvironment>();
        mockEnv.EnvironmentName.Returns("Development");

        var schemeOptionsMonitor = new StaticOptionsMonitor<AuthenticationSchemeOptions>(new AuthenticationSchemeOptions());
        var handler = new BasicAuthenticationHandler(
            schemeOptionsMonitor,
            NullLoggerFactory.Instance,
            System.Text.Encodings.Web.UrlEncoder.Default,
            Options.Create(options),
            mockEnv);

        var context = new DefaultHttpContext();
        await handler.InitializeAsync(new AuthenticationScheme(BasicAuthenticationHandler.SchemeName, "Basic", typeof(BasicAuthenticationHandler)), context);

        var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("tenant_user:secretPassword123"));
        context.Request.Headers.Authorization = $"Basic {credentials}";

        // Act
        var result = await handler.AuthenticateAsync();

        // Assert
        result.Succeeded.ShouldBeTrue();
        result.Principal.ShouldNotBeNull();
        result.Principal.FindFirst("tenant_id")?.Value.ShouldBe("tenant-finance");
        result.Principal.FindFirst("tenant")?.Value.ShouldBe("tenant-finance");
        result.Principal.FindFirst("tid")?.Value.ShouldBe("tenant-finance");
    }

    [Fact]
    public void TenantId_TrailingNewline_ThrowsArgumentException()
    {
        Should.Throw<ArgumentException>(() => new TenantId("tenant-alpha\n"));
        Should.Throw<ArgumentException>(() => new TenantId("tenant-alpha\r\n"));
    }

    [Fact]
    public async Task StreamRlsPolicyEnforcer_EmptyCdcTenantId_ReturnsDenied()
    {
        // Finding F-02 Verification:
        // A CDC event with null or empty TenantId must fail closed and be denied.
        var policyEnforcement = Autheris.Tests.Unit.TestSupport.PdpTestDoubles.AllowAll();
        var metadataRepo = new InMemoryTableMetadataRepository();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        var epochService = Substitute.For<IEpochValidationService>();

        var enforcer = Autheris.Tests.Unit.TestDoubles.StreamRlsPolicyEnforcerTestFactory.Create(
            policyEnforcement,
            metadataRepo,
            maskingProvider,
            epochService,
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            new InMemoryConsentRepository(),
            Substitute.For<IConsentResolutionService>(),
            Substitute.For<IConsentCacheService>());

        var table = new TableIdentifier("sales", "crm", "leads");
        var cdcEvent = new CdcEvent(
            EventId: "evt-empty-tenant",
            Table: table,
            Operation: CdcOperation.Insert,
            TenantId: null!, // Missing TenantId
            Before: null,
            After: new System.Collections.Generic.Dictionary<string, object?> { ["id"] = 1 },
            Timestamp: DateTimeOffset.UtcNow);

        var subscriber = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("tenant_id", "tenant-alpha"),
            new Claim(ClaimTypes.PrimarySid, "S-1-5-SUBSCRIBER")
        }));

        var decision = await enforcer.EvaluateAndMaskAsync(cdcEvent, subscriber);

        decision.IsAllowed.ShouldBeFalse();
        decision.FilterReason.ShouldBe("Tenant mismatch");
    }
}
