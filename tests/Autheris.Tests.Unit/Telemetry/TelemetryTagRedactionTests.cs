using System.Diagnostics;
using Autheris.Infrastructure.Telemetry;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Telemetry;

/// <summary>
/// .NET 11 hosting emits OpenTelemetry HTTP semantic-convention tags (<c>url.query</c>, <c>url.full</c>, request headers) on the
/// <c>Microsoft.AspNetCore</c> activities. The redaction processor must keep the query string, the credentials in headers and
/// statement text out of every exported span, whichever component sets the tag.
/// </summary>
[Trait("Category", "Security")]
public sealed class TelemetryTagRedactionTests : IDisposable
{
    private readonly ActivitySource _source = new("Microsoft.AspNetCore");
    private readonly ActivityListener _listener;

    public TelemetryTagRedactionTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == "Microsoft.AspNetCore",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private Activity Start()
    {
        var activity = _source.StartActivity("GET /api/orders");
        activity.ShouldNotBeNull();
        return activity;
    }

    private static void Redact(Activity activity) => new SpanRedactionProcessor().OnEnd(activity);

    [Fact]
    public void UrlQuery_IsRemoved()
    {
        using var activity = Start();
        activity.SetTag("url.query", "access_token=secret-token&x=1");

        Redact(activity);

        activity.GetTagItem("url.query").ShouldBeNull();
    }

    [Fact]
    public void UrlFull_LosesItsQueryString()
    {
        using var activity = Start();
        activity.SetTag("url.full", "https://gateway.example.org/api/orders?access_token=secret-token");

        Redact(activity);

        activity.GetTagItem("url.full").ShouldBe("https://gateway.example.org/api/orders");
    }

    [Theory]
    [InlineData("http.request.header.authorization")]
    [InlineData("http.request.header.cookie")]
    [InlineData("http.request.header.x-forwarded-user")]
    [InlineData("http.response.header.set-cookie")]
    public void CredentialBearingHttpHeaders_AreRemoved(string headerTag)
    {
        using var activity = Start();
        activity.SetTag(headerTag, "Bearer secret-token");

        Redact(activity);

        activity.GetTagItem(headerTag).ShouldBeNull();
    }

    [Fact]
    public void DbStatement_IsRemoved()
    {
        using var activity = Start();
        activity.SetTag("db.statement", "SELECT * FROM users WHERE token = 'secret-token'");

        Redact(activity);

        activity.GetTagItem("db.statement").ShouldBeNull();
    }

    [Fact]
    public void NoRemainingTagValueContainsTheSecret()
    {
        using var activity = Start();
        activity.SetTag("url.query", "access_token=secret-token");
        activity.SetTag("url.full", "https://gateway.example.org/x?access_token=secret-token");
        activity.SetTag("http.request.header.authorization", "Bearer secret-token");
        activity.SetTag("http.route", "/api/orders");

        Redact(activity);

        activity.TagObjects
            .Select(static t => t.Value?.ToString() ?? string.Empty)
            .ShouldAllBe(static v => !v.Contains("secret-token", StringComparison.Ordinal));
        activity.GetTagItem("http.route").ShouldBe("/api/orders");
    }
}
