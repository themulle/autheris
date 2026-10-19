using System.Text;
using Autheris.Application.Streaming.Crypto;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.CdcStream;

/// <summary>REQ-CDC-99 I1-C: payload codec (D-A7) and event id (D-A10). SEC-CDC99-16, SEC-CDC99-19.</summary>
public sealed class CdcPayloadCodecTests
{
    private static readonly Guid SampleGuid = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");

    private static Dictionary<string, object?> Row() => new()
    {
        ["S"] = "text \"quoted\" ü",
        ["I64"] = 9_000_000_000L,
        ["I32"] = -5,
        ["I16"] = (short)7,
        ["U8"] = (byte)255,
        ["Dec"] = 12.30m,
        ["F64"] = 1.5d,
        ["F32"] = 2.25f,
        ["Bool"] = true,
        ["Dt"] = new DateTime(2026, 10, 15, 8, 30, 0, DateTimeKind.Utc),
        ["Dto"] = new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.FromHours(2)),
        ["Date"] = new DateOnly(2026, 10, 15),
        ["Time"] = new TimeOnly(8, 30, 15),
        ["Guid"] = SampleGuid,
        ["Bin"] = new byte[] { 1, 2, 3 },
        ["Null"] = null,
    };

    [Fact]
    public void ChangePayload_RoundTripsAllTypesWithTheirClrTypes()
    {
        var row = Row();
        var payload = new CdcChangePayload(new Dictionary<string, object?> { ["Id"] = 1 }, row, null, true, false);

        var decoded = CdcPayloadCodec.DecodeChange(CdcPayloadCodec.EncodeChange(payload));

        decoded.BeforeUnknown.ShouldBeTrue();
        decoded.PayloadOversize.ShouldBeFalse();
        decoded.After.ShouldBeNull();
        decoded.Key["Id"].ShouldBe(1);
        foreach (var (k, v) in row)
        {
            var actual = decoded.Before![k];
            if (v is byte[] b)
            {
                ((byte[])actual!).ShouldBe(b);
            }
            else
            {
                actual.ShouldBe(v);
                actual?.GetType().ShouldBe(v!.GetType());
            }
        }

        ((DateTime)decoded.Before!["Dt"]!).Kind.ShouldBe(DateTimeKind.Utc);
        ((DateTimeOffset)decoded.Before["Dto"]!).Offset.ShouldBe(TimeSpan.FromHours(2));
    }

    [Fact]
    public void DecodedDictionaries_AreImmutable()
    {
        var payload = new CdcChangePayload(new Dictionary<string, object?> { ["Id"] = 1 }, null, Row(), false, false);
        var decoded = CdcPayloadCodec.DecodeChange(CdcPayloadCodec.EncodeChange(payload));
        decoded.After.ShouldNotBeNull();
        Should.Throw<NotSupportedException>(() => ((IDictionary<string, object?>)decoded.After!)["S"] = "x");
        decoded.After.ShouldNotBeAssignableTo<Dictionary<string, object?>>();
    }

    [Fact]
    public void Encode_UnsupportedClrType_FailsWithStableCode()
    {
        var payload = new CdcChangePayload(new Dictionary<string, object?> { ["Id"] = 1 }, null, new Dictionary<string, object?> { ["X"] = new object() }, false, false);
        var ex = Should.Throw<CdcPayloadCodecException>(() => CdcPayloadCodec.EncodeChange(payload));
        ex.Code.ShouldBe(CdcActivationCodes.UnsupportedType);
    }

    [Fact]
    public void Decode_UnknownTag_IsRejected() =>
        Should.Throw<CdcPayloadCodecException>(() => CdcPayloadCodec.DecodeChange(Encoding.UTF8.GetBytes("{\"k\":{\"A\":{\"t\":\"System.Diagnostics.Process\",\"v\":\"x\"}},\"b\":null,\"a\":null,\"bu\":false,\"po\":false}")));

    [Fact]
    public void Decode_OverDeepInput_IsRejected()
    {
        var deep = new StringBuilder();
        for (var i = 0; i < 200; i++) deep.Append("{\"x\":");
        deep.Append('1');
        for (var i = 0; i < 200; i++) deep.Append('}');
        Should.Throw<CdcPayloadCodecException>(() => CdcPayloadCodec.DecodeChange(Encoding.UTF8.GetBytes(deep.ToString())));
    }

    [Fact]
    public void Decode_OverSizedInput_IsRejected() =>
        Should.Throw<CdcPayloadCodecException>(() => CdcPayloadCodec.DecodeChange(new byte[CdcPayloadCodec.MaxDecodeBytes + 1]));

    [Fact]
    public void Decode_GarbageAndMissingFields_AreRejected()
    {
        Should.Throw<CdcPayloadCodecException>(() => CdcPayloadCodec.DecodeChange(Encoding.UTF8.GetBytes("not json")));
        Should.Throw<CdcPayloadCodecException>(() => CdcPayloadCodec.DecodeChange(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void ControlPayload_RoundTrips_AndCarriesNoRowData()
    {
        var control = new CdcControlPayload("watermark_expired", 5, 10, 20);
        var bytes = CdcPayloadCodec.EncodeControl(control);
        CdcPayloadCodec.DecodeControl(bytes).ShouldBe(control);
        Encoding.UTF8.GetString(bytes).ShouldNotContain("\"k\"");
    }

    [Fact]
    public void EventId_FormatsDeterministicallyAndMatchesPattern()
    {
        var id = new CdcStreamId("0123456789abcdef0123456789abcdef");
        var evt = CdcEventId.Format(id, 42);
        evt.ShouldBe("evt_0123456789abcdef0123456789abcdef_42");
        System.Text.RegularExpressions.Regex.IsMatch(evt, "^evt_[0-9a-f]{32}_[0-9]+$").ShouldBeTrue();
        CdcEventId.Format(id, 42).ShouldBe(evt);
        evt.Length.ShouldBeLessThanOrEqualTo(58);
        Should.Throw<ArgumentOutOfRangeException>(() => CdcEventId.Format(id, 0));
    }
}
