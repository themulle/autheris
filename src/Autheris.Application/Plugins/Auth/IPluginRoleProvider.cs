namespace Autheris.Application.Plugins.Auth;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Domain.Plugins.Auth;

public interface IPluginRoleProvider
{
    string Name { get; }
    ValueTask<PluginRoleEnrichmentResult> EnrichRolesAsync(PluginSecurityContext context, CancellationToken ct);
}
