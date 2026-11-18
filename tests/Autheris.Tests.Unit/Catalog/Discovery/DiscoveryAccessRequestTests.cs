namespace Autheris.Tests.Unit.Catalog.Discovery;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class DiscoveryAccessRequestTests
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");

    private readonly IDiscoveryService _discovery = Substitute.For<IDiscoveryService>();
    private readonly IConsentRequestService _consents = Substitute.For<IConsentRequestService>();
    private readonly IConsentApprovalRepository _approvals = Substitute.For<IConsentApprovalRepository>();
    private readonly IRateLimiterService _limiter = Substitute.For<IRateLimiterService>();
    private ConsentRequestSubmission? _submitted;

    public DiscoveryAccessRequestTests()
    {
        _consents.SubmitAsync(Arg.Any<ConsentRequestSubmission>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _submitted = ci.Arg<ConsentRequestSubmission>();
                return new SubmitConsentRequestResult { RequestId = Guid.NewGuid(), Status = "PENDING", Message = "ok" };
            });
        _limiter.CheckCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(new CostQuotaResult(true, 10, 0));
        _approvals.GetRequestsByRequesterSidAsync(Arg.Any<Sid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ConsentRequest>());
    }

    private static DiscoveryColumn Col(string name, DiscoveryColumnState state = DiscoveryColumnState.Masked, params string[] clearable) =>
        new(name, null, "varchar", null, clearable.Length > 0, clearable.Length > 0, state, null, ClearRequestable: clearable);

    private static DiscoveryObject Table(bool hidden = false, params DiscoveryColumn[] columns) =>
        new(DiscoveryObjectKind.Table, "sales", "dbo", "orders", null, null, "NORMAL", [], [], DiscoveryAccessState.None, null, columns, null, false, hidden);

    private DiscoveryAccessRequestService Service(CatalogDiscoverySettings? settings = null) =>
        new(_discovery, _consents, Options.Create(new GatewayOptions { Tenancy = TenancyShape.Multi.Options() }), _approvals,
            Options.Create(new CatalogDiscoveryOptions { Discovery = settings ?? new CatalogDiscoverySettings() }), _limiter);

    private static CatalogCaller Caller(string sid = "S-1") =>
        DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.User(sid, "tenant-a"));

    private static DiscoveryAccessRequest Request(params string[] clear) =>
        new("sales", "dbo", "orders", "Quarterly audit of orders", 7, null, clear.Length == 0 ? null : clear, null);

    private void Discovered(DiscoveryObject? obj) =>
        _discovery.GetAsync(Orders, Arg.Any<CatalogCaller>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<DiscoveryObject?>(obj));

    [Fact]
    [Trait("Category", "Security")]
    public async Task RequestAccess_ClearColumns_MapToRequestedClearByClassification()
    {
        Discovered(Table(false, Col("email", DiscoveryColumnState.Masked, "PII"), Col("iban", DiscoveryColumnState.Masked, "PII", "FINANCIAL"), Col("id", DiscoveryColumnState.Clear)));

        await Service().RequestAsync(Request("EMAIL", "iban"), Caller());

        var clear = _submitted!.RequestedClear!.ToDictionary(i => i.Classification, i => i.Columns.ToArray());
        clear.Keys.Order().ShouldBe(["FINANCIAL", "PII"]);
        clear["PII"].ShouldBe(["email", "iban"]);
        clear["FINANCIAL"].ShouldBe(["iban"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RequestAccess_HiddenOrUnknownColumn_SameInvalidColumnsError()
    {
        Discovered(Table(false, Col("email", DiscoveryColumnState.Masked, "PII"), Col("denied", DiscoveryColumnState.NoAccess, "PII"),
            Col("plain", DiscoveryColumnState.Masked), Col("open", DiscoveryColumnState.Clear, "PII")));

        var errors = new List<(string Code, string Message)>();
        foreach (var name in new[] { "missing", "hidden_in_catalog", "denied", "plain", "open", "  " })
        {
            var ex = await Should.ThrowAsync<ConsentRequestException>(() => Service().RequestAsync(Request("email", name), Caller()));
            errors.Add((ex.Code, ex.Message));
        }

        errors.Select(e => e.Code).Distinct().ShouldBe([DiscoveryAccessRequestService.InvalidColumnsCode]);
        errors.Select(e => e.Message).Distinct().Count().ShouldBe(1);
        await _consents.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RequestAccess_NotDiscoverableTable_SameNotFound()
    {
        var results = new List<(string Code, string Message)>();
        foreach (var obj in new DiscoveryObject?[] { null, Table(hidden: true) })
        {
            Discovered(obj);
            var ex = await Should.ThrowAsync<ConsentRequestException>(() => Service().RequestAsync(Request(), Caller()));
            results.Add((ex.Code, ex.Message));
        }

        results.Distinct().Count().ShouldBe(1);
        results[0].Code.ShouldBe(DiscoveryAccessRequestService.NotFoundCode);
        await _consents.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RequestAccess_RateLimited_RetryAfter()
    {
        Discovered(Table());
        _limiter.CheckCostQuotaAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<ClientQuotaPolicy>(), Arg.Any<CancellationToken>())
            .Returns(new CostQuotaResult(false, 0, 120));

        var ex = await Should.ThrowAsync<DiscoveryStatusException>(() => Service().RequestAsync(Request(), Caller()));

        ex.Kind.ShouldBe(DiscoveryFailureKind.RateLimited);
        ex.Code.ShouldBe("RATE_LIMIT_EXCEEDED");
        ex.RetryAfterSeconds.ShouldBe(120);
        await _limiter.Received(1).CheckCostQuotaAsync(
            "discovery-access-request:tenant-a:S-1", 1, Arg.Is<ClientQuotaPolicy>(p => p.MaxTokensCapacity == 30), Arg.Any<CancellationToken>());
        await _consents.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RequestAccess_Anonymous_IsUnauthorized_AndNothingIsSubmitted()
    {
        Discovered(Table());

        var ex = await Should.ThrowAsync<DiscoveryStatusException>(() =>
            Service().RequestAsync(Request(), DiscoveryTestBuilder.Caller(DiscoveryTestBuilder.Anonymous())));

        ex.Kind.ShouldBe(DiscoveryFailureKind.Unauthenticated);
        await _consents.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [Fact]
    public async Task RequestAccess_DuplicatePending_ReturnsTheExistingId()
    {
        Discovered(Table(false, Col("email", DiscoveryColumnState.Masked, "PII")));
        var existing = new ConsentRequest
        {
            TableIdentifier = Orders,
            TenantId = new TenantId("tenant-a"),
            Status = "PENDING",
            ContextJson = RequestedClearCodec.Merge(null, [new RequestedClearItem("PII", ["email"])])
        };
        _approvals.GetRequestsByRequesterSidAsync(Arg.Any<Sid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { existing });

        var result = await Service().RequestAsync(Request("Email"), Caller());

        result.RequestId.ShouldBe(existing.Id);
        await _consents.DidNotReceiveWithAnyArgs().SubmitAsync(default!, default);
    }

    [Fact]
    public async Task RequestAccess_MissingDuration_UsesTheConfiguredDefault()
    {
        Discovered(Table());

        await Service().RequestAsync(Request() with { DurationDays = null }, Caller());

        _submitted!.DurationDays.ShouldBeGreaterThanOrEqualTo(1);
        _submitted.RequestedClear.ShouldBeNull();
    }
}
