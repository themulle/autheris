using System;
using Autheris.Api.Extensions;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Serilog;

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
builder.Host.UseSerilog((ctx, lc) => lc
    .ReadFrom.Configuration(ctx.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console());

// 2. DI Container Validation
builder.Host.UseDefaultServiceProvider(options =>
{
    options.ValidateScopes = true;
    options.ValidateOnBuild = true;
});

// 3. Modular Service Registrations
var gatewayOptions = builder.Services.AddGatewayOptions(builder.Configuration, builder.Environment);
builder.Services.AddGatewayInfrastructure(gatewayOptions);
builder.Services.AddGatewayAuth(gatewayOptions, builder.Environment);
builder.Services.AddGatewayGraphQL(gatewayOptions);

// 4. Build and Pipeline Configuration
var app = builder.Build();

app.UseGatewayPipeline(gatewayOptions);
app.MapGatewayEndpoints(gatewayOptions);

app.Run();

// Make Program class accessible for WebApplicationFactory in integration tests
public partial class Program { }
