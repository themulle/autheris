using Autheris.Api.Middleware;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.ConsentRequests;

/// <summary>WP-0 (PLAN-GOV-113, F-1): the denial and lost-race codes reach the client unchanged outside Development and are no server fault.</summary>
public sealed class ConsentDecisionErrorSanitizingTests
{
    private const string NotPermitted = "Only a person can approve or reject consent requests.";
    private const string NotPending = "The consent request is no longer pending.";

    [Theory]
    [InlineData("DECISION_NOT_PERMITTED", NotPermitted, "Staging")]
    [InlineData("DECISION_NOT_PERMITTED", NotPermitted, "Production")]
    [InlineData("ACCESS_REQUEST_NOT_PENDING", NotPending, "Staging")]
    [InlineData("ACCESS_REQUEST_NOT_PENDING", NotPending, "Production")]
    public void DecisionCodes_PassThroughUnchangedOutsideDevelopment_AndAreNotLoggedAtError(string code, string message, string environment)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(environment);
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var filter = new ErrorSanitizingFilter(env, factory.CreateLogger<ErrorSanitizingFilter>());

        // The exception is attached on purpose: a refused decision must not be logged as a server fault even then.
        var error = ErrorBuilder.New()
            .SetCode(code)
            .SetMessage(message)
            .SetException(new GraphQLException(message))
            .Build();

        var sanitized = filter.OnError(error);

        sanitized.Code.ShouldBe(code);
        sanitized.Message.ShouldBe(message);
        sanitized.Exception.ShouldBeNull();
        logs.Entries.ShouldNotContain(e => e.Level >= LogLevel.Error);
    }
}
