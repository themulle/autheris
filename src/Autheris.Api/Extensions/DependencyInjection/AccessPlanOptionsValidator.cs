namespace Autheris.Api.Extensions.DependencyInjection;

using Autheris.Application.State;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

/// <summary>
/// Validates <see cref="AccessPlanOptions"/>: when the cluster state is shared across replicas, all nodes must derive the
/// confirmation-token HMAC key from the same configured secret (a per-process random key would reject tokens issued by
/// another node).
/// </summary>
public sealed class AccessPlanOptionsValidator : IValidateOptions<AccessPlanOptions>
{
    private readonly IClusterStateTopology? _topology;

    public AccessPlanOptionsValidator(IClusterStateTopology? topology = null)
    {
        _topology = topology;
    }

    public ValidateOptionsResult Validate(string? name, AccessPlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (_topology?.IsClusterWide == true && string.IsNullOrWhiteSpace(options.SigningKeyRef))
        {
            return ValidateOptionsResult.Fail(
                $"{AccessPlanOptions.SectionName}:SigningKeyRef is required when the cluster state is shared across replicas.");
        }

        return ValidateOptionsResult.Success;
    }
}
