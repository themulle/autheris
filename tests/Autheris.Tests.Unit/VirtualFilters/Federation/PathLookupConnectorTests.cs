namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Connectors;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Connectors;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 CR-01: a path lookup sends one request per authorized key, keeps only the rows of the requested key,
/// de-duplicates by key and maps an upstream 404 for one key to "key not present". The result must not depend on what
/// the upstream does with the key (an upstream that ignores it answers every request with all rows).
/// </summary>
public sealed class PathLookupConnectorTests
{
    private static readonly IReadOnlyDictionary<string, object?>[] AllRows =
        Enumerable.Range(1, 5).Select(i => Row(("id", (object?)(long)i), ("name", "n" + i))).ToArray();

    private static VirtualFilterPathLookup Lookup(VirtualFilterKeyType type = VirtualFilterKeyType.IntegerNumber, params string[] values) =>
        new("id", "id", type, values);

    private static ConnectorSessionContext Session() =>
        new(User(), Tenant, Unrestricted(HttpTable("crm_api", "public", "customers", "id", "name")),
            ["id", "name"], new Dictionary<string, object?> { ["limit"] = 100 });

    private static (PathLookupConnector Connector, List<string> Requested) Build(
        VirtualFilterPathLookup lookup, Func<string, IReadOnlyList<IReadOnlyDictionary<string, object?>>> upstream)
    {
        var requested = new List<string>();
        var inner = Substitute.For<IAutherisConnector>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        inner.RecordSource.Returns(recordSource);
        inner.SplitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns([ConnectorSplit.Default("split")]);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var key = (string)ci.ArgAt<ConnectorSessionContext>(1).Arguments["id"]!;
                lock (requested)
                {
                    requested.Add(key);
                }

                return Task.FromResult(upstream(key));
            });
        return (new PathLookupConnector(inner, lookup, maxConcurrency: 2), requested);
    }

    private static async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadAsync(PathLookupConnector connector)
    {
        var splits = await connector.SplitManager.GetSplitsAsync(HttpTable("crm_api", "public", "customers", "id"), Session());
        return await connector.RecordSource.ReadBatchAsync(splits[0], Session());
    }

    [Fact]
    public async Task OneRequestPerKey_EachWithTheKeyAsTheArgument()
    {
        var (connector, requested) = Build(Lookup(values: ["1", "3", "4"]), key => [.. AllRows.Where(r => r["id"]!.ToString() == key)]);

        var rows = await ReadAsync(connector);

        requested.OrderBy(k => k).ShouldBe(["1", "3", "4"]);
        rows.Select(r => r["id"]).ShouldBe([1L, 3L, 4L]);
        connector.Stats.Requests.ShouldBe(3);
        connector.Stats.KeysNotFound.ShouldBe(0);
    }

    [Fact]
    public async Task UpstreamIgnoresTheKey_EachResponseIsRestrictedToTheRequestedKey_NoMultiplication()
    {
        var (connector, requested) = Build(Lookup(values: ["1", "3", "4"]), _ => AllRows);

        var rows = await ReadAsync(connector);

        requested.Count.ShouldBe(3);
        rows.Select(r => r["id"]).ShouldBe([1L, 3L, 4L]);   // exactly one row per key, not 3 x 5
    }

    [Fact]
    public async Task UpstreamRepeatsRows_AreDeduplicatedByTheKey()
    {
        var (connector, _) = Build(Lookup(values: ["2"]), _ => [AllRows[1], AllRows[1], Row(("id", (object?)2L), ("name", "other"))]);

        var rows = await ReadAsync(connector);

        rows.ShouldHaveSingleItem()["name"].ShouldBe("n2");
    }

    [Theory]
    [InlineData(VirtualFilterKeyType.IntegerNumber, "7", 7L)]
    [InlineData(VirtualFilterKeyType.IntegerNumber, "7", "7")]
    [InlineData(VirtualFilterKeyType.IntegerNumber, "7", 7.0)]
    [InlineData(VirtualFilterKeyType.Text, "ab", "ab")]
    public async Task RowKeys_AreComparedCanonically(VirtualFilterKeyType type, string requested, object rowValue)
    {
        var (connector, _) = Build(Lookup(type, requested), _ => [Row(("id", rowValue), ("name", "x"))]);

        (await ReadAsync(connector)).ShouldHaveSingleItem();
    }

    [Theory]
    [InlineData(VirtualFilterKeyType.IntegerNumber, "7", 8L)]
    [InlineData(VirtualFilterKeyType.IntegerNumber, "7", 7.5)]
    [InlineData(VirtualFilterKeyType.Text, "ab", "AB")]
    public async Task RowsOfAnotherKey_AreDropped(VirtualFilterKeyType type, string requested, object rowValue)
    {
        var (connector, _) = Build(Lookup(type, requested), _ => [Row(("id", rowValue), ("name", "x"))]);

        (await ReadAsync(connector)).ShouldBeEmpty();
    }

    [Fact]
    public async Task RowWithoutTheKeyColumnOrWithANullKey_IsDropped()
    {
        var (connector, _) = Build(Lookup(values: ["1"]), _ => [Row(("name", "no key")), Row(("id", (object?)null), ("name", "null key"))]);

        (await ReadAsync(connector)).ShouldBeEmpty();
    }

    [Fact]
    public async Task NotFoundForOneKey_MeansNoRowsForThatKey_QueryContinues_AndIsCounted()
    {
        var (connector, _) = Build(Lookup(values: ["1", "2", "3"]), key => key == "2"
            ? throw new HttpRequestException("not found", null, HttpStatusCode.NotFound)
            : [.. AllRows.Where(r => r["id"]!.ToString() == key)]);

        var rows = await ReadAsync(connector);

        rows.Select(r => r["id"]).ShouldBe([1L, 3L]);
        connector.Stats.Requests.ShouldBe(3);
        connector.Stats.KeysNotFound.ShouldBe(1);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task AnyOtherFailure_FailsTheWholeQuery(HttpStatusCode status)
    {
        var (connector, _) = Build(Lookup(values: ["1", "2"]), key => key == "2"
            ? throw new HttpRequestException("fail", null, status)
            : [AllRows[0]]);

        var ex = await Should.ThrowAsync<HttpRequestException>(() => ReadAsync(connector));

        ex.StatusCode.ShouldBe(status);
    }

    [Fact]
    public async Task AFailureWithoutAStatus_IsNotMistakenForNotFound()
    {
        var (connector, _) = Build(Lookup(values: ["1"]), _ => throw new HttpRequestException("connection refused"));

        await Should.ThrowAsync<HttpRequestException>(() => ReadAsync(connector));
    }

    [Fact]
    public async Task Requests_NeverExceedTheConcurrencyLimit()
    {
        int current = 0, max = 0;
        var inner = Substitute.For<IAutherisConnector>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        inner.RecordSource.Returns(recordSource);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                int now = Interlocked.Increment(ref current);
                int seen;
                while ((seen = Volatile.Read(ref max)) < now && Interlocked.CompareExchange(ref max, now, seen) != seen) { }
                await Task.Delay(20);
                Interlocked.Decrement(ref current);
                return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)[];
            });
        var connector = new PathLookupConnector(inner, Lookup(values: ["1", "2", "3", "4", "5", "6"]), maxConcurrency: 2);

        await connector.RecordSource.ReadBatchAsync(ConnectorSplit.Default("s"), Session());

        max.ShouldBeLessThanOrEqualTo(2);
        connector.Stats.Requests.ShouldBe(6);
    }

    [Fact]
    public async Task EachRequest_GetsItsOwnItemsDictionary_NotTheCallersSharedOne()
    {
        var seen = new List<IDictionary<string, object?>>();
        var inner = Substitute.For<IAutherisConnector>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        inner.RecordSource.Returns(recordSource);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var items = ci.ArgAt<ConnectorSessionContext>(1).Items;
                lock (seen)
                {
                    seen.Add(items);
                }

                items["written-by-connector"] = true;
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
            });
        var connector = new PathLookupConnector(inner, Lookup(values: ["1", "2", "3"]), maxConcurrency: 3);
        var session = Session();
        session.Items["caller"] = 1;

        await connector.RecordSource.ReadBatchAsync(ConnectorSplit.Default("s"), session);

        seen.Count.ShouldBe(3);
        seen.Distinct().Count().ShouldBe(3);
        seen.ShouldAllBe(items => !ReferenceEquals(items, session.Items));
        seen.ShouldAllBe(items => items.ContainsKey("caller"));
        session.Items.ContainsKey("written-by-connector").ShouldBeFalse();
    }

    [Fact]
    public async Task FirstFailure_CancelsTheSiblingRequestsStillInFlight()
    {
        var cancelled = 0;
        var inner = Substitute.For<IAutherisConnector>();
        var recordSource = Substitute.For<IConnectorRecordSource>();
        inner.RecordSource.Returns(recordSource);
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var key = (string)ci.ArgAt<ConnectorSessionContext>(1).Arguments["id"]!;
                if (key == "1")
                {
                    await Task.Delay(20);
                    throw new HttpRequestException("boom");
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, ci.ArgAt<CancellationToken>(2));
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref cancelled);
                    throw;
                }

                return (IReadOnlyList<IReadOnlyDictionary<string, object?>>)[];
            });
        var connector = new PathLookupConnector(inner, Lookup(values: ["1", "2", "3"]), maxConcurrency: 3);

        var read = connector.RecordSource.ReadBatchAsync(ConnectorSplit.Default("s"), Session());

        var ex = await Should.ThrowAsync<HttpRequestException>(() => read).WaitAsync(TimeSpan.FromSeconds(5));
        ex.Message.ShouldBe("boom");
        cancelled.ShouldBe(2);
    }
}
