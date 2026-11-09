namespace Autheris.Api.Security;

using System;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Identity;
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
    private readonly IIdentitySubjectResolver _subjectResolver;
    private readonly IIdentityPipeline _pipeline;
    private readonly JsonWebTokenHandler _tokenHandler = new();

    /// <summary>
    /// The token policy resolver and the identity pipeline are required: there is no path that accepts a token without the Entra or AD FS
    /// policy and without the same group resolution as HTTP.
    /// </summary>
    public JwtSocketTokenValidator(
        IOptionsMonitor<JwtBearerOptions> jwtOptionsMonitor,
        IOptions<GatewayOptions> gatewayOptions,
        IHostEnvironment environment,
        ILogger<JwtSocketTokenValidator> logger,
        IIdentitySubjectResolver subjectResolver,
        IIdentityPipeline pipeline)
    {
        _jwtOptionsMonitor = jwtOptionsMonitor ?? throw new ArgumentNullException(nameof(jwtOptionsMonitor));
        _gatewayOptions = gatewayOptions ?? throw new ArgumentNullException(nameof(gatewayOptions));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _subjectResolver = subjectResolver ?? throw new ArgumentNullException(nameof(subjectResolver));
        _pipeline = pipeline ?? throw new ArgumentNullException(nameof(pipeline));
    }

    public async Task<(bool IsValid, ClaimsPrincipal? Principal)> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return (false, null);
        }

        try
        {
            var scheme = GatewayAuthSchemes.SelectJwtScheme(token, _gatewayOptions.Value);
            var jwtOptions = _jwtOptionsMonitor.Get(scheme);
            var validationParameters = jwtOptions.TokenValidationParameters?.Clone();
            if (validationParameters == null)
            {
                return (false, null);
            }

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

            if (validationParameters.IssuerSigningKey == null && (validationParameters.IssuerSigningKeys == null || !validationParameters.IssuerSigningKeys.Any()))
            {
                _logger.LogWarning("WebSocket token rejected: No cryptographic IssuerSigningKey configured.");
                return (false, null);
            }

            var validationResult = await _tokenHandler.ValidateTokenAsync(token, validationParameters);
            if (!validationResult.IsValid || validationResult.ClaimsIdentity == null)
            {
                _logger.LogWarning(validationResult.Exception, "WebSocket JWT cryptographic validation failed: {Reason}", validationResult.Exception?.Message);
                return (false, null);
            }

            // The token policy is selected by the issuer exactly like on the HTTP path: Entra tokens meet the Entra policy, AD FS tokens the
            // AD FS client allowlist.
            var rawPrincipal = new ClaimsPrincipal(validationResult.ClaimsIdentity);
            var failure = TokenPolicySelector.Apply(rawPrincipal, validationResult.SecurityToken?.Issuer, _gatewayOptions.Value.Authentication, _subjectResolver);
            if (failure != null)
            {
                _logger.LogWarning("WebSocket connection rejected: token policy validation failed: {Reason}", failure);
                return (false, null);
            }

            // The privileged-subject check, normalization, realm, tenant and groups are the pipeline's; Degraded or unauthenticated is a rejection.
            var processed = await _pipeline.ProcessAsync(rawPrincipal, scheme, null, cancellationToken).ConfigureAwait(false);
            if (processed.Identity?.IsAuthenticated != true || GroupResolutionClaims.Read(processed) != GroupResolutionStatus.Complete)
            {
                _logger.LogWarning("WebSocket connection rejected: the identity pipeline did not resolve the caller.");
                return (false, null);
            }

            return (true, processed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error during WebSocket token validation.");
            return (false, null);
        }
    }
}
