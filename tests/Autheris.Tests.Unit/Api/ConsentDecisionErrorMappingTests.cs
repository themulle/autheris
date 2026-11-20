namespace Autheris.Tests.Unit.Api;

using Autheris.Api.Middleware;
using Autheris.Tests.Common;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Consent decision outcomes raised through GraphQL keep their governed code and a fixed text; they are not masked as INTERNAL_SERVER_ERROR.</summary>
public sealed class ConsentDecisionErrorMappingTests
{
    [Theory]
    [InlineData("CONSENT_REQUEST_NOT_FOUND", "The consent request was not found.")]
    [InlineData("PRECONDITION_FAILED", "The consent request was changed in the meantime.")]
    [InlineData("WORKFLOW_MODE_MISMATCH", "The consent request cannot be decided through this channel.")]
    [InlineData("FOUR_EYES_SAME_APPROVER", "A different approver is required for the next approval step.")]
    public void DecisionCode_KeepsCodeAndFixedText_InProduction(string code, string expectedMessage)
    {
        IHostEnvironment env = new TestHostEnvironment("Production");
        var filter = new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);
        var error = ErrorBuilder.New().SetCode(code).SetMessage("Consent request '3f2504e0-4f89-41d3-9a0c-0305e82c3301' detail.").Build();

        var sanitized = filter.OnError(error);

        sanitized.Code.ShouldBe(code);
        sanitized.Message.ShouldBe(expectedMessage);
    }
}
