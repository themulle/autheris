namespace Autheris.Tests.Unit.Security;

using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DevDiagnosticsTests
{
    private static DefaultHttpContext CreateContext(params Claim[] claims)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var allClaims = claims.Append(new Claim(ClaimTypes.Name, "alice")).ToList();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(allClaims, "TestAuth", ClaimTypes.Name, ClaimTypes.Role));
        return context;
    }

    private static async Task<JsonDocument> ReadJsonBodyAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        var json = await reader.ReadToEndAsync();
        return JsonDocument.Parse(json);
    }

    [Fact]
    public async Task WriteForbiddenAsync_InvalidTenantClaim_OmitsTenantValue()
    {
        var context = CreateContext(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TENANT-TEST"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "evil<script>tenant"));

        await DevDiagnostics.WriteForbiddenAsync(context, "Access denied.");

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        raw.ShouldNotContain("evil");
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("tenant").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task WriteForbiddenAsync_ConflictingTenantClaims_OmitsTenantValue()
    {
        var context = CreateContext(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TENANT-CONFLICT"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "tenant-alpha"),
            new Claim("tid", "tenant-beta"));

        await DevDiagnostics.WriteForbiddenAsync(context, "Access denied.");

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        raw.ShouldNotContain("tenant-alpha");
        raw.ShouldNotContain("tenant-beta");
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("tenant").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task WriteForbiddenAsync_ValidTenantClaim_EmitsTenantValue()
    {
        var context = CreateContext(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-TENANT-VALID"),
            new Claim(ClaimTypes.Role, "Consumer"),
            new Claim("tenant_id", "tenant-alpha"));

        await DevDiagnostics.WriteForbiddenAsync(context, "Access denied.");

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        using var doc = await ReadJsonBodyAsync(context);
        doc.RootElement.GetProperty("tenant").GetString().ShouldBe("tenant-alpha");
    }

    [Fact]
    public async Task DevAuthorizationResultHandler_InvalidTenantClaim_DoesNotEchoRawClaim()
    {
        var context = CreateContext(
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-AUTHZ-TEST"),
            new Claim(ClaimTypes.Role, "GovernanceAdmin"),
            new Claim("tenant_id", "evil<script>tenant"));
        context.SetEndpoint(new Endpoint(
            _ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute(GatewayPolicies.GovernanceAdmin)),
            "test"));

        var handler = new DevAuthorizationResultHandler();
        await handler.HandleAsync(
            _ => Task.CompletedTask,
            context,
            new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build(),
            PolicyAuthorizationResult.Forbid());

        context.Response.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        context.Response.Body.Position = 0;
        var raw = await new StreamReader(context.Response.Body).ReadToEndAsync();
        raw.ShouldNotContain("evil");
        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("tenant").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
