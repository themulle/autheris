namespace Autheris.Tests.Unit.Consent;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Consent;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class ConsentRequestServiceTests
{
    private static readonly Sid User = new("S-1-5-21-SVC");

    private sealed class MemoryIdempotencyStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, object> _items = new();
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) where T : class => Task.FromResult(_items.TryGetValue(key, out var v) ? v as T : null);
        public Task<bool> SetIfNotExistsAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct = default) where T : class => Task.FromResult(_items.TryAdd(key, value));
    }

    private static ClaimsPrincipal Principal(string tokenTenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, User.Value), new("tenant_id", tokenTenant) };
        foreach (var role in roles) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static SubmitConsentRequestCommand Command(
        ClaimsPrincipal principal, string contextTenant, string table = "salaries", string justification = "Quarterly audit", int days = 7, string? key = null)
        => new("hr", "dbo", table, justification, days, key, User, principal, new TenantId(contextTenant));

    private static (ITableMetadataRepository Metadata, IConsentApprovalRepository Approvals, List<ConsentRequest> Created) Repositories(bool tableExists = true)
    {
        var metadata = Substitute.For<ITableMetadataRepository>();
        metadata.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(tableExists
                ? new TableMetadata { Identifier = ci.Arg<TableIdentifier>(), Table = new Table { Id = Guid.NewGuid() } }
                : null));
        var created = new List<ConsentRequest>();
        var approvals = Substitute.For<IConsentApprovalRepository>();
        approvals.CreateConsentRequestAsync(Arg.Any<ConsentRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var r = ci.Arg<ConsentRequest>(); created.Add(r); return Task.FromResult(r); });
        return (metadata, approvals, created);
    }

    [Fact]
    public async Task Submit_CrossTenantWithoutAdmin_Throws()
    {
        var (metadata, approvals, created) = Repositories();
        var service = new ConsentRequestService(metadata, approvals);

        var ex = await Should.ThrowAsync<ConsentRequestException>(() =>
            service.SubmitAsync(Command(Principal("tenant-a"), "tenant-b")));

        ex.Code.ShouldBe("FORBIDDEN");
        created.ShouldBeEmpty();
    }

    [Fact]
    public async Task Submit_CrossTenantAsClusterAdmin_UsesContextTenant()
    {
        var (metadata, approvals, created) = Repositories();
        var service = new ConsentRequestService(metadata, approvals);

        var result = await service.SubmitAsync(Command(Principal("tenant-a", "ClusterAdmin"), "tenant-b"));

        created.ShouldHaveSingleItem().TenantId.ShouldBe(new TenantId("tenant-b"));
        result.Message.ShouldBe("Consent request submitted successfully.");
    }

    [Fact]
    public async Task Submit_SameIdempotencyKey_ReturnsStoredResult()
    {
        var (metadata, approvals, created) = Repositories();
        var service = new ConsentRequestService(metadata, approvals, new MemoryIdempotencyStore());
        var principal = Principal("tenant-a");

        var first = await service.SubmitAsync(Command(principal, "tenant-a", key: "key-1"));
        var replay = await service.SubmitAsync(Command(principal, "tenant-a", key: "key-1"));
        var other = await service.SubmitAsync(Command(principal, "tenant-a", table: "bonuses", key: "key-1"));

        created.Count.ShouldBe(2);
        replay.ShouldBeSameAs(first);
        other.RequestId.ShouldNotBe(first.RequestId);
    }

    [Theory]
    [InlineData("", "dbo", "t", "Valid justification", 7)]
    [InlineData("hr", "dbo", "t", "abc", 7)]
    [InlineData("hr", "dbo", "t", "Valid justification", 0)]
    [InlineData("hr", "dbo", "t", "Valid justification", 366)]
    public async Task Submit_InvalidArguments_ThrowsInvalidArgument(string domain, string schema, string table, string justification, int days)
    {
        var (metadata, approvals, _) = Repositories();
        var service = new ConsentRequestService(metadata, approvals);

        var ex = await Should.ThrowAsync<ConsentRequestException>(() =>
            service.SubmitAsync(new SubmitConsentRequestCommand(domain, schema, table, justification, days, null, User, Principal("t"), TenantId.LegacySingleTenant)));

        ex.Code.ShouldBe("INVALID_ARGUMENT");
    }

    [Fact]
    public async Task Submit_UnknownTable_ReturnsNeutralForbidden()
    {
        var (metadata, approvals, _) = Repositories(tableExists: false);
        var service = new ConsentRequestService(metadata, approvals);

        var ex = await Should.ThrowAsync<ConsentRequestException>(() =>
            service.SubmitAsync(Command(Principal("tenant-a"), "tenant-a")));

        ex.Code.ShouldBe("FORBIDDEN");
    }

    [Fact]
    public async Task Submit_ItsmDispatchFails_DeletesRequestAndThrows()
    {
        var (metadata, approvals, created) = Repositories();
        var dispatcher = Substitute.For<IConsentRequestDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<ItsmTicketRequest>(), Arg.Any<ItsmSystemType>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ItsmTicketResult(false, null, "ITSM_DOWN", "down")));
        var options = Options.Create(new GatewayOptions { Itsm = new ItsmOptions { Enabled = true } });
        var service = new ConsentRequestService(metadata, approvals, null, dispatcher, options);

        var ex = await Should.ThrowAsync<ConsentRequestException>(() =>
            service.SubmitAsync(Command(Principal("tenant-a"), "tenant-a")));

        ex.Code.ShouldBe("ITSM_DOWN");
        await approvals.Received(1).DeleteConsentRequestAsync(created[0].Id, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submit_ItsmDispatchSucceeds_StoresTicketAndReturnsPendingExternal()
    {
        var (metadata, approvals, created) = Repositories();
        var reference = new ItsmTicketReference(ItsmSystemType.ServiceNow, "INC1", "https://itsm/INC1");
        var dispatcher = Substitute.For<IConsentRequestDispatcher>();
        dispatcher.DispatchAsync(Arg.Any<ItsmTicketRequest>(), Arg.Any<ItsmSystemType>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ItsmTicketResult(true, reference, null, null)));
        var options = Options.Create(new GatewayOptions { Itsm = new ItsmOptions { Enabled = true } });
        var service = new ConsentRequestService(metadata, approvals, null, dispatcher, options);

        var result = await service.SubmitAsync(Command(Principal("tenant-a"), "tenant-a"));

        result.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
        result.ItsmTicketReference.ShouldBe(reference);
        await approvals.Received(1).UpdateConsentRequestTicketIdAsync(created[0].Id, "INC1", Arg.Any<CancellationToken>());
    }
}
