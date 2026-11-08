namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Hosting;
using Autheris.Application.Audit;
using Autheris.Domain.Audit;
using Autheris.Domain.Options.DataSources;
using Autheris.Infrastructure.Modules.Trust;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ModuleEndpointSecurityTests
{
    [Fact]
    public void Module_endpoint_with_AllowAnonymous_fails_startup()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"autheris-mod-ep-anon-{Guid.NewGuid():N}");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        try
        {
            var modDir = Path.Combine(tempRoot, "anon-mod");
            Directory.CreateDirectory(modDir);

            var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
            var dllSha = IntegrityHashHelper.ComputeSha256HexLower(dllBytes);
            File.WriteAllBytes(Path.Combine(modDir, "Sample.TestModule01.dll"), dllBytes);

            var manifest = ModulePackageBuilder.CreateSignedManifest(
                ecdsa,
                moduleId: "anon-mod",
                entryType: typeof(AllowAnonymousEndpointModule).FullName!,
                files: [new ModuleManifestFile("Sample.TestModule01.dll", dllSha)]);
            File.WriteAllText(Path.Combine(modDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));

            var services = new ServiceCollection();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Modules:Path"] = tempRoot,
                    ["Gateway:Modules:Enabled:0"] = "anon-mod",
                })
                .Build();
            var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };
            var loader = new ModuleLoader(publicKeyOverride: ecdsa, isDirectoryWritableOverride: _ => false);

            var loadResult = loader.LoadAndConfigureServices(services, config, env);

            using var app = new FakeEndpointRouteBuilder(services);

            var ex = Should.Throw<ModuleLoadException>(() =>
                loadResult.MapEndpoints(app));

            ex.ModuleId.ShouldBe("anon-mod");
            ex.Message.ShouldContain("AllowAnonymous");
        }
        finally
        {
            ModulePackageBuilder.RestoreWritablePermissionsRecursively(tempRoot);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Module_endpoint_without_authorization_metadata_fails_startup()
    {
        using var app = new FakeEndpointRouteBuilder();

        // Simulate a module route that bypassed RequireAuthorization() or stripped IAuthorizeData
        IEndpointRouteBuilder endpoints = app;
        endpoints.MapGet("/api/v1/rogue/unprotected", () => Results.Ok())
            .WithMetadata(new ModuleEndpointMetadata("rogue-mod"))
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithAudit(AuditLevel.Full, AuditEventTypes.CatalogRead);

        var ex = Should.Throw<ModuleLoadException>(() =>
            ModuleEndpointRegistrar.VerifyModuleEndpoints(endpoints, "rogue-mod"));

        ex.ModuleId.ShouldBe("rogue-mod");
        ex.Message.ShouldContain("authorization metadata");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Audit_details_escape_request_path()
    {
        var complexPath = "/api/\"test\"/\\path";
        var detailsJson = ModuleEndpointRegistrar.BuildModuleAuditDetails("sample", complexPath);

        using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
        var root = doc.RootElement;
        root.EnumerateObject().Count().ShouldBe(2);
        root.GetProperty("module").GetString().ShouldBe("sample");
        root.GetProperty("path").GetString().ShouldBe(complexPath);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Audit_details_path_is_length_capped()
    {
        var longPath = "/" + new string('x', 2048);
        var detailsJson = ModuleEndpointRegistrar.BuildModuleAuditDetails("sample", longPath);

        using var doc = System.Text.Json.JsonDocument.Parse(detailsJson);
        var pathValue = doc.RootElement.GetProperty("path").GetString().ShouldNotBeNull();
        pathValue.Length.ShouldBeLessThanOrEqualTo(512);
        pathValue.ShouldBe(longPath[..512]);
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-50-04")]
    public void Audit_details_exclude_query_string()
    {
        var pathWithoutQuery = "/api/resource";
        var detailsJson = ModuleEndpointRegistrar.BuildModuleAuditDetails("sample", pathWithoutQuery);

        detailsJson.ShouldNotContain("?");
        detailsJson.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-SIMPL-50-04")]
    public void Audit_target_is_length_capped()
    {
        var longTarget = "/" + new string('y', 2048);
        var cappedTarget = ModuleEndpointRegistrar.CapAuditString(longTarget, 512);
        cappedTarget.Length.ShouldBeLessThanOrEqualTo(512);
    }

    // ---- B4: route namespace (SEC-50-08) ----

    [Fact]
    public void Module_route_outside_prefix_is_rejected()
    {
        using var app = new FakeEndpointRouteBuilder();
        var registrar = new ModuleEndpointRegistrar(app, "sample");

        var ex = Should.Throw<ModuleLoadException>(() => registrar.MapGroup("/api/x"));
        ex.ModuleId.ShouldBe("sample");
        Should.Throw<ModuleLoadException>(() => registrar.MapGroup("/modules/other/x"));
        Should.Throw<ModuleLoadException>(() => registrar.MapGroup("/modules/samplex/x"));
        Should.Throw<ModuleLoadException>(() => registrar.MapGroup("/modules/Sample/x"));
        Should.NotThrow(() => registrar.MapGroup("/modules/sample/x"));
    }

    [Theory]
    [InlineData("/modules/sample/../api")]
    [InlineData("/modules/sample/./x")]
    [InlineData("/modules/sample//x")]
    public void Module_route_with_dot_segments_is_rejected(string prefix)
    {
        using var app = new FakeEndpointRouteBuilder();
        var registrar = new ModuleEndpointRegistrar(app, "sample");

        Should.Throw<ModuleLoadException>(() => registrar.MapGroup(prefix));
    }

    [Fact]
    public void Endpoint_without_module_ownership_fails_validation()
    {
        using var app = new FakeEndpointRouteBuilder();
        IEndpointRouteBuilder endpoints = app;
        var before = ModuleEndpointRegistrar.CaptureEndpointSnapshot(endpoints);

        endpoints.MapGet("/modules/sample/unowned", () => Results.Ok());

        var ex = Should.Throw<ModuleLoadException>(() =>
            ModuleEndpointRegistrar.VerifyModuleEndpoints(endpoints, "sample", before));
        ex.ModuleId.ShouldBe("sample");
    }

    [Fact]
    public void Endpoint_owned_by_another_module_fails_validation()
    {
        using var app = new FakeEndpointRouteBuilder();
        IEndpointRouteBuilder endpoints = app;
        var before = ModuleEndpointRegistrar.CaptureEndpointSnapshot(endpoints);

        endpoints.MapGet("/modules/sample/x", () => Results.Ok()).WithMetadata(new ModuleEndpointMetadata("other"));

        Should.Throw<ModuleLoadException>(() =>
            ModuleEndpointRegistrar.VerifyModuleEndpoints(endpoints, "sample", before));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-08")]
    public void Encoded_dot_segment_route_is_rejected()
    {
        using var app = new FakeEndpointRouteBuilder();
        var registrar = new ModuleEndpointRegistrar(app, "sample");

        Should.Throw<ModuleLoadException>(() => registrar.MapGroup("/modules/sample/%2e%2e/api"));
        Should.Throw<ModuleLoadException>(() => registrar.MapGroup("/modules/sample/%2E%2E%2fapi"));
        Should.Throw<ModuleLoadException>(() => registrar.MapGroup("/modules/sample%2f..%2fapi"));

        // The same rules apply to the raw pattern of every endpoint a module added.
        IEndpointRouteBuilder endpoints = app;
        var before = ModuleEndpointRegistrar.CaptureEndpointSnapshot(endpoints);
        endpoints.MapGet("/modules/sample/%2e%2e/api", () => Results.Ok()).WithMetadata(new ModuleEndpointMetadata("sample"));
        Should.Throw<ModuleLoadException>(() =>
            ModuleEndpointRegistrar.VerifyModuleEndpoints(endpoints, "sample", before));
    }

    // ---- B11: audit decision (SEC-50-09) ----

    private static (DefaultEndpointFilterInvocationContext Context, FakeAuditWriter Writer) NewFilterContext(bool authenticated = true)
    {
        var writer = new FakeAuditWriter();
        var services = new ServiceCollection().AddSingleton<IAuditWriter>(writer).BuildServiceProvider();
        var identity = authenticated
            ? new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-1"), new Claim("tenant_id", "tenant-a")],
                authenticationType: "TestAuth")
            : new ClaimsIdentity();
        var httpContext = new DefaultHttpContext { RequestServices = services, User = new ClaimsPrincipal(identity) };
        httpContext.Request.Path = "/modules/sample/x";
        return (new DefaultEndpointFilterInvocationContext(httpContext), writer);
    }

    private static EndpointFilterDelegate Returning(object? value) => _ => ValueTask.FromResult(value);

    [Fact]
    public async Task Forbidden_result_is_audited_as_deny()
    {
        var (context, writer) = NewFilterContext();

        await ModuleEndpointRegistrar.ExecuteModuleSecurityFilterAsync(
            "sample", context, Returning(Results.StatusCode(StatusCodes.Status403Forbidden)));

        writer.Entries.Count.ShouldBe(1);
        writer.Entries[0].Decision.ShouldBe("DENY");
    }

    [Fact]
    public async Task Throwing_handler_is_audited_as_error()
    {
        var (context, writer) = NewFilterContext();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ModuleEndpointRegistrar.ExecuteModuleSecurityFilterAsync(
                "sample", context, _ => throw new InvalidOperationException("boom")));

        writer.Entries.Count.ShouldBe(1);
        writer.Entries[0].Decision.ShouldBe("ERROR");
    }

    [Fact]
    public async Task Unauthenticated_request_is_audited_as_deny()
    {
        var (context, writer) = NewFilterContext(authenticated: false);
        var reached = false;

        var result = await ModuleEndpointRegistrar.ExecuteModuleSecurityFilterAsync(
            "sample", context, _ => { reached = true; return ValueTask.FromResult<object?>(null); });

        reached.ShouldBeFalse();
        var statusResult = result.ShouldBeAssignableTo<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>().ShouldNotBeNull();
        statusResult.StatusCode.ShouldBe(StatusCodes.Status401Unauthorized);
        writer.Entries.Count.ShouldBe(1);
        writer.Entries[0].Decision.ShouldBe("DENY");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-09")]
    public async Task Error_audit_has_no_exception_message()
    {
        var (context, writer) = NewFilterContext();

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ModuleEndpointRegistrar.ExecuteModuleSecurityFilterAsync(
                "sample", context, _ => throw new InvalidOperationException("secret-value-123")));

        var entry = writer.Entries.ShouldHaveSingleItem();
        entry.DetailsJson.ShouldNotContain("secret-value-123");
        entry.DetailsJson.ShouldContain(nameof(InvalidOperationException));
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-09")]
    public async Task Audit_written_once_per_request()
    {
        var (ok, okWriter) = NewFilterContext();
        await ModuleEndpointRegistrar.ExecuteModuleSecurityFilterAsync("sample", ok, Returning(Results.Ok()));
        okWriter.Entries.ShouldHaveSingleItem().Decision.ShouldBe("ALLOW");

        var (denied, deniedWriter) = NewFilterContext();
        await ModuleEndpointRegistrar.ExecuteModuleSecurityFilterAsync(
            "sample", denied, Returning(Results.StatusCode(StatusCodes.Status403Forbidden)));
        deniedWriter.Entries.ShouldHaveSingleItem().Decision.ShouldBe("DENY");

        var (thrown, thrownWriter) = NewFilterContext();
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await ModuleEndpointRegistrar.ExecuteModuleSecurityFilterAsync(
                "sample", thrown, _ => throw new InvalidOperationException("x")));
        thrownWriter.Entries.ShouldHaveSingleItem().Decision.ShouldBe("ERROR");
    }
}
