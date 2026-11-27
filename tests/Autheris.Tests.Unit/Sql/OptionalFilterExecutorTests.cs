namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;
using static Autheris.Tests.Unit.Sql.ClientListFixture;

/// <summary>Optional filter <c>(@p IS NULL OR X)</c> through the governed executor: null shape and bind-time data errors.</summary>
public sealed class OptionalFilterExecutorTests
{
    private const string Optional = "SELECT id FROM orders WHERE (@region IS NULL OR status = @region)";

    [Fact]
    [Trait("Category", "Security")]
    public async Task Absent_param_is_not_a_null_shape()
    {
        await using var f = await CreateAsync();

        // an absent parameter stays a missing parameter: it is never read as NULL, so it never drops the predicate
        await Should.ThrowAsync<MissingClientParameterException>(() => f.IdsAsync(Optional, new Dictionary<string, object?>()));

        // an explicit NULL is the null shape
        var ids = await f.IdsAsync(Optional, new Dictionary<string, object?> { ["region"] = null });
        ids.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Bind_data_error_is_audited_like_client_list_bind_error()
    {
        const string secret = "not-a-number-secret-991";
        await using var f = await CreateAsync();

        var listError = await Should.ThrowAsync<ClientListException>(() =>
            f.IdsAsync("SELECT id FROM orders WHERE id IN (@ids)", new Dictionary<string, object?> { ["ids"] = Json($"[1, \"{secret}\"]") }));
        var dataError = await Should.ThrowAsync<GovernedSqlException>(() =>
            f.IdsAsync("SELECT id FROM orders WHERE id = @x", new Dictionary<string, object?> { ["x"] = secret }));

        listError.Code.ShouldBe(GovernedSqlErrorCodes.ListParamType);
        dataError.Code.ShouldBe(GovernedSqlErrorCodes.DataError);
        dataError.Message.ShouldNotContain(secret);
        string everything = string.Join("\n", f.Audit.Select(e => e.EventType + " " + e.DetailsJson)) + "\n" + dataError;
        everything.ShouldNotContain(secret);

        var dataAudit = f.Audit.Last();
        dataAudit.DetailsJson!.ShouldContain(GovernedSqlErrorCodes.DataError);
        dataAudit.EventType.ShouldNotBe("WEBSQL_QUERY");
    }
}
