using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Hosting;

/// <summary>
/// .NET 11 <c>IHost.RunAsync</c>/<c>StopAsync</c> surface the failure of a <see cref="BackgroundService"/> (default behaviour
/// <see cref="BackgroundServiceExceptionBehavior.StopHost"/>). A failing security-relevant worker (revocation, grant expiry sweep,
/// SCIM sync) therefore ends the host with an error instead of leaving a running host without that worker.
/// </summary>
[Trait("Category", "Security")]
public sealed class BackgroundServiceFailureStopsHostTests
{
    private sealed class FailingWorker : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
            throw new InvalidOperationException("worker failed");
    }

    private sealed class FailingAfterYieldWorker : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Yield();
            throw new InvalidOperationException("worker failed");
        }
    }

    private static IHost BuildHost<TWorker>(Action<HostOptions>? configure = null)
        where TWorker : BackgroundService
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddHostedService<TWorker>();
        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        return builder.Build();
    }

    [Fact]
    public void DefaultHostOptions_StopTheHostWhenABackgroundServiceFails()
    {
        using var host = BuildHost<FailingWorker>();

        var options = host.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<HostOptions>>().Value;

        options.BackgroundServiceExceptionBehavior.ShouldBe(BackgroundServiceExceptionBehavior.StopHost);
    }

    [Fact]
    public async Task FailingSynchronousWorker_EndsRunAsyncWithAnError()
    {
        using var host = BuildHost<FailingWorker>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var ex = await Should.ThrowAsync<Exception>(() => host.RunAsync(cts.Token));

        cts.IsCancellationRequested.ShouldBeFalse("the host must stop because of the failure, not because of the guard timeout");
        Flatten(ex).Any(static e => e is InvalidOperationException { Message: "worker failed" }).ShouldBeTrue();
    }

    [Fact]
    public async Task FailingAsynchronousWorker_RequestsApplicationStoppingAndEndsRunAsyncWithAnError()
    {
        using var host = BuildHost<FailingAfterYieldWorker>();
        var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var ex = await Should.ThrowAsync<Exception>(() => host.RunAsync(cts.Token));

        stopping.IsCancellationRequested.ShouldBeTrue("a failed worker must stop the host, so no request is served afterwards");
        cts.IsCancellationRequested.ShouldBeFalse();
        Flatten(ex).Any(static e => e is InvalidOperationException { Message: "worker failed" }).ShouldBeTrue();
    }

    private static IEnumerable<Exception> Flatten(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            yield return current;
            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                {
                    foreach (var nested in Flatten(inner))
                    {
                        yield return nested;
                    }
                }
            }
        }
    }
}
