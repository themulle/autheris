namespace Autheris.Api.Security;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

public sealed class JwtSocketTokenValidator : ISocketTokenValidator
{
    private readonly IOptionsMonitor<JwtBearerOptions> _jwtOptionsMonitor;
    private readonly IOptions<GatewayOptions> _gatewayOptions;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<JwtSocketTokenValidator> _logger;
    private readonly JsonWebTokenHandler _tokenHandler = new();

    public JwtSocketTokenValidator(
        IOptionsMonitor<JwtBearerOptions> jwtOptionsMonitor,
        IOptions<GatewayOptions> gatewayOptions,
        IHostEnvironment environment,
        ILogger<JwtSocketTokenValidator> logger)
    {
        _jwtOptionsMonitor = jwtOptionsMonitor ?? throw new ArgumentNullException(nameof(jwtOptionsMonitor));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<(bool IsValid, ClaimsPrincipal? Principal)> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, null);
        }

        try
        {
            var jwtOptions = _jwtOptionsMonitor.Get(GatewayAuthSchemes.SelectJwtScheme(token, _gatewayOptions.Value));
            var validationParameters = jwtOptions.TokenValidationParameters?.Clone();

            if (validationParameters != null)
            {
                // SEC H-2 / Low-17: Resolve dynamic signing keys from OIDC metadata ConfigurationManager if not statically configured
                if (validationParameters.IssuerSigningKey == null &&
                    (validationParameters.IssuerSigningKeys == null || !validationParameters.IssuerSigningKeys.Any()) &&
                    jwtOptions.ConfigurationManager != null)
                {
                    try
                    {
                        var config = await jwtOptions.ConfigurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
                        if (config?.SigningKeys != null && config.SigningKeys.Count > 0)
                        {
                            validationParameters.IssuerSigningKeys = config.SigningKeys;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to dynamically resolve JWT signing keys from ConfigurationManager for WebSocket auth.");
                    }
                }

                if (validationParameters.IssuerSigningKey != null || (validationParameters.IssuerSigningKeys != null && validationParameters.IssuerSigningKeys.Any()))
                {
                    var validationResult = await _tokenHandler.ValidateTokenAsync(token, validationParameters);
                    if (!validationResult.IsValid || validationResult.ClaimsIdentity == null)
                    {
                        _logger.LogWarning(validationResult.Exception, "WebSocket JWT cryptographic validation failed: {Reason}", validationResult.Exception?.Message);
                        return (false, null);
                    }

                    var identity = validationResult.ClaimsIdentity;
                    var sub = identity.FindFirst("sub")?.Value ?? identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;

                    if (!string.IsNullOrWhiteSpace(sub) &&
                        (sub.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase) || sub.EndsWith("-500", StringComparison.OrdinalIgnoreCase)))
                    {
                        _logger.LogWarning("WebSocket connection rejected: Attempted use of privileged SID '{Sub}'.", sub);
                        return (false, null);
                    }

                    return (true, new ClaimsPrincipal(identity));
                }

                _logger.LogWarning("WebSocket token rejected: No cryptographic IssuerSigningKey configured.");
                return (false, null);
            }

            return (false, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during WebSocket token validation.");
            return (false, null);
        }
    }
}
