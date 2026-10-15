namespace Autheris.Api.Middleware;

using System.IO;
using System.Linq;
using Autheris.Api.Configuration;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Extracted startup banner and authentication configuration logging helper (WP-ARC-C-04).
/// </summary>
public static class GatewayStartupBannerLog
{
    public static void LogStartupBanner(WebApplication app, GatewayOptions options, DevConfigurationReport? devReport = null)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Autheris.Api.Startup");
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

        if (devReport != null)
        {
            foreach (var note in devReport.Notes)
            {
                logger.LogWarning("Dev configuration: {Note}", note);
            }
        }

        var devFeatures = DevFeatures.Resolve(options, app.Environment.IsDevelopment());
        if (devFeatures.Banner)
        {
            app.Lifetime.ApplicationStarted.Register(() =>
                logger.LogInformation("{Banner}", DevStartupBanner.Build(app.Urls.ToList(), options, app.Environment, devReport)));
        }

        if (app.Configuration is IConfigurationRoot configRoot)
        {
            foreach (var provider in configRoot.Providers)
            {
                if (provider is FileConfigurationProvider fileProvider &&
                    fileProvider.Source.Path is { } path &&
                    Directory.Exists(path))
                {
                    logger.LogWarning("Mounted configuration source '{Path}' is a directory, expected a file! Check Docker Compose volume mount paths.", path);
                }
            }
        }
    }
}
