namespace Autheris.Tests.Unit.Governance;

using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using System.Data.Common;
using Shouldly;
using TrinoSqlEngine.Ast.Security;
using Xunit;

/// <summary>The dry run adapter of query analysis: execution's mapped refusal, or an exception, never "valid" by accident.</summary>
public sealed class GovernedSqlCompileParityCheckTests
{
    private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity(
        [new Claim(ClaimTypes.PrimarySid, "S-1-USER"), new Claim("tenant_id", "tenant-a")], "Test"));

    private sealed class FakeExecution(Func<Exception?> fail) : IGovernedSqlExecutionService
    {
        public string? LastDataSource { get; private set; }

        public TenantId? LastTenant { get; private set; }

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, string? dataSourceName, CancellationToken ct = default)
        {
            LastDataSource = dataSourceName;
            LastTenant = tenantId;
            return fail() is { } ex ? Task.FromException(ex) : Task.CompletedTask;
        }

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ExecuteGovernedQueryAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, Func<DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static (GovernedSqlCompileParityCheck Check, FakeExecution Execution) Create(Func<Exception?>? fail = null)
    {
        var execution = new FakeExecution(fail ?? (() => null));
        return (new GovernedSqlCompileParityCheck(execution), execution);
    }

    [Fact]
    public async Task Success_ReturnsNull()
    {
        var (check, execution) = Create();

        var failure = await check.CheckAsync("SELECT 1 FROM t", Principal, "demo", CancellationToken.None);

        failure.ShouldBeNull();
        execution.LastDataSource.ShouldBe("demo");
        execution.LastTenant.ShouldBe(new TenantId("tenant-a"));
    }

    [Fact]
    public async Task MappedError_CopiesCodeTitlePositionSubjectHint()
    {
        var (check, _) = Create(() => new FunctionNotPermittedException("pg_sleep", 1, 8));

        var failure = await check.CheckAsync("SELECT pg_sleep(1) FROM t", Principal, "demo", CancellationToken.None);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe("SQL_FUNCTION_NOT_PERMITTED");
        failure.Message.ShouldNotContain("pg_sleep");   // the fixed title of the code, never the exception text
        failure.Line.ShouldBe(1);
        failure.Column.ShouldBe(8);
        failure.SubjectKind.ShouldBe("function");
        failure.SubjectName.ShouldBe("pg_sleep");
        failure.Hint.ShouldNotBeNull().ShouldContain("pg_sleep");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PolicyDenied_CarriesNoNameOrPosition()
    {
        var (check, _) = Create(() => new Autheris.Application.Sql.WebSqlTableDeniedException("hidden_table"));

        var failure = await check.CheckAsync("SELECT 1 FROM hidden_table", Principal, "demo", CancellationToken.None);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe("SQL_POLICY_DENIED");
        failure.Line.ShouldBeNull();
        failure.Column.ShouldBeNull();
        failure.SubjectKind.ShouldBeNull();
        failure.SubjectName.ShouldBeNull();
        failure.Hint.ShouldBeNull();
        failure.Message.ShouldNotContain("hidden_table");
    }

    [Fact]
    public async Task UnmappedInvalidQuery_UsesClassifierCode()
    {
        var (check, _) = Create(() => new ArgumentException("raw parser text with secret"));

        var failure = await check.CheckAsync("SELECT 1 FROM t", Principal, "demo", CancellationToken.None);

        failure.ShouldNotBeNull();
        failure.Code.ShouldBe("BAD_REQUEST");
        failure.Message.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task UnmappedInternal_Rethrows()
    {
        var (check, _) = Create(() => new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(() => check.CheckAsync("SELECT 1 FROM t", Principal, "demo", CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Timeout_NotReportedValid()
    {
        var (check, _) = Create(() => new TimeoutException());

        await Should.ThrowAsync<TimeoutException>(() => check.CheckAsync("SELECT 1 FROM t", Principal, "demo", CancellationToken.None));
    }

    [Fact]
    public async Task ClientCancellation_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var (check, _) = Create(() => new OperationCanceledException(cts.Token));

        await Should.ThrowAsync<OperationCanceledException>(() => check.CheckAsync("SELECT 1 FROM t", Principal, "demo", cts.Token));
    }
}
