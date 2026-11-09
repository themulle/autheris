namespace Autheris.Tests.Unit.Hosting;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Hosting;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Modules.Trust;
using Autheris.Modules.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

internal sealed class FakeEndpointRouteBuilder : IEndpointRouteBuilder, IAsyncDisposable, IDisposable
{
    private readonly ServiceProvider _ownedProvider;

    public FakeEndpointRouteBuilder(IServiceCollection? services = null)
    {
        var svc = services ?? new ServiceCollection();
        svc.AddRouting();
        svc.AddLogging();
        svc.AddAuthorization();
        _ownedProvider = svc.BuildServiceProvider();
        ServiceProvider = _ownedProvider;
    }

    public IServiceProvider ServiceProvider { get; }

    public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

    public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);

    public void Dispose() => _ownedProvider.Dispose();

    public ValueTask DisposeAsync() => _ownedProvider.DisposeAsync();
}

internal sealed class FakeHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Production;
    public string ApplicationName { get; set; } = "Autheris.Tests";
    public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}

internal sealed class FakeAuditWriter : IAuditWriter
{
    public List<AuditLogEntry> Entries { get; } = [];

    public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }
}

public interface ISampleModuleMarker
{
    string ModuleId { get; }
    string? ConfiguredEndpoint { get; }
}

public sealed class SampleModuleMarker(string moduleId, string? configuredEndpoint) : ISampleModuleMarker
{
    public string ModuleId => moduleId;
    public string? ConfiguredEndpoint => configuredEndpoint;
}

public sealed class SampleValidModule : IAutherisModule
{
    public string Id => "sample";

    public IReadOnlyList<string> ConfigurationSections => ["Gateway:Sample"];

    public void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host)
    {
        var endpoint = moduleConfiguration["Gateway:Sample:Endpoint"];
        services.AddSingleton<ISampleModuleMarker>(new SampleModuleMarker(Id, endpoint));
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        var group = endpoints.MapGroup("/modules/sample");
        group.MapGet("/ping", () => Results.Ok(new { status = "pong" }));
        group.MapGet("/admin", () => Results.Ok(new { status = "admin-ok" }))
            .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizeAttribute { Roles = "GovernanceAdmin" });
    }
}

public sealed class AuditReplacingModule : IAutherisModule
{
    public string Id => "evil-audit";
    public IReadOnlyList<string> ConfigurationSections => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host)
    {
        services.Clear();
        services.AddSingleton<IAuditWriter>(new FakeAuditWriter());
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints) { }
}

public sealed class AuthHandlerAddingModule : IAutherisModule
{
    private sealed class NoopAuthHandler : IAuthorizationHandler
    {
        public Task HandleAsync(AuthorizationHandlerContext context)
        {
            foreach (var req in context.PendingRequirements)
            {
                context.Succeed(req);
            }
            return Task.CompletedTask;
        }
    }

    public string Id => "evil-authz";
    public IReadOnlyList<string> ConfigurationSections => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host)
    {
        services.AddSingleton<IAuthorizationHandler, NoopAuthHandler>();
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints) { }
}

public sealed class ClaimsTransformationAddingModule : IAutherisModule
{
    private sealed class EvilClaimsTransformation : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) => Task.FromResult(principal);
    }

    public string Id => "evil-claims";
    public IReadOnlyList<string> ConfigurationSections => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host)
    {
        services.AddSingleton<IClaimsTransformation, EvilClaimsTransformation>();
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints) { }
}

public sealed class ColumnMaskingReplacingModule : IAutherisModule
{
    internal sealed class NoopColumnMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => rawValue;
    }

    public string Id => "evil-masking";
    public IReadOnlyList<string> ConfigurationSections => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host)
    {
        for (int i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(IColumnMaskingProvider))
            {
                services.RemoveAt(i);
            }
        }
        services.AddSingleton<IColumnMaskingProvider, NoopColumnMaskingProvider>();
    }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints) { }
}

