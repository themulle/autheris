using System;
using Autheris.Api.Extensions;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;
if (args.Length > 0 && (args[0] == "hash-password" || args[0] == "--hash-password"))
{
    Environment.ExitCode = Autheris.Api.Security.PasswordHashCli.Run(args);
    return;
}

if (args.Length > 0 && (args[0] == "healthcheck" || args[0] == "--healthcheck"))
{
    Environment.ExitCode = await Autheris.Api.Security.HealthCheckCli.RunAsync(args);
    return;
}

var builder = WebApplication.CreateBuilder(args);

// SEC M-01: Kestrel limits (configurable via Gateway:Hosting). Global body limit is small (default 2 MB);
// endpoints with large payloads (dbt sync) raise it explicitly via RequestSizeLimitAttribute.
var hostingLimits = builder.Configuration.GetSection($"{GatewayOptions.SectionName}:Hosting").Get<HostingLimitsOptions>()
    ?? new HostingLimitsOptions();

// Configure Kestrel limits & high-throughput concurrency settings (F-PERF / graphql-bench)
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = hostingLimits.MaxRequestBodySizeBytes;
    options.AddServerHeader = false;
    options.Limits.MaxConcurrentConnections = hostingLimits.MaxConcurrentConnections > 0
        ? hostingLimits.MaxConcurrentConnections
        : null;
    options.Limits.MaxConcurrentUpgradedConnections = hostingLimits.MaxConcurrentUpgradedConnections;
    options.Limits.Http2.MaxStreamsPerConnection = 1024;
    options.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(2);
    options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
    options.AllowSynchronousIO = false;
});

// 1. Serilog Setup
// DEP-8: code defaults first (Microsoft.AspNetCore at Warning, query strings redacted), then the Serilog section.
builder.Host.UseSerilog((ctx, lc) => Autheris.Api.Logging.SensitiveLogPropertyEnricher.ApplyDefaults(lc)
    .ReadFrom.Configuration(ctx.Configuration, new Serilog.Settings.Configuration.ConfigurationReaderOptions(typeof(Program).Assembly))
    .Enrich.FromLogContext()
    .WriteTo.Console());

// 2. DI Container Validation
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = builder.Environment.IsDevelopment();
    options.ValidateOnBuild = true;
});

// 3a. F-AUTH-DX: resolve Gateway:Dev (preset defaults, legacy aliases, persistence) before the options are bound
Autheris.Api.Configuration.LegacySwitchGuard.ThrowIfLegacyKeysSet(builder.Configuration);
var devReport = Autheris.Api.Configuration.DevConfiguration.Apply(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(devReport);

// 3. Modular Service Registrations
var gatewayOptions = builder.Services.AddGatewayOptions(builder.Configuration, builder.Environment);
builder.Services.AddGatewayInfrastructure(gatewayOptions, builder.Environment);
builder.Services.AddGatewayAuth(gatewayOptions, builder.Environment);
// One switch for all sample content (demo catalog, finance/hr GraphQL types, demo MCP tools, golden queries)
var demoDataEnabled = Autheris.Domain.Options.DemoDataSwitch.Resolve(gatewayOptions, builder.Environment.EnvironmentName);
builder.Services.AddSingleton<Autheris.Domain.Options.IDemoDataSwitch>(new Autheris.Domain.Options.DemoDataSwitch(demoDataEnabled));
builder.Services.AddGatewayGraphQL(gatewayOptions, demoDataEnabled);

// 4. Build and Pipeline Configuration
var app = builder.Build();

app.UseGatewayPipeline(gatewayOptions);
app.MapGatewayEndpoints(gatewayOptions);

LogAuthStartupConfiguration(app, gatewayOptions, devReport);

app.Run();

static void LogAuthStartupConfiguration(WebApplication app, GatewayOptions options, Autheris.Api.Configuration.DevConfigurationReport devReport)
{
    var logger = app.Services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Program>>();
    var auth = options.Authentication;
    var gql = options.GraphQL;

    logger.LogInformation(
        "Autheris Gateway starting in {Environment} environment. Effective Auth Schemes: BasicAuth={BasicEnabled} (Users={UserCount}, Realm='{Realm}'), ForwardAuth={ForwardAuthEnabled}, EntraId={EntraIdEnabled}, Adfs={AdfsEnabled}, RequireKerberosOnly={KerberosOnly}. GraphQL Tooling: EnableBananaCakePop={EnableBananaCakePop}",
        app.Environment.EnvironmentName,
        auth.BasicAuth.Enabled,
        auth.BasicAuth.Users.Count,
        auth.BasicAuth.Realm,
        auth.ForwardAuth.Enabled,
        auth.EntraId.Enabled,
        auth.Adfs.Enabled,
        auth.RequireKerberosOnly,
        gql.EnableBananaCakePop);

    foreach (var note in devReport.Notes)
    {
        logger.LogWarning("Dev configuration: {Note}", note);
    }

    var devFeatures = DevFeatures.Resolve(options, app.Environment.IsDevelopment());
    if (devFeatures.Banner)
    {
        app.Lifetime.ApplicationStarted.Register(() =>
            logger.LogInformation("{Banner}", Autheris.Api.Extensions.DevStartupBanner.Build(app.Urls.ToList(), options, app.Environment, devReport)));
    }

    if (app.Configuration is IConfigurationRoot configRoot)
    {
        foreach (var provider in configRoot.Providers)
        {
            if (provider is Microsoft.Extensions.Configuration.FileConfigurationProvider fileProvider &&
                fileProvider.Source.Path is { } path)
            {
                if (System.IO.Directory.Exists(path))
                {
                    logger.LogWarning("Mounted configuration source '{Path}' is a directory, expected a file! Check Docker Compose volume mount paths.", path);
                }
            }
        }
    }
}

// Make Program class accessible for WebApplicationFactory in integration tests
public partial class Program { }
