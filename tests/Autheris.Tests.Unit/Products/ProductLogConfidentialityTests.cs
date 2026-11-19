using Autheris.Application.Products;
using Autheris.Application.State;
using Autheris.Application.State.Caching.TableCache;
using Autheris.Domain.Model;
using Autheris.Domain.Options.TableCache;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Products;

[Trait("Category", "Security")]
public sealed class ProductLogConfidentialityTests
{
    private sealed class CapturingLogger : ILogger<ProductLoadService>
    {
        public List<string> Lines { get; } = [];

        public List<Exception> Exceptions { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Lines.Add(formatter(state, exception));
            if (exception is not null)
            {
                Exceptions.Add(exception);
            }
        }
    }

    [Fact]
    public async Task Load_failure_log_has_no_exception_message()
    {
        var h = new ProductHarness();
        var runner = new ScriptedRunner { Failure = _ => new InvalidOperationException("row value jane.doe@example.com") };
        var cluster = new InMemoryClusterStateProvider();
        var logger = new CapturingLogger();
        var loader = new ProductLoadService(h.Store, runner, new RecordingCacheStore(), new TableCacheCatalog(), cluster,
            h.Audit, new FakeTimeProvider(), logger);
        var admin = new ProductCaller(ProductHarness.Admin, ProductHarness.Tenant, true, false, true);
        var product = await h.Service.PutAsync(new ProductPutRequest(admin, "sales", "orders_view", DataProductKind.View, "SELECT id FROM crm.public.orders", ProductHarness.Bob,
            new LoadSettings(LoadMode.Live)));

        var thrown = await Should.ThrowAsync<ProductLoadException>(() => loader.LoadAsync(product));

        logger.Lines.ShouldNotBeEmpty();
        logger.Lines.ShouldAllBe(l => !l.Contains("jane.doe", StringComparison.Ordinal));
        logger.Exceptions.ShouldBeEmpty("the exception object is never handed to the logger");
        thrown.InnerException.ShouldBeNull();
    }
}
