namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Text.Json;
using Autheris.Api.Endpoints;
using Shouldly;
using Xunit;

public sealed class WebSqlDuplicateParameterNameTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("{\"p0\":\"a\",\"P0\":\"b\"}")]
    [InlineData("{\"@p0\":\"a\",\"p0\":\"b\"}")]
    [InlineData("{\"@P0\":\"a\",\"@p0\":\"b\"}")]
    public void WebSqlRequest_CaseOnlyDuplicateParameterNames_AreDetected(string parametersJson)
    {
        var dto = JsonSerializer.Deserialize<WebSqlEndpoints.WebSqlRequestDto>(
            "{\"sql\":\"SELECT 1\",\"parameters\":" + parametersJson + "}", Options);

        WebSqlEndpoints.HasCaseOnlyDuplicateParameterNames(dto!.Parameters).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WebSqlRequest_DistinctParameterNames_AreAccepted()
    {
        WebSqlEndpoints.HasCaseOnlyDuplicateParameterNames(new Dictionary<string, object?> { ["p0"] = 1, ["p1"] = 2, ["@x"] = 3 }).ShouldBeFalse();
        WebSqlEndpoints.HasCaseOnlyDuplicateParameterNames(null).ShouldBeFalse();
    }
}
