namespace Autheris.Tests.Unit.Api;

using Autheris.Api.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

/// <summary>NL-SC-04 (O-5a): governed ProblemDetails carry <c>code</c> and <c>detail</c> but no top-level <c>error</c> member.</summary>
[Trait("Category", "Security")]
public sealed class ProblemDetailsShapeTests
{
    [Fact]
    public void Problem_Details_Has_No_Top_Level_Error()
    {
        var result = ApiProblems.Problem(StatusCodes.Status403Forbidden, "FORBIDDEN", "Not allowed.");
        var problem = result.ShouldBeOfType<ProblemHttpResult>().ProblemDetails;

        problem.Extensions.ContainsKey("error").ShouldBeFalse();
        problem.Extensions["code"].ShouldBe("FORBIDDEN");
        problem.Detail.ShouldBe("Not allowed.");
        problem.Status.ShouldBe(403);
        problem.Type.ShouldBe("urn:autheris:error:FORBIDDEN");
    }

    [Fact]
    public void Created_Problem_Details_Has_No_Top_Level_Error()
    {
        var problem = ApiProblems.CreateProblemDetails(StatusCodes.Status400BadRequest, "BAD", "Bad request.");

        problem.Extensions.ContainsKey("error").ShouldBeFalse();
        problem.Extensions["code"].ShouldBe("BAD");
    }
}