public sealed class AllowAnonymousEndpointModule : IAutherisModule
{
    public string Id => "anon-mod";
    public IReadOnlyList<string> ConfigurationSections => [];

    public void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) { }

    public void MapEndpoints(IModuleEndpointRegistrar endpoints)
    {
        var group = endpoints.MapGroup("/modules/anon-mod");
        group.MapGet("/open", () => "open").AllowAnonymous();
    }
}

public abstract class EvilServiceModuleBase(string id) : IAutherisModule
{
    public string Id => id;
    public virtual IReadOnlyList<string> ConfigurationSections => [];
    public abstract void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host);
    public void MapEndpoints(IModuleEndpointRegistrar endpoints) { }
}

public sealed class StartupFilterAddingModule() : EvilServiceModuleBase("evil-startup")
{
    private sealed class NoopStartupFilter : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => next;
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) =>
        services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter, NoopStartupFilter>();
}

public sealed class HostedServiceAddingModule() : EvilServiceModuleBase("evil-hosted")
{
    private sealed class NoopHostedService : Microsoft.Extensions.Hosting.IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) =>
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService, NoopHostedService>();
}

public sealed class AuthorizationOptionsConfiguringModule() : EvilServiceModuleBase("evil-authz-options")
{
    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) =>
        services.Configure<AuthorizationOptions>(options => options.InvokeHandlersAfterFailure = false);
}

public sealed class JwtOptionsPostConfiguringModule() : EvilServiceModuleBase("evil-jwt-options")
{
    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) =>
        services.PostConfigure<Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerOptions>(options => options.RequireHttpsMetadata = false);
}

public sealed class ClientIpResolverReplacingModule() : EvilServiceModuleBase("evil-client-ip")
{
    private sealed class FixedClientIpResolver : IClientIpResolver
    {
        public System.Net.IPAddress ResolveClientIp() => System.Net.IPAddress.Loopback;
    }

    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) =>
        services.AddSingleton<IClientIpResolver, FixedClientIpResolver>();
}

public sealed class DataProtectionConfiguringModule() : EvilServiceModuleBase("evil-data-protection")
{
    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) =>
        services.Configure<Microsoft.AspNetCore.DataProtection.DataProtectionOptions>(options => options.ApplicationDiscriminator = "x");
}

public sealed class ForwardedHeadersConfiguringModule() : EvilServiceModuleBase("evil-forwarded")
{
    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) =>
        services.Configure<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>(options => options.ForwardLimit = 10);
}

public sealed class ForeignSectionClaimingModule() : EvilServiceModuleBase("claim-foreign")
{
    public override IReadOnlyList<string> ConfigurationSections => ["ConnectionStrings"];
    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) { }
}

public sealed class ForeignGatewaySectionClaimingModule() : EvilServiceModuleBase("claim-gateway")
{
    public override IReadOnlyList<string> ConfigurationSections => ["Gateway:Caching"];
    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) { }
}

public sealed class CoreSectionIdModule() : EvilServiceModuleBase("caching")
{
    public override void ConfigureServices(IServiceCollection services, IConfiguration moduleConfiguration, IModuleHostContext host) { }
}

internal static class ModulePackageBuilder
{
    private static readonly Lazy<byte[]> PatchedModuleAssemblyBytes = new(CreatePatchedModuleAssemblyBytes);

    /// <summary>
    /// Produces a valid managed assembly binary containing the test module types above,
    /// with its embedded PE assembly name rewritten to <c>Sample.TestModule01</c> so it does not collide with host assemblies.
    /// </summary>
    public static byte[] GetValidModuleDllBytes() =>
        (byte[])PatchedModuleAssemblyBytes.Value.Clone();

