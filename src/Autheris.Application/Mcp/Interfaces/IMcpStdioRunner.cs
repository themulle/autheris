namespace Autheris.Application.Mcp.Interfaces;

using System.IO;
using System.Threading;
using System.Threading.Tasks;

public interface IMcpStdioRunner
{
    Task RunAsync(
        TextReader input,
        TextWriter output,
        string servicePrincipalId = "cli-developer",
        string tenantId = "legacy-single-tenant",
        CancellationToken cancellationToken = default);
}
