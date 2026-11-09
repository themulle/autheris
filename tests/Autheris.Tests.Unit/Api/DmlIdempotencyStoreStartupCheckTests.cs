namespace Autheris.Tests.Unit.Api;

using System;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Idempotency;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DmlIdempotencyStoreStartupCheckTests
{
    private sealed class FakeClusterWideStore : IClusterWideIdempotencyStore
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class => Task.FromResult<T?>(null);
        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class => Task.FromResult(true);
    }

    private static DmlIdempotencyStoreStartupCheck Check(bool allowDml, IIdempotencyStore store) =>
        new(Options.Create(new GatewayOptions { WebSql = new WebSqlOptions { AllowDml = allowDml } }), store);

    [Fact]
    public async Task Per_process_store_with_dml_enabled_fails_startup() =>
        await Should.ThrowAsync<ValidationException>(() => Check(true, new InMemoryIdempotencyStore()).StartAsync(default));

    [Fact]
    public async Task Cluster_wide_store_with_dml_enabled_starts() =>
        await Check(true, new FakeClusterWideStore()).StartAsync(default);

    [Fact]
    public async Task Per_process_store_with_dml_disabled_starts() =>
        await Check(false, new InMemoryIdempotencyStore()).StartAsync(default);
}
