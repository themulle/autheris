using Autheris.Application.Sql.Tree;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Tests.Unit.TestSupport;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.GraphQL;

public sealed partial class GovernedTreeQueryServiceTests
{
    [Fact]
    public async Task CountAsync_AfterExecute_ReturnsParsedCount_AndReusesDecision()
    {
        var fixture = Create(stubReaderJson: "250");
        var opId = "op-count-1";
        var root = new TreeQueryNode(Authors, ["id"]) { Limit = 100 };
        (await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), root, null, opId)).Dispose();

        var count = await fixture.Service.CountAsync(DefaultTenantCaller.Principal(), root, null, opId);

        count.ShouldBe(250);
        await fixture.Resolver.Received(1).ResolveTableAccessAsync(Arg.Any<System.Security.Claims.ClaimsPrincipal?>(), Authors,
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string[]>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CountAsync_NoMemoizedDecision_ThrowsForbidden_NoResolveNoConnection()
    {
        var fixture = Create(stubReaderJson: "250");

        await Should.ThrowAsync<GatewayForbiddenException>(() =>
            fixture.Service.CountAsync(DefaultTenantCaller.Principal(), new TreeQueryNode(Authors, ["id"]), null, "op-never-executed"));

        await fixture.Resolver.DidNotReceiveWithAnyArgs().ResolveTableAccessAsync(default, default, default, default, default);
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CountAsync_DeniedTable_ThrowsForbidden_BeforeAnyConnection()
    {
        var fixture = Create(a => a[Authors] = a[Authors] with
        {
            Decision = TableAccessDecision.Denied(Authors, "no")
        });
        var opId = "op-count-denied";
        var root = new TreeQueryNode(Authors, ["id"]);
        await Should.ThrowAsync<GatewayForbiddenException>(() => fixture.ExecuteAsync(DefaultTenantCaller.Principal(), root, null, opId));

        await Should.ThrowAsync<GatewayForbiddenException>(() => fixture.Service.CountAsync(DefaultTenantCaller.Principal(), root, null, opId));

        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task CountAsync_UnbackedSource_IsDenied()
    {
        var fixture = Create(unbackedBlogSource: true, stubReaderJson: "5");
        var opId = "op-count-unbacked";
        var root = new TreeQueryNode(Authors, ["id"]);
        await Should.ThrowAsync<GatewayForbiddenException>(() => fixture.ExecuteAsync(DefaultTenantCaller.Principal(), root, null, opId));

        await Should.ThrowAsync<GatewayForbiddenException>(() => fixture.Service.CountAsync(DefaultTenantCaller.Principal(), root, null, opId));
        fixture.Factory.Opened.ShouldBe(0);
    }

    [Fact]
    public async Task CountAsync_GateFull_Throttles()
    {
        var gate = Substitute.For<Autheris.Application.Common.Interfaces.ITableReadConcurrencyGate>();
        var fixture = Create(
            options: new Autheris.Domain.Options.GatewayOptions { DataSources = new Autheris.Domain.Options.SqlDataSourceOptions { MaxConcurrentReadsPerUserAndTable = 1 } },
            gate: gate,
            stubReaderJson: "3");
        var opId = "op-count-gate";
        var root = new TreeQueryNode(Authors, ["id"]);
        gate.TryEnter(Arg.Any<string>(), Arg.Any<int>()).Returns(Substitute.For<IDisposable>());
        (await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), root, null, opId)).Dispose();
        gate.TryEnter(Arg.Any<string>(), Arg.Any<int>()).Returns((IDisposable?)null);

        await Should.ThrowAsync<GatewayThrottledException>(() => fixture.Service.CountAsync(DefaultTenantCaller.Principal(), root, null, opId));
    }

    [Fact]
    public async Task CountAsync_NonNumericResult_Throws()
    {
        var fixture = Create(stubReaderJson: "[]");
        var opId = "op-count-nan";
        var root = new TreeQueryNode(Authors, ["id"]);
        (await fixture.ExecuteAsync(DefaultTenantCaller.Principal(), root, null, opId)).Dispose();

        await Should.ThrowAsync<InvalidOperationException>(() => fixture.Service.CountAsync(DefaultTenantCaller.Principal(), root, null, opId));
    }

    [Fact]
    public async Task CountAsync_EmptyOperationId_Throws()
    {
        var fixture = Create();

        await Should.ThrowAsync<ArgumentException>(() =>
            fixture.Service.CountAsync(DefaultTenantCaller.Principal(), new TreeQueryNode(Authors, ["id"]), null, " "));
    }
}
