using System.Text;
using Autheris.Api.Configuration;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;

namespace Autheris.Api.Extensions;

/// <summary>
/// F-AUTH-DX: Development-only startup banner with the links and personas a developer needs first.
/// </summary>
public static class DevStartupBanner
{
    public static string Build(IReadOnlyCollection<string> urls, GatewayOptions options, IHostEnvironment env, DevConfigurationReport? report = null)
    {
        var baseUrl = (urls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) ?? urls.FirstOrDefault() ?? "http://localhost:5031")
            .TrimEnd('/')
            .Replace("://0.0.0.0", "://localhost", StringComparison.Ordinal)
            .Replace("://[::]", "://localhost", StringComparison.Ordinal)
            .Replace("://*", "://localhost", StringComparison.Ordinal);

        var sb = new StringBuilder();
        sb.AppendLine().AppendLine("Autheris developer mode");
        sb.AppendLine($"  Hub          {baseUrl}/");
        sb.AppendLine($"  GraphQL      {baseUrl}{options.GraphQL.EndpointPath}");
        if (options.GraphQL.EnableBananaCakePop)
        {
            sb.AppendLine($"  GraphQL IDE  {baseUrl}{options.GraphQL.BananaCakePopPath}");
        }
        sb.AppendLine($"  Swagger      {baseUrl}/docs");
        sb.AppendLine($"  Identity     {baseUrl}/api/auth/session");
        sb.AppendLine($"  Config       {baseUrl}/api/dev/info");
        sb.AppendLine($"  Health       {baseUrl}/health");

        var features = DevFeatures.Resolve(options, env.IsDevelopment());
        sb.AppendLine($"  Preset       {options.Dev.Preset}" + (options.GovernanceDb.ConnectionString.Contains("Mode=Memory", StringComparison.OrdinalIgnoreCase)
            ? " (in-memory database)"
            : features.PersistDb ? " (persistent database)" : string.Empty));
        if (features.DevSecurity.Count > 0)
        {
            sb.AppendLine($"  Dev security {string.Join(", ", features.DevSecurity)}");
        }

        foreach (var note in report?.Notes ?? [])
        {
            sb.AppendLine($"  Note         {note}");
        }

        var basic = options.Authentication.BasicAuth;
        if (!basic.Enabled || basic.Users.Count == 0)
        {
            return sb.ToString();
        }

        var sessionActive = BasicAuthSession.IsAllowed(options, env);
        sb.AppendLine().AppendLine(sessionActive
            ? "  Personas (open the login link once, the session cookie does the rest):"
            : "  Personas (Basic login; session cookie inactive):");
        foreach (var user in basic.Users)
        {
            // Plaintext passwords are only accepted in Development; hashed ones are never printed.
            var password = user.Password.StartsWith("$pbkdf2$", StringComparison.Ordinal) ? "<hashed>" : user.Password;
            var roles = user.Roles.Count == 0 ? "-" : string.Join(",", user.Roles);
            sb.Append($"    {user.Username,-12} pw {password,-8} {roles,-14} tenant {BasicAuthPrincipalFactory.ResolveTenant(user)}");
            if (sessionActive)
            {
                var redirectPath = options.GraphQL.EnableBananaCakePop
                    ? options.GraphQL.BananaCakePopPath
                    : options.GraphQL.EndpointPath;
                sb.Append($"  {baseUrl}/api/dev/login/{Uri.EscapeDataString(user.Username)}?redirect={redirectPath}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
