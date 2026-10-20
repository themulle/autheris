namespace Autheris.Application.Plugins.Auth;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Plugins.Auth;

public interface IAuthPluginHost
{
    bool HasRoleProviders { get; }
    bool HasCallerAttributeProviders { get; }
    bool HasAuthorizationHooks { get; }

    int LoadPluginsFromDirectory(string directoryPath);

    ValueTask<PluginRoleEnrichmentExecutionResult> EnrichRolesAsync(
        PluginSecurityContext context,
        CancellationToken ct);

    bool TryGetCallerAttributeProvider(string kind, out IPluginCallerAttributeProvider? provider);

    ValueTask<PluginAuthorizationResult> EvaluateAuthorizationHooksAsync(
        PluginAuthorizationContext context,
        CancellationToken ct);
}

public sealed record PluginRoleEnrichmentExecutionResult(
    bool IsSuccess,
    PluginRoleEnrichmentResult Result,
    string? ErrorCode = null,
    string? ErrorMessage = null)
{
    public static PluginRoleEnrichmentExecutionResult Success(PluginRoleEnrichmentResult result) =>
        new(true, result);

    public static PluginRoleEnrichmentExecutionResult Failed(string errorCode, string? errorMessage = null) =>
        new(false, PluginRoleEnrichmentResult.Empty, errorCode, errorMessage);
}