    private static byte[] CreatePatchedModuleAssemblyBytes()
    {
        var path = typeof(ModulePackageBuilder).Assembly.Location;
        var bytes = File.ReadAllBytes(path);

        // Replace "Autheris.Tests.Unit\0" (20 bytes) with "Sample.TestModule01\0" (20 bytes) in the metadata #Strings heap.
        ReplaceBytesInPlace(
            bytes,
            Encoding.UTF8.GetBytes("Autheris.Tests.Unit\0"),
            Encoding.UTF8.GetBytes("Sample.TestModule01\0"));

        ReplaceBytesInPlace(
            bytes,
            Encoding.UTF8.GetBytes("Autheris.Tests.Unit.dll\0"),
            Encoding.UTF8.GetBytes("Sample.TestModule01.dll\0"));

        return bytes;
    }

    private static void ReplaceBytesInPlace(byte[] buffer, ReadOnlySpan<byte> pattern, ReadOnlySpan<byte> replacement)
    {
        if (pattern.Length != replacement.Length)
        {
            throw new ArgumentException("Pattern and replacement must have identical byte length.");
        }

        for (int i = 0; i <= buffer.Length - pattern.Length; i++)
        {
            if (buffer.AsSpan(i, pattern.Length).SequenceEqual(pattern))
            {
                replacement.CopyTo(buffer.AsSpan(i, replacement.Length));
            }
        }
    }

    public static ModuleManifest CreateSignedManifest(
        ECDsa signingKey,
        string moduleId = "sample",
        string entryAssembly = "Sample.TestModule01.dll",
        string? entryType = null,
        IReadOnlyList<ModuleManifestFile>? files = null,
        string version = "1.2.0",
        string? hostVersion = null,
        string abstractionsVersion = "1.0.0",
        string? keyIdOverride = null,
        string? signatureOverride = null)
    {
        var effectiveEntryType = entryType ?? typeof(SampleValidModule).FullName!;
        var keyId = keyIdOverride ?? ManifestSignatureVerifier.ComputeKeyId(signingKey);
        var unsignedManifest = new ModuleManifest(
            Schema: 1,
            Id: moduleId,
            Version: version,
            HostVersion: hostVersion ?? ModuleLoader.ResolveHostVersion(),
            AbstractionsVersion: abstractionsVersion,
            EntryAssembly: entryAssembly,
            EntryType: effectiveEntryType,
            Files: files ?? [],
            KeyId: keyId,
            Signature: string.Empty);

        var canonical = ManifestCanonicalizer.ToCanonicalBytes(unsignedManifest);
        var signature = signatureOverride ?? ManifestSignatureVerifier.Sign(canonical, signingKey);
        return unsignedManifest with { Signature = signature };
    }

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string SerializeManifestJson(ModuleManifest manifest)
    {
        var obj = new Dictionary<string, object?>
        {
            ["schema"] = manifest.Schema,
            ["id"] = manifest.Id,
            ["version"] = manifest.Version,
            ["hostVersion"] = manifest.HostVersion,
            ["abstractionsVersion"] = manifest.AbstractionsVersion,
            ["entryAssembly"] = manifest.EntryAssembly,
            ["entryType"] = manifest.EntryType,
            ["files"] = manifest.Files,
            ["keyId"] = manifest.KeyId,
            ["signature"] = manifest.Signature,
        };
        return JsonSerializer.Serialize(obj, ManifestJsonOptions);
    }

    public static void SetReadOnlyPermissionsIfUnix(string directoryPath)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                File.SetUnixFileMode(
                    directoryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
            catch (PlatformNotSupportedException)
            {
            }
        }
        else
        {
            File.SetAttributes(directoryPath, File.GetAttributes(directoryPath) | FileAttributes.ReadOnly);
        }
    }

    public static void RestoreWritablePermissionsRecursively(string rootPath)
    {
        if (!Directory.Exists(rootPath))
        {
            return;
        }

        var dirs = new List<string> { rootPath };
        try
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                File.SetUnixFileMode(rootPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            else
            {
                File.SetAttributes(rootPath, File.GetAttributes(rootPath) & ~FileAttributes.ReadOnly);
            }

            dirs.AddRange(Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories));
            foreach (var dir in dirs)
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                else
                {
                    File.SetAttributes(dir, File.GetAttributes(dir) & ~FileAttributes.ReadOnly);
                }
            }
        }
        catch
        {
        }
    }
}
