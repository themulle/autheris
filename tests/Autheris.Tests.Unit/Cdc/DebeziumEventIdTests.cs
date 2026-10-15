namespace Autheris.Tests.Unit.Cdc;

using Autheris.Extensions.Cdc;
using Shouldly;
using Xunit;

public sealed class DebeziumEventIdTests
{
    private const string Body = """{"op":"c","source":{"schema":"public","table":"orders","name":"srv","ts_ms":1760000000000},"after":{"id":1,"tenant_id":"t1"}}""";

    [Fact]
    public void SameBody_YieldsSameEventId()
    {
        var a = DebeziumCdcParser.Parse(Body, null, null);
        var b = DebeziumCdcParser.Parse(Body, null, null);

        a.EventId.ShouldBe(b.EventId);
    }

    [Fact]
    public void SurroundingWhitespace_DoesNotChangeEventId() =>
        DebeziumCdcParser.Parse("  \n" + Body + "\n", null, null).EventId.ShouldBe(DebeziumCdcParser.Parse(Body, null, null).EventId);

    [Fact]
    public void DifferentBody_YieldsDifferentEventId() =>
        DebeziumCdcParser.Parse(Body.Replace("\"id\":1", "\"id\":2"), null, null).EventId
            .ShouldNotBe(DebeziumCdcParser.Parse(Body, null, null).EventId);

    [Fact]
    public void EventId_IsOpaquePrefixedHash()
    {
        var id = DebeziumCdcParser.Parse(Body, null, null).EventId;

        id.ShouldStartWith("dbz-");
        id.Length.ShouldBe(4 + 32);
        id.ShouldNotContain("orders");
    }
}
