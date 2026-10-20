namespace Autheris.Application.Plugins.Auth;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Plugins.Auth;

public interface IPluginAuthorizationHook
{
    string HookName { get; }
    int Order { get; }
    ValueTask<PluginAuthorizationResult> EvaluateAccessAsync(PluginAuthorizationContext context, CancellationToken ct);
}
