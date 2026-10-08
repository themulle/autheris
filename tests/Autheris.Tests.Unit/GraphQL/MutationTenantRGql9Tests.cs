namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.GraphQL.Types;
using HotChocolate;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// R-GQL-9: mutations use the tenant resolved for the request (ClusterAdmin's X-Tenant-ID), the claim only as a
/// cross-check, with the same admin exception everywhere. GQL-11: an idempotency key only replays the same request.
/// </summary>
public sealed class MutationTenantRGql9Tests
{
    private sealed class Accessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; } = context;
    }

    private sealed class MemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, object> _items = new();
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class => Task.FromResult(_items.TryGetValue(key, out var v) ? v as T : null);
        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class => Task.FromResult(_items.TryAdd(key, value));
    }

    private static Accessor Context(string tokenTenant, string requestTenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-MUT"), new("tenant_id", tokenTenant) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        http.Items["TenantId"] = new TenantId(requestTenant);
        return new Accessor(http);
    }

    private static (ITableMetadataRepository Metadata, IConsentApprovalRepository Approvals, List<ConsentRequest> Created) Repositories()
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(new TableMetadata { Identifier = ci.Arg<TableIdentifier>(), Table = new Table { Id = Guid.NewGuid() } }));
        var created = new List<ConsentRequest>();
        var approvals = Substitute.For<IConsentApprovalRepository>();
        approvals.CreateConsentRequestAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var r = ci.Arg<ConsentRequest>(); created.Add(r); return Task.FromResult(r); });
        return (metadata, approvals, created);
    }

    [Fact]
    public async Task ClusterAdmin_RequestsAccess_InTheSelectedTenant()
    {
        var (metadata, approvals, created) = Repositories();

        await new Mutation().RequestTableAccessAsync("hr", "dbo", "salaries", "Quarterly audit", 7, null, metadata, approvals, Context("tenant-a", "tenant-b", "ClusterAdmin"));

        created.ShouldHaveSingleItem().TenantId.ShouldBe(new TenantId("tenant-b"));
    }

    [Fact]
    public async Task NonAdmin_WithDifferentClaimAndRequestTenant_IsForbidden()
    {
        var (metadata, approvals, _) = Repositories();

        var ex = await Should.ThrowAsync<GraphQLException>(() =>
            new Mutation().RequestTableAccessAsync("hr", "dbo", "salaries", "Quarterly audit", 7, null, metadata, approvals, Context("tenant-a", "tenant-b")));

        ex.Errors[0].Code.ShouldBe("FORBIDDEN");
    }

    [Fact]
    public async Task IdempotencyKey_ReusedForOtherArguments_DoesNotReplayTheFirstResult()
    {
        var (metadata, approvals, created) = Repositories();
        var store = new MemoryIdempotencyStore();
        var context = Context("tenant-a", "tenant-a");

        var first = await new Mutation().RequestTableAccessAsync("hr", "dbo", "salaries", "Quarterly audit", 7, "key-1", metadata, approvals, context, store);
        var replay = await new Mutation().RequestTableAccessAsync("hr", "dbo", "salaries", "Quarterly audit", 7, "key-1", metadata, approvals, context, store);
        var other = await new Mutation().RequestTableAccessAsync("hr", "dbo", "bonuses", "Quarterly audit", 7, "key-1", metadata, approvals, context, store);

        created.Count.ShouldBe(2);
        replay.ShouldBe(first);
        other.ShouldNotBe(first);
    }
}
