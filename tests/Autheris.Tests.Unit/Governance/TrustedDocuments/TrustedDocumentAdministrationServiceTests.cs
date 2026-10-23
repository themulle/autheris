namespace Autheris.Tests.Unit.Governance.TrustedDocuments;

using System.Text.Json;
using Autheris.Application.Audit;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Domain.Audit;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP2: admin rules (validation, conflicts, invalidate-before-audit, audit payload; SEC-281-8, SEC-281-9).</summary>
[Trait("Category", "Security")]
public sealed class TrustedDocumentAdministrationServiceTests
{
    private static readonly string H1 = new('a', 64);
    private static readonly string H2 = new('b', 64);
    private static readonly TrustedDocumentActor Actor = new("admin-sub", "trusted-documents:write");
    private static readonly string[] Origins = ["https://app.example.com"];

    private readonly ITrustedDocumentRepository _repo = Substitute.For<ITrustedDocumentRepository>();
    private readonly ITrustedDocumentAppRegistry _registry = Substitute.For<ITrustedDocumentAppRegistry>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly List<AuditLogEntry> _entries = [];

    public TrustedDocumentAdministrationServiceTests()
    {
        _repo.ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new TrustedDocumentSetDelta([H1], [H2], 3, true, true)));
        _repo.RetireTrustedDocumentVersionAsync(default!, default!, default, default).ReturnsForAnyArgs(TrustedDocumentRetireOutcome.Retired);
        _audit.RecordRequiredAsync(Arg.Do<AuditLogEntry>(_entries.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
    }

    private TrustedDocumentAdministrationService Create(AppScopedTrustedDocumentsOptions? options = null) => new(
        _repo, _registry, _audit,
        Options.Create(new GatewayOptions { GraphQL = new GraphQLOptions { AppScopedTrustedDocuments = options ?? new AppScopedTrustedDocumentsOptions { Enabled = true } } }));

    [Fact]
    public async Task Put_NormalizesInput_CallsRepository_InvalidatesAndAudits()
    {
        var result = await Create().PutAsync("shop", "1.0", [new string('A', 64), H2, H2], ["HTTPS://App.Example.com:443", "https://app.example.com"], Actor);

        result.ShouldBe(new TrustedDocumentPutResult(1, 1, 3, true));
        await _repo.Received(1).ReplaceTrustedDocumentVersionAsync(
            "shop", "1.0", Arg.Is<IReadOnlyList<string>>(o => o.SequenceEqual(Origins)), Arg.Is<IReadOnlyList<string>>(h => h.SequenceEqual(new[] { H1, H2 })),
            20, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        _registry.Received(1).Invalidate();
        _entries.ShouldHaveSingleItem().EventType.ShouldBe(AuditEventTypes.TrustedDocumentsReplaced);
    }

    [Theory]
    [InlineData("Shop", "1", TrustedDocumentAdminException.InvalidAppId)]
    [InlineData("", "1", TrustedDocumentAdminException.InvalidAppId)]
    [InlineData("shop", "-1", TrustedDocumentAdminException.InvalidVersion)]
    [InlineData("shop", "a/b", TrustedDocumentAdminException.InvalidVersion)]
    public async Task Put_InvalidIdentity_400_NothingStored(string appId, string version, string code)
    {
        var ex = await Should.ThrowAsync<TrustedDocumentAdminException>(() => Create().PutAsync(appId, version, [H1], Origins, Actor));
        ex.Code.ShouldBe(code);
        ex.IsConflict.ShouldBeFalse();
        await AssertNothingHappened();
    }

    [Fact]
    public async Task Admin_Put_OverBounds_400()
    {
        var sut = Create(new AppScopedTrustedDocumentsOptions { Enabled = true, MaxHashesPerVersion = 2, MaxAllowedOriginsPerApp = 1 });
        string[] three = [H1, H2, new string('c', 64)];

        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => sut.PutAsync("shop", "1", three, Origins, Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidHashCount);
        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => sut.PutAsync("shop", "1", [], Origins, Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidHashCount);
        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => sut.PutAsync("shop", "1", null, Origins, Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidHashCount);
        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => sut.PutAsync("shop", "1", [H1], [], Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidOriginCount);
        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => sut.PutAsync("shop", "1", [H1], ["https://a.example.com", "https://b.example.com"], Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidOriginCount);
        await AssertNothingHappened();
    }

    [Theory]
    [InlineData("not-a-hash")]
    [InlineData("zz")]
    public async Task Put_InvalidHash_400(string hash)
    {
        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => Create().PutAsync("shop", "1", [hash], Origins, Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidHash);
        await AssertNothingHappened();
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://app.example.com/path")]
    [InlineData("null")]
    public async Task Put_InvalidOrigin_400(string origin)
    {
        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => Create().PutAsync("shop", "1", [H1], [origin], Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidOrigin);
        await AssertNothingHappened();
    }

    [Fact]
    public async Task Admin_Put_RetiredVersion_409()
    {
        _repo.ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<TrustedDocumentSetDelta>(new TrustedDocumentVersionRetiredException()));

        var ex = await Should.ThrowAsync<TrustedDocumentAdminException>(() => Create().PutAsync("shop", "1", [H1], Origins, Actor));
        ex.Code.ShouldBe(TrustedDocumentAdminException.VersionRetired);
        ex.IsConflict.ShouldBeTrue();
        _entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Put_ActiveVersionCap_409_AndCapIsPassedToTheRepository()
    {
        _repo.ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromException<TrustedDocumentSetDelta>(new TrustedDocumentVersionLimitException()));

        var ex = await Should.ThrowAsync<TrustedDocumentAdminException>(() =>
            Create(new AppScopedTrustedDocumentsOptions { Enabled = true, MaxActiveVersionsPerApp = 7 }).PutAsync("shop", "1", [H1], Origins, Actor));
        ex.Code.ShouldBe(TrustedDocumentAdminException.VersionLimit);
        ex.IsConflict.ShouldBeTrue();
        await _repo.Received(1).ReplaceTrustedDocumentVersionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<string>>(), 7, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Audit_Payload_NoDocumentText_ListsCapped()
    {
        await Create().PutAsync("shop", "1.0", [H1], Origins, Actor);

        var entry = _entries.ShouldHaveSingleItem();
        entry.TargetTable.ShouldBe("trusted-documents:shop/1.0");
        entry.ActorSid.Value.ShouldBe("admin-sub");
        entry.Decision.ShouldBe("ALLOW");
        using var doc = JsonDocument.Parse(entry.DetailsJson!);
        var root = doc.RootElement;
        root.GetProperty("appId").GetString().ShouldBe("shop");
        root.GetProperty("version").GetString().ShouldBe("1.0");
        root.GetProperty("addedCount").GetInt32().ShouldBe(1);
        root.GetProperty("removedCount").GetInt32().ShouldBe(1);
        root.GetProperty("originsChanged").GetBoolean().ShouldBeTrue();
        root.GetProperty("added")[0].GetString().ShouldBe(H1);
        root.GetProperty("removed")[0].GetString().ShouldBe(H2);
        root.GetProperty("scope").GetString().ShouldBe("trusted-documents:write");
        entry.DetailsJson!.ShouldNotContain("query");
    }

    [Fact]
    public async Task Audit_Payload_ListsOverCap_CountsOnly()
    {
        var many = Enumerable.Range(0, TrustedDocumentAdministrationService.MaxAuditedListLength + 1).Select(i => i.ToString("x64")).ToList();
        _repo.ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new TrustedDocumentSetDelta(many, [], 0, false, false)));

        await Create().PutAsync("shop", "1", [H1], Origins, Actor);

        using var doc = JsonDocument.Parse(_entries.Single().DetailsJson!);
        doc.RootElement.GetProperty("addedCount").GetInt32().ShouldBe(many.Count);
        doc.RootElement.GetProperty("added").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task NoOpPut_IsAuditedToo()
    {
        _repo.ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult(new TrustedDocumentSetDelta([], [], 1, false, false)));

        await Create().PutAsync("shop", "1", [H1], Origins, Actor);

        _entries.ShouldHaveSingleItem().EventType.ShouldBe(AuditEventTypes.TrustedDocumentsReplaced);
    }

    [Fact]
    public async Task Admin_Audit_Failure_Surfaces_And_Cache_Invalidated()
    {
        _audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("audit down")));

        await Should.ThrowAsync<InvalidOperationException>(() => Create().PutAsync("shop", "1", [H1], Origins, Actor));
        _registry.Received(1).Invalidate();

        await Should.ThrowAsync<InvalidOperationException>(() => Create().RetireAsync("shop", "1", Actor));
        _registry.Received(2).Invalidate();
    }

    [Theory]
    [InlineData(TrustedDocumentRetireOutcome.Retired)]
    [InlineData(TrustedDocumentRetireOutcome.AlreadyRetired)]
    [InlineData(TrustedDocumentRetireOutcome.NotFound)]
    public async Task Retire_ReturnsOutcome_Invalidates_AndAudits(TrustedDocumentRetireOutcome outcome)
    {
        _repo.RetireTrustedDocumentVersionAsync(default!, default!, default, default).ReturnsForAnyArgs(outcome);

        (await Create().RetireAsync("shop", "1", Actor)).ShouldBe(outcome);

        _registry.Received(1).Invalidate();
        var entry = _entries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.TrustedDocumentsRetired);
        entry.DetailsJson!.ShouldContain(outcome.ToString());
    }

    [Fact]
    public async Task Retire_InvalidIdentity_400()
    {
        (await Should.ThrowAsync<TrustedDocumentAdminException>(() => Create().RetireAsync("Shop", "1", Actor))).Code.ShouldBe(TrustedDocumentAdminException.InvalidAppId);
        await AssertNothingHappened();
    }

    [Fact]
    public async Task GetApp_ValidatesTheId_AndReadsFromTheRepository()
    {
        await Should.ThrowAsync<TrustedDocumentAdminException>(() => Create().GetAppAsync("Bad Id"));
        _repo.GetTrustedDocumentAppAsync("shop", Arg.Any<CancellationToken>()).Returns(new TrustedDocumentAppView("shop", Origins, []));
        (await Create().GetAppAsync("shop"))!.AppId.ShouldBe("shop");
    }

    private async Task AssertNothingHappened()
    {
        await _repo.DidNotReceiveWithAnyArgs().ReplaceTrustedDocumentVersionAsync(default!, default!, default!, default!, default, default, default);
        await _repo.DidNotReceiveWithAnyArgs().RetireTrustedDocumentVersionAsync(default!, default!, default, default);
        _registry.DidNotReceive().Invalidate();
        _entries.ShouldBeEmpty();
    }
}
