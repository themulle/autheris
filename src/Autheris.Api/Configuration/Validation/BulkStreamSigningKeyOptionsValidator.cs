using System;
using Autheris.Application.Security;
using Autheris.Domain.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Autheris.Api.Configuration.Validation;

/// <summary>
/// Validates <c>Gateway:RestApi:BulkStream:SigningKey</c>, the HMAC key of the keyset cursors across both the REST bulk
/// stream endpoints and GraphQL keyset connections. Without a configured key every node signs with its own ephemeral key,
/// so continuations fail after restart or across nodes. Outside Development the start fails whenever the key is missing,
/// a placeholder value, or shorter than 32 UTF-8 bytes after trimming, regardless of transport switches. In Development
/// an unconfigured key logs a warning and uses a per-instance ephemeral key. The key value is never echoed.
/// </summary>
public sealed class BulkStreamSigningKeyOptionsValidator : IValidateOptions<GatewayOptions>
{
    private readonly IHostEnvironment _environment;
    private readonly ILogger<BulkStreamSigningKeyOptionsValidator>? _logger;

    public BulkStreamSigningKeyOptionsValidator(IHostEnvironment environment, ILogger<BulkStreamSigningKeyOptionsValidator>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(environment);
        _environment = environment;
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, GatewayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var key = options.RestApi?.BulkStream?.SigningKey;
        var configured = !string.IsNullOrWhiteSpace(key);

        if (_environment.IsDevelopment())
        {
            if (!configured)
            {
                _logger?.LogWarning(
                    "Gateway:RestApi:BulkStream:SigningKey is not configured; using a random per-instance key in Development. " +
                    "Keyset cursors are only valid until instance restart. A non-placeholder key of at least {MinimumBytes} bytes is required outside Development.",
                    SecretKeyRequirements.MinimumKeyBytes);
            }

            return ValidateOptionsResult.Success;
        }

        if (!SecretKeyRequirements.TryValidateCursorSigningKey(key, out _, out var errorMessage))
        {
            return ValidateOptionsResult.Fail(errorMessage!);
        }

        return ValidateOptionsResult.Success;
    }
}
