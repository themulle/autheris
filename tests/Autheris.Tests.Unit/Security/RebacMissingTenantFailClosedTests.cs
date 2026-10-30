namespace Autheris.Tests.Unit.Security;

using Autheris.Application.Security.Rebac.Services;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>DT-A-9: a ReBAC operation without a tenant fails closed; there is no implicit "default" tenant.</summary>
[Trait("Category", "Security")]
public sealed class RebacMissingTenantFailClosedTests
{
    private readonly InMemoryRebacStore _store = new(NullLogger<InMemoryRebacStore>.Instance);
    private readonly ZanzibarRebacEvaluator _evaluator;

    public RebacMissingTenantFailClosedTests()
    {
        var options = new GatewayOptions { Rebac = new RebacOptions { Enabled = true, MaxTraversalDepth = 5, CacheTtlSeconds = 10 } };
        _evaluator = new ZanzibarRebacEvaluator(_store, Options.Create(options), NullLogger<ZanzibarRebacEvaluator>.Instance);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public async Task DT_A_9_Check_Without_Tenant_Is_Denied_Even_When_A_Tuple_Exists_Elsewhere(string? tenant)
    {
        await _store.AddTupleAsync(new RebacTuple("default", "alice", "viewer", "doc:1"));

        var result = await _evaluator.CheckAsync(new RebacCheckRequest(tenant!, "alice", "viewer", "doc:1"));

        result.Allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task DT_A_9_Batch_Without_Tenant_Is_Denied_As_A_Whole()
    {
        await _store.AddTupleAsync(new RebacTuple("default", "alice", "viewer", "doc:1"));
        var check = new RebacCheckRequest("", "alice", "viewer", "doc:1");

        var result = await _evaluator.BatchCheckAsync(new RebacBatchCheckRequest("", [check]));

        result.Decisions[check].ShouldBeFalse();
    }

    [Fact]
    public async Task DT_A_9_Store_Operations_Without_Tenant_Throw_Tenant_Unresolved()
    {
        await Should.ThrowAsync<ArgumentException>(async () => await _store.AddTupleAsync(new RebacTuple("", "alice", "viewer", "doc:1")));
        await Should.ThrowAsync<TenantUnresolvedException>(async () => await _store.ClearTenantTuplesAsync(" "));
        await Should.ThrowAsync<TenantUnresolvedException>(() => _evaluator.InvalidateTenantCacheAsync(""));
    }
}
