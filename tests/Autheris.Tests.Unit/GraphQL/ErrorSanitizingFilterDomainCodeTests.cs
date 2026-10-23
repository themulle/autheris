namespace Autheris.Tests.Unit.GraphQL;

using Autheris.Api.Middleware;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SEC-PG-12 (WP-D): client-meaningful consent and grant codes survive in every environment (the Development passthrough is gone)
/// with a fixed message; the thrower's text (it can contain ex.Message, hosts, ids) never reaches the client.
/// </summary>
[Trait("Category", "Security")]
public sealed class ErrorSanitizingFilterDomainCodeTests
{
    private const string Leak = "connect failed host=itsm.internal.example password=hunter2 table hr.salaries";

    private static ErrorSanitizingFilter CreateFilter(string envName)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(envName);
        return new ErrorSanitizingFilter(env, NullLogger<ErrorSanitizingFilter>.Instance);
    }

    [Theory]
    [InlineData("Development", "ITSM_UNAVAILABLE")]
    [InlineData("Production", "ITSM_UNAVAILABLE")]
    [InlineData("Development", "INVALID_STATE")]
    [InlineData("Production", "INVALID_STATE")]
    [InlineData("Production", "CONSENT_ON_ATTRIBUTE_SOURCE")]
    [InlineData("Production", "INDIVIDUAL_GRANT_REQUIRES_EXPIRY")]
    [InlineData("Production", "INDIVIDUAL_GRANT_EXPIRY_IN_PAST")]
    [InlineData("Production", "INDIVIDUAL_GRANT_EXCEEDS_MAXIMUM")]
    [InlineData("Production", "INDIVIDUAL_GRANT_REQUIRES_FOUR_EYES")]
    [InlineData("Production", "INDIVIDUAL_GRANT_RENEWAL_BY_GRANTEE")]
    [InlineData("Production", "INDIVIDUAL_GRANT_RENEWAL_REQUIRES_CAMPAIGN")]
    [InlineData("Production", "INDIVIDUAL_GRANT_DISABLED_FOR_REALM")]
    [InlineData("Production", "INDIVIDUAL_GRANT_REQUIRES_JUSTIFICATION")]
    [InlineData("Production", "INDIVIDUAL_GRANT_INDETERMINATE")]
    public void SecPg12Wpd_ClientMeaningfulDomainCode_KeepsCodeWithFixedMessage(string env, string code)
    {
        var error = ErrorBuilder.New().SetMessage(Leak).SetCode(code).SetException(new InvalidOperationException(Leak)).Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe(code);
        result.Message.ShouldNotContain("itsm.internal");
        result.Message.ShouldNotContain("hunter2");
        result.Message.ShouldNotContain("hr.salaries");
        result.Message.ShouldNotBe("An internal server error occurred.");
        result.Message.ShouldNotBeNullOrWhiteSpace();
        if (env == "Production")
        {
            result.Exception.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void SecPg12Wpd_UnlistedDomainCode_StillFailsClosedToInternalError(string env)
    {
        var error = ErrorBuilder.New().SetMessage(Leak).SetCode("SOME_UNLISTED_CODE").Build();

        var result = CreateFilter(env).OnError(error);

        result.Code.ShouldBe("INTERNAL_SERVER_ERROR");
        result.Message.ShouldNotContain("hunter2");
    }
}
