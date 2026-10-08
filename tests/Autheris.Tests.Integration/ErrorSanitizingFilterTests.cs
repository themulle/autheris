using Autheris.Api.Middleware;
using HotChocolate;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration;

public class ErrorSanitizingFilterTests
{
    [Fact]
    public void ErrorSanitizingFilter_InNonDevelopment_StripsExceptionsFromGraphQLException()
    {
        // Finding C: In non-development, GraphQLException must not leak internal exceptions or stack traces
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Staging"); // Non-development

        var logger = Substitute.For<ILogger<ErrorSanitizingFilter>>();
        var filter = new ErrorSanitizingFilter(env, logger);

        var innerException = new InvalidOperationException("Internal database schema leak: table xyz crashed!");
        var error = ErrorBuilder.New()
            .SetMessage("Zugriff verweigert.")
            .SetCode("FORBIDDEN")
            .SetException(innerException)
            .Build();

        var sanitized = filter.OnError(error);

        sanitized.ShouldNotBeNull();
        sanitized.Code.ShouldBe("FORBIDDEN");
        sanitized.Exception.ShouldBeNull("Internal exception must be stripped in non-development");
    }

    [Fact]
    public void ErrorSanitizingFilter_NonWhitelistedCode_MaskedAsInternalServerError()
    {
        // Finding C: Unknown or technical error codes must be converted to INTERNAL_SERVER_ERROR in non-dev
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Production");

        var logger = Substitute.For<ILogger<ErrorSanitizingFilter>>();
        var filter = new ErrorSanitizingFilter(env, logger);

        var error = ErrorBuilder.New()
            .SetMessage("SQLite internal error: column not found")
            .SetCode("SQLITE_SYNTAX_ERROR")
            .SetException(new InvalidOperationException("Sqlite raw exception"))
            .Build();

        var sanitized = filter.OnError(error);

        sanitized.Code.ShouldBe("INTERNAL_SERVER_ERROR");
        sanitized.Message.ShouldBe("An internal server error occurred.");
        sanitized.Exception.ShouldBeNull();
    }

    [Fact]
    public void ErrorSanitizingFilter_InDevelopment_PreservesExceptionsForDebugging()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var logger = Substitute.For<ILogger<ErrorSanitizingFilter>>();
        var filter = new ErrorSanitizingFilter(env, logger);

        var inner = new InvalidOperationException("Dev error detail");
        var error = ErrorBuilder.New()
            .SetMessage("Some error")
            .SetCode("DEV_DEBUG")
            .SetException(inner)
            .Build();

        var sanitized = filter.OnError(error);

        sanitized.Exception.ShouldBe(inner);
    }
}
