namespace Autheris.Tests.Unit.Security;

using System;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Xunit;

public sealed class RebacTableGateUnitTests
{
    private readonly IRebacEvaluator _evaluator = Substitute.For<IRebacEvaluator>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly TableIdentifier _table = TableIdentifier.Parse("erp.finance.ledger");
    private readonly TenantId _tenant = new("tenant-alpha");
    private readonly Sid _subject = new("user-101");

    [Fact]
    public void CanonicalObjectIds_FormatCorrectly()
    {
        RebacTableGate.Relation.ShouldBe("can_query");
        RebacTableGate.ObjectId(_table).ShouldBe("table:erp.finance.ledger");
        RebacTableGate.SchemaObjectId("erp", "finance").ShouldBe("schema:erp.finance");
        RebacTableGate.SchemaObjectId(_table).ShouldBe("schema:erp.finance");
        RebacTableGate.DomainObjectId("erp").ShouldBe("domain:erp");
        RebacTableGate.DomainObjectId(_table).ShouldBe("domain:erp");
    }

    [Fact]
    public void IsEnforcedOnQueryPaths_EvaluatesOptionsCorrectly()
    {
        RebacTableGate.IsEnforcedOnQueryPaths(null).ShouldBeFalse();

        var disabledOptions = new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = false, EnforceOnQueryPaths = true }
        };
        RebacTableGate.IsEnforcedOnQueryPaths(disabledOptions).ShouldBeFalse();

        var unenforcedPathsOptions = new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true, EnforceOnQueryPaths = false }
        };
        RebacTableGate.IsEnforcedOnQueryPaths(unenforcedPathsOptions).ShouldBeFalse();

        var fullyEnabled = new GatewayOptions
        {
            Rebac = new RebacOptions { Enabled = true, EnforceOnQueryPaths = true }
        };
        RebacTableGate.IsEnforcedOnQueryPaths(fullyEnabled).ShouldBeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_EvaluatorNullOrDisabled_ReturnsFalseFailClosed()
    {
        var allowedNull = await RebacTableGate.IsAllowedAsync(null, _tenant, _subject, _table, CancellationToken.None);
        allowedNull.ShouldBeFalse();

        _evaluator.IsEnabled.Returns(false);
        // Even if evaluator would return permitted, disabled evaluator MUST fail-closed without calling CheckAsync
        _evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(RebacCheckResult.Permitted));

        var allowedDisabled = await RebacTableGate.IsAllowedAsync(_evaluator, _tenant, _subject, _table, CancellationToken.None);
        allowedDisabled.ShouldBeFalse();
        await _evaluator.DidNotReceive().CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsAllowedAsync_EvaluatorReturnsAllowed_ReturnsTrue()
    {
        _evaluator.IsEnabled.Returns(true);
        _evaluator.CheckAsync(Arg.Is<RebacCheckRequest>(r =>
            r.TenantId == _tenant.Value &&
            r.User == _subject.Value &&
            r.Relation == "can_query" &&
            r.Object == "table:erp.finance.ledger"), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(RebacCheckResult.Permitted));

        var allowed = await RebacTableGate.IsAllowedAsync(_evaluator, _tenant, _subject, _table, CancellationToken.None);
        allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task IsAllowedAsync_EvaluatorReturnsDenied_ReturnsFalse()
    {
        _evaluator.IsEnabled.Returns(true);
        _evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<RebacCheckResult>(RebacCheckResult.Denied));

        var allowed = await RebacTableGate.IsAllowedAsync(_evaluator, _tenant, _subject, _table, CancellationToken.None);
        allowed.ShouldBeFalse();
    }

    [Fact]
    public async Task IsAllowedAsync_EvaluatorThrowsException_ReturnsFalseFailClosed()
    {
        _evaluator.IsEnabled.Returns(true);
        _evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("Redis backend timed out"));

        var allowed = await RebacTableGate.IsAllowedAsync(_evaluator, _tenant, _subject, _table, CancellationToken.None, _logger);
        allowed.ShouldBeFalse();

        _logger.ReceivedWithAnyArgs(1).Log(
            LogLevel.Error,
            default,
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task IsAllowedAsync_OperationCanceledException_Propagates()
    {
        _evaluator.IsEnabled.Returns(true);
        _evaluator.CheckAsync(Arg.Any<RebacCheckRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await RebacTableGate.IsAllowedAsync(_evaluator, _tenant, _subject, _table, CancellationToken.None, _logger));
    }
}
