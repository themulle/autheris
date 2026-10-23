using Autheris.Tests.Parity.Channels;
using Autheris.Tests.Parity.Infrastructure;
using Shouldly;
using Xunit.Sdk;

namespace Autheris.Tests.Parity.Invariant;

/// <summary>Pure unit tests of the no-5xx / no-leak invariant (D-9, SEC-PAR-07). No host needed.</summary>
[Trait("Category", "Parity")]
public sealed class ParityAssertTests : ParityTestBase
{
    private static ChannelResult Result(int status, string? code, string body) => new(status, code, ChannelResult.NoRows, body);

    private static ChannelResult Ok() => Result(200, null, "{\"columns\":[\"n\"],\"rows\":[{\"n\":1}]}");

    [ParityFact]
    public void NoServerError_FailsOn500() =>
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(Result(500, "InternalServerError", "{}")));

    [ParityTheory]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void NoServerError_FailsOnEvery5xxExceptNotImplemented(int status) =>
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(Result(status, "BadGateway", "{}")));

    [ParityFact]
    public void NoServerError_Tolerates501NotImplemented()
    {
        Should.NotThrow(() => ParityAssert.NoServerError(Result(501, "NotImplemented", "{\"error\":\"NotImplemented\"}")));
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(Result(501, "Whatever", "{}")));
    }

    [ParityTheory]
    [InlineData("INTERNAL_SERVER_ERROR")]
    [InlineData("InternalServerError")]
    public void NoServerError_FailsOnInternalServerErrorCode(string code) =>
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(Result(200, code, "{\"errors\":[{\"extensions\":{\"code\":\"" + code + "\"}}]}")));

    [ParityFact]
    public void NoServerError_FailsOnStackTrace() =>
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(
            Result(400, "BAD_REQUEST", "{\"error\":\"x\",\"detail\":\"at Autheris.Api.Foo.Bar(String s) in /src/Foo.cs:line 12\"}")));

    [ParityFact]
    public void NoServerError_FailsOnUnknownCode()
    {
        var unknown = Result(400, "SOMETHING_NEW", "{\"error\":\"x\"}");
        Should.NotThrow(() => ParityAssert.NoServerError(unknown));
        Should.Throw<XunitException>(() => ParityAssert.StableCode(unknown));
        Should.Throw<XunitException>(() => ParityAssert.StableCode(Result(400, null, "{\"error\":\"x\"}")));
    }

    [ParityFact]
    public void NoServerError_UnknownCodeIsNotAHardFailure() =>
        // A missing or unknown 4xx code is a functional gap (KnownGap-able), never a hard failure.
        Should.Throw<XunitException>(() => ParityAssert.StableCode(Result(422, "NOPE", "{}"))).ShouldNotBeOfType<ParityHardFailureException>();

    [ParityTheory]
    [InlineData(400, "BAD_REQUEST")]
    [InlineData(400, "BadRequest")]
    [InlineData(403, "SQL_POLICY_DENIED")]
    [InlineData(422, "SQL_CONSTRUCT_NOT_SUPPORTED")]
    [InlineData(401, "UNAUTHORIZED")]
    public void NoServerError_AcceptsStable4xx(int status, string code)
    {
        var r = Result(status, code, "{\"error\":\"Invalid SQL syntax.\",\"code\":\"" + code + "\"}");
        ParityAssert.NoServerError(r);
        ParityAssert.StableCode(r);
    }

    [ParityFact]
    public void NoServerError_AcceptsSuccess()
    {
        ParityAssert.NoServerError(Ok());
        ParityAssert.StableCode(Ok());
    }

    [ParityFact]
    public void NoServerError_DetectsGraphQlErrorsWithoutCodeAsUnstable()
    {
        var body = "{\"errors\":[{\"message\":\"The field `items` does not exist.\",\"extensions\":{\"field\":\"items\"}}]}";
        var r = Result(400, null, body);
        ParityAssert.NoServerError(r);
        Should.Throw<XunitException>(() => ParityAssert.StableCode(r));
        // GraphQL reports errors with HTTP 200 as well.
        Should.Throw<XunitException>(() => ParityAssert.StableCode(Result(200, null, body)));
    }

    [ParityFact]
    public void NoServerError_CannotBeConvertedToAKnownGap()
    {
        // SEC-PAR-05b: a hard failure thrown inside a gapped body stays a failure.
        Should.Throw<ParityHardFailureException>(() =>
            KnownGap.RunAsync("PARITY-GAP-001", () => { ParityAssert.NoServerError(Result(500, null, "{}")); return Task.CompletedTask; }));
    }

    // ---- SEC-PAR-07 leak matchers: positive and negative fixtures ----

    public static IEnumerable<object[]> LeakFixtures() =>
    [
        ["stack frame", "at Autheris.Api.Endpoints.Foo.Bar(Int32 x)"],
        ["source line", "Foo.cs:line 42"],
        ["exception type", "System.InvalidOperationException: boom"],
        ["microsoft type", "Microsoft.Data.Sqlite.SqliteConnection"],
        ["sqlite message", "SQLite Error 1: 'no such column: nope'."],
        ["sqlite exception", "SqliteException"],
        ["syntax near", "near \"FROM\": syntax error"],
        ["no such table", "no such table: parity_sales"],
        ["npgsql", "Npgsql.PostgresException"],
        ["sql exception", "SqlException (0x80131904)"],
        ["windows path", "C:\\\\Users\\\\build\\\\work"],
        ["unix path home", "/home/runner/work/app.cs"],
        ["unix path src", "failed in /src/Autheris.Api/Program.cs"],
        ["connection string", "Data Source=/tmp/data.db;Mode=ReadWrite"],
        ["password token", "Server=x;Password=hunter2"],
    ];

    [ParityTheory]
    [MemberData(nameof(LeakFixtures))]
    [Trait("Category", "Security")]
    public void NoServerError_FailsOnSqlEngineMessage_AndEveryLeakToken(string name, string leak)
    {
        // SEC-PAR-07: the check applies to all status codes (4xx and 501 included), not only 5xx.
        foreach (var status in new[] { 400, 404, 422, 501 })
        {
            var r = Result(status, status == 501 ? "NotImplemented" : "BAD_REQUEST", "{\"error\":\"BadRequest\",\"message\":\"" + leak.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"}");
            Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(r), $"{name} @ {status}");
        }
    }

    [ParityFact]
    [Trait("Category", "Security")]
    public void NoServerError_FailsOnStackTraceIn4xx() =>
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(
            Result(404, "RESOURCE_NOT_FOUND", "{\"title\":\"x\",\"detail\":\"   at Foo.Bar() in x.cs:line 3\"}")));

    [ParityFact]
    [Trait("Category", "Security")]
    public void NoServerError_FailsOnFilePathAndConnectionString()
    {
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoLeak("{\"message\":\"/root/x.db\"}"));
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoLeak("{\"message\":\"Data Source=x\"}"));
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoLeak("{\"message\":\"c:\\\\temp\\\\x\"}"));
    }

    [ParityFact]
    [Trait("Category", "Security")]
    public void NoServerError_FailsOnEchoedSubmittedSqlIn5xx()
    {
        const string sql = "SELECT secret_column FROM hidden_table WHERE x = 1";
        Should.Throw<ParityHardFailureException>(() => ParityAssert.NoServerError(Result(502, "BadGateway", "{\"message\":\"failed: " + sql + "\"}"), sql));
    }

    [ParityFact]
    [Trait("Category", "Security")]
    public void NoServerError_AcceptsStable4xxWithoutLeakTokens()
    {
        string[] bodies =
        [
            "{\"error\":\"BadRequest\",\"code\":\"SQL_SYNTAX_ERROR\",\"message\":\"Invalid SQL syntax.\",\"traceId\":\"ed2434e6931cbdceacce75996aeba0df\"}",
            "{\"error\":\"Forbidden\",\"code\":\"SQL_POLICY_DENIED\",\"message\":\"Access to a referenced table or column is denied.\"}",
            "{\"type\":\"https://httpwg.org/specs/rfc9110.html#status.400\",\"title\":\"Bad Request\",\"status\":400,\"code\":\"BAD_REQUEST\"}",
        ];
        foreach (var body in bodies)
        {
            ParityAssert.NoServerError(Result(400, "BAD_REQUEST", body));
        }

        ParityAssert.FindLeaks("Column 'a' does not exist on table 'main.parity_sales'.").ShouldBeEmpty();
    }

    [ParityFact]
    public void ParityErrorCodes_ContainsPublicConstantsAndWebSqlTokens()
    {
        ParityErrorCodes.All.ShouldContain("BAD_REQUEST");
        ParityErrorCodes.All.ShouldContain("SQL_SYNTAX_ERROR");
        ParityErrorCodes.All.ShouldContain("FORBIDDEN");
        ParityErrorCodes.All.ShouldContain("BadRequest");
        ParityErrorCodes.All.ShouldContain("TooManyRequests");
        ParityErrorCodes.All.ShouldNotContain("INTERNAL_SERVER_ERROR");
        ParityErrorCodes.All.ShouldNotContain("InternalServerError");
        ParityErrorCodes.All.Count.ShouldBeGreaterThan(40);
    }
}
