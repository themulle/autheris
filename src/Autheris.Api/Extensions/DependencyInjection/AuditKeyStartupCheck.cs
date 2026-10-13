namespace Autheris.Api.Extensions.DependencyInjection;

using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Sql.Services;
using Microsoft.Extensions.Hosting;

/// <summary>
/// L-2: resolves the key of the SQL audit digest at startup. Outside Development and Test a missing, unreadable or too short key stops the
/// host (fail closed) instead of failing the first governed statement. The digester is a singleton, so the key read here is the one that
/// the request path uses.
/// </summary>
public sealed class AuditKeyStartupCheck(CompiledSqlAuditDigester digester) : IHostedService
{
    /// <exception cref="System.InvalidOperationException">No usable audit key outside Development and Test.</exception>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        digester.EnsureKey();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
