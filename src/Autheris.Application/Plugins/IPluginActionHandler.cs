namespace Autheris.Application.Plugins;

using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Handler contract implemented by C# DLL plugins providing custom callable actions.
/// </summary>
public interface IPluginActionHandler
{
    /// <summary>
    /// The unique action name handled by this plugin (e.g. "sap.sync_user_roles").
    /// </summary>
    string ActionName { get; }

    /// <summary>
    /// Executes the action with the given context parameters.
    /// </summary>
    Task<PluginActionResult> ExecuteActionAsync(PluginActionContext context, CancellationToken ct = default);
}
