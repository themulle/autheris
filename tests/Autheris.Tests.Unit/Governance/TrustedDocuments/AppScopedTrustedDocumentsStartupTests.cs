namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using System.ComponentModel.DataAnnotations;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP3: startup validation of the app-scoped switch (REQ-281-11) and the Helm exposure check (SEC-281-6).</summary>
[Trait("Category", "Security")]
public sealed class AppScopedTrustedDocumentsStartupTests
{
    private static IHostEnvironment Env()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return env;
    }

    private static GatewayOptions Options(bool appScoped, bool persistedOnly, string directory = "") => new()
    {
        GraphQL = new GraphQLOptions
        {
            PersistedQueriesOnly = persistedOnly,
            TrustedDocumentsDirectory = directory,
            AppScopedTrustedDocuments = new AppScopedTrustedDocumentsOptions { Enabled = appScoped }
        }
    };

    [Fact]
    public void Enabled_WithoutPersistedQueriesOnly_FailsClosed() =>
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateStartup(Options(true, false), Env(), _ => null));

    [Fact]
    public void Enabled_WithPersistedQueriesOnly_AllowsEmptyDirectory() =>
        Should.NotThrow(() => GatewayStartupValidator.ValidateStartup(Options(true, true), Env(), _ => null));

    [Fact]
    public void Enabled_WithMissingNonEmptyDirectory_StillFails() =>
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateStartup(
            Options(true, true, Path.Combine(Path.GetTempPath(), "autheris-missing-" + Guid.NewGuid().ToString("N"))), Env(), _ => null));

    [Fact]
    public void Disabled_PersistedQueriesOnly_StillRequiresDirectory() =>
        Should.Throw<ValidationException>(() => GatewayStartupValidator.ValidateStartup(Options(false, true), Env(), _ => null));

    [Fact]
    public void Registry_And_AdminService_AreRegisteredAsSingletons()
    {
        var services = new ServiceCollection();
        services.AddAutherisGovernance(new GatewayOptions());
        services.ShouldContain(d => d.ServiceType == typeof(ITrustedDocumentAppRegistry) && d.Lifetime == ServiceLifetime.Singleton);
        services.ShouldContain(d => d.ServiceType == typeof(TrustedDocumentAdministrationService) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void Helm_DefaultIngress_DeniesApiAdmin_ToEndpointlessService()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Autheris.sln")))
        {
            root = root.Parent;
        }

        root.ShouldNotBeNull();
        var chart = Path.Combine(root!.FullName, "deploy", "helm", "autheris");
        File.ReadAllText(Path.Combine(chart, "values.yaml")).ShouldContain("exposeAdmin: false");

        var ingress = File.ReadAllText(Path.Combine(chart, "templates", "ingress-traefik.yaml"));
        ingress.ShouldContain("if $denyAdmin");
        ingress.ShouldContain("-admin-blocked");

        // The only chart file that may mention /api/admin is the guarded ingress deny rule; the target Service has no pod match.
        foreach (var file in Directory.EnumerateFiles(chart, "*.yaml", SearchOption.AllDirectories)
                     .Where(f => Path.GetFileName(f) != "ingress-traefik.yaml"))
        {
            File.ReadAllText(file).ShouldNotContain("/api/admin", Case.Insensitive, $"{Path.GetFileName(file)} must not route /api/admin (SEC-281-6)");
        }

        File.ReadAllText(Path.Combine(chart, "templates", "service-admin-blocked.yaml")).ShouldNotContain("selector");
    }
}
