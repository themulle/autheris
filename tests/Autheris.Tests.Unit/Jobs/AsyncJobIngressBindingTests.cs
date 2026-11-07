using Autheris.Tests.Common;
namespace Autheris.Tests.Unit.Jobs;

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Runtime.Jobs.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #447 WP-3, SEC-ATR-05: the queued job carries the ingress of the submitting request; the worker scope is stamped from it and
/// an item without one stays unset, which the transport gate denies (the worker never assumes a transport).
/// </summary>
[Trait("Category", "Security")]
public sealed class AsyncJobIngressBindingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "autheris-atr-job-" + Guid.NewGuid().ToString("N"));
    private readonly IHostEnvironment _env = Substitute.For<IHostEnvironment>();
    private readonly IAuditLogRepository _audit = Substitute.For<IAuditLogRepository>();

    public AsyncJobIngressBindingTests()
    {
        Directory.CreateDirectory(_dir);
        _env.ContentRootPath.Returns(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static ClaimsPrincipal User() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "alice"), new Claim("sub", "alice"), new Claim("tenant_id", "t")], "Test"));

    private async Task<DataSourceTransportFlags> IngressSeenByWorkerAsync(DataSourceTransportFlags? submittedWith)
    {
        var options = Options.Create(new GatewayOptions());
        var manager = new AsyncQueryJobManager(_audit, environment: _env, options: options);
        DataSourceTransportFlags? seen = null;
        var services = new ServiceCollection();
        services.AddScoped<IIngressTransportContext, IngressTransportContext>();
        services.AddScoped(sp =>
        {
            var context = sp.GetRequiredService<IIngressTransportContext>();
            var sql = Substitute.For<IGovernedSqlExecutionService>();
            sql.ExecuteQueryBufferedAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
            {
                seen = context.Current;
                return Task.FromResult(new GovernedSqlResult("s", ["id"], new List<IReadOnlyDictionary<string, object?>>(), 0, 1));
            });
            return sql;
        });
        var worker = new AsyncQueryJobBackgroundWorker(
            manager, manager, services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), _audit,
            NullLogger<AsyncQueryJobBackgroundWorker>.Instance, options);

        var job = submittedWith is { } ingress
            ? await manager.SubmitJobAsync(new Autheris.Domain.Model.AsyncQueryJobRequest("SELECT 1"), User(), new TenantId("t"), ingress)
            : await manager.SubmitJobAsync(new Autheris.Domain.Model.AsyncQueryJobRequest("SELECT 1"), User(), new TenantId("t"));
        await worker.StartAsync(CancellationToken.None);
        await WaitHelper.WaitUntil(() => seen is not null, TimeSpan.FromSeconds(10));
        await worker.StopAsync(CancellationToken.None);
        job.ShouldNotBeNull();
        return seen!.Value;
    }

    [Fact]
    public async Task AsyncJobWorker_StampsThePersistedIngress_NotAConstant()
    {
        (await IngressSeenByWorkerAsync(DataSourceTransportFlags.WebSql)).ShouldBe(DataSourceTransportFlags.WebSql);
        (await IngressSeenByWorkerAsync(DataSourceTransportFlags.Mcp)).ShouldBe(DataSourceTransportFlags.Mcp);
    }

    [Fact]
    public async Task AsyncJob_RowWithoutIngress_Denied()
    {
        // an item queued without an ingress leaves the worker scope unset: IngressTransportContext.Current stays None, which the gate denies
        (await IngressSeenByWorkerAsync(null)).ShouldBe(DataSourceTransportFlags.None);
    }
}
