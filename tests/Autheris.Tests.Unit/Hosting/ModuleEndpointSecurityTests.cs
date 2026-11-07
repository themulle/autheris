namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Hosting;
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
    public void Module_graphql_type_without_authorization_fails_startup()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"autheris-mod-gql-{Guid.NewGuid():N}");
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        try
        {
            var modDir = Path.Combine(tempRoot, "gql-unprotected");
            Directory.CreateDirectory(modDir);

            var dllBytes = ModulePackageBuilder.GetValidModuleDllBytes();
            var dllSha = IntegrityHashHelper.ComputeSha256HexLower(dllBytes);
            File.WriteAllBytes(Path.Combine(modDir, "Sample.TestModule01.dll"), dllBytes);

            var manifest = ModulePackageBuilder.CreateSignedManifest(
                ecdsa,
                moduleId: "gql-unprotected",
                entryType: typeof(UnprotectedGraphQLModule).FullName!,
                files: [new ModuleManifestFile("Sample.TestModule01.dll", dllSha)]);
            File.WriteAllText(Path.Combine(modDir, "module.json"), ModulePackageBuilder.SerializeManifestJson(manifest));

            var services = new ServiceCollection();
            var config = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Gateway:Modules:Path"] = tempRoot,
                    ["Gateway:Modules:Enabled:0"] = "gql-unprotected",
                })
                .Build();
            var env = new FakeHostEnvironment { EnvironmentName = Environments.Production };
            var loader = new ModuleLoader(publicKeyOverride: ecdsa, isDirectoryWritableOverride: _ => false);

            var loadResult = loader.LoadAndConfigureServices(services, config, env);

            var ex = Should.Throw<ModuleLoadException>(() =>
                loadResult.ConfigureGraphQL());

            ex.ModuleId.ShouldBe("gql-unprotected");
            ex.Message.ShouldContain("[Authorize]");
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
    public void ModuleGraphQLRegistrar_accepts_type_with_ExtendObjectType_and_Authorize()
    {
        var registrar = new ModuleGraphQLRegistrar("sample");
        registrar.AddTypeExtension(typeof(ProtectedGraphQLExtension));
        registrar.RegisteredTypeExtensions.ShouldContain(typeof(ProtectedGraphQLExtension));
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
    [Trait("SecurityCondition", "SEC-50-04")]
    public void Audit_details_exclude_query_string()
    {
        var pathWithoutQuery = "/api/resource";
        var detailsJson = ModuleEndpointRegistrar.BuildModuleAuditDetails("sample", pathWithoutQuery);

        detailsJson.ShouldNotContain("?");
        detailsJson.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    [Trait("SecurityCondition", "SEC-50-04")]
    public void Audit_target_is_length_capped()
    {
        var longTarget = "/" + new string('y', 2048);
        var cappedTarget = ModuleEndpointRegistrar.CapAuditString(longTarget, 512);
        cappedTarget.Length.ShouldBeLessThanOrEqualTo(512);
    }
}
