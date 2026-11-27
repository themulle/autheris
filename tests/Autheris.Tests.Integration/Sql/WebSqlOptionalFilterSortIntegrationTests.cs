namespace Autheris.Tests.Integration.Sql;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Sql.Errors;
using Autheris.Tests.Common;
using Shouldly;
using Xunit;
using static Autheris.Tests.Integration.Sql.ClientListPgFixture;

/// <summary>Optional filters through the whole governed pipeline against PostgreSQL; JSON null versus value versus absent.</summary>
public sealed class WebSqlOptionalFilterSortIntegrationTests
{
    private const string Optional = "SELECT id FROM orders WHERE (@status IS NULL OR status = @status) ORDER BY id";

    private static Dictionary<string, object?> P(string name, string json) => new() { [name] = Json(json) };

    [RequiresContainerRuntimeFact]
    public async Task OptionalFilter_JsonNull_ReturnsAllTenantRows()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        (await f.IdsAsync(Optional, P("status", "null"))).ShouldBe([1, 2, 3, 4, 5]);
    }

    [RequiresContainerRuntimeFact]
    public async Task OptionalFilter_Value_Filters()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        (await f.IdsAsync(Optional, P("status", "\"paid\""))).ShouldBe([3, 4]);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task OptionalFilter_AbsentParameter_IsRefused()
    {
        await using var f = await CreateAsync();
        if (f is null) return;
        var ex = await Should.ThrowAsync<ArgumentException>(() => f.IdsAsync(Optional, new Dictionary<string, object?>()));

        // the client gets the syntax error answer (400), never rows and never a dropped predicate
        SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
        mapped.Status.ShouldBe(400);
        mapped.Code.ShouldBe("SQL_SYNTAX_ERROR");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public async Task OptionalFilter_NullWithRequiredFilterTable_Returns400RequiredFilterMissing()
    {
        await using var f = await CreateAsync(requiredFilterColumns: ["status"]);
        if (f is null) return;

        foreach (var parameters in new[] { P("status", "null"), P("status", "\"paid\"") })
        {
            var ex = await Should.ThrowAsync<Exception>(() => f.IdsAsync(Optional, parameters));
            SqlCompilerErrorMapper.TryMap(ex, out var mapped).ShouldBeTrue();
            mapped.Code.ShouldBe("REQUIRED_FILTER_COLUMN_MISSING");
            mapped.Status.ShouldBe(400);
        }
    }
}
