namespace Autheris.Tests.Unit.Sql;

using Autheris.Application.Audit;
using TrinoSqlEngine.Ast.Emit;
using Autheris.Domain.Audit;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-36-SP2-03, SEC-36-SP2-04, SEC-36-SP2-05: the DML intent record is required and written before any connection or transaction.</summary>
[Trait("Category", "Security")]
public sealed class DmlWriteAheadAuditTests
{
    private const string Update = "UPDATE orders SET amount = 1 WHERE id = 1";

    [Fact]
    public async Task Intent_row_is_written_before_transaction_begins()
    {
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("Sqlite", Update);
        GovernedSqlExecutorX1Tests.Harness? h = null;
        var connectionsAtIntent = -1;
        var transactionsAtIntent = -1;
        h = GovernedSqlExecutorX1Tests.CreateHarness("Sqlite", auditSink: (e, _) =>
        {
            if (e.EventType == AuditEventTypes.WebSqlDmlIntent)
            {
                connectionsAtIntent = h!.Factory.ReceivedCalls().Count();
                transactionsAtIntent = h.Connection.Transactions.Count;
            }

            return Task.CompletedTask;
        });

        await GovernedSqlExecutorX1Tests.ExecuteAsync(h, compiled, dml: GovernedSqlExecutorX1Tests.Dml());

        connectionsAtIntent.ShouldBe(0);
        transactionsAtIntent.ShouldBe(0);
        h.Audit.Select(e => e.EventType).ShouldBe([AuditEventTypes.WebSqlDmlIntent, AuditEventTypes.WebSqlDmlExecuted]);
    }

    [Fact]
    public async Task Dml_is_not_executed_when_intent_audit_fails()
    {
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("Sqlite", Update);
        var h = GovernedSqlExecutorX1Tests.CreateHarness("Sqlite", auditSink: (_, _) => throw new InvalidOperationException("audit store down"));

        await Should.ThrowAsync<AuditUnavailableException>(() => GovernedSqlExecutorX1Tests.ExecuteAsync(h, compiled, dml: GovernedSqlExecutorX1Tests.Dml()));

        h.Factory.ReceivedCalls().ShouldBeEmpty();
        h.Connection.Transactions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Completion_audit_failure_does_not_alter_response_and_increments_counter()
    {
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("Sqlite", Update);
        var h = GovernedSqlExecutorX1Tests.CreateHarness("Sqlite", auditSink: (e, _) =>
            e.EventType == AuditEventTypes.WebSqlDmlExecuted ? throw new InvalidOperationException("audit store down") : Task.CompletedTask);
        long failures = 0;
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Name == "autheris_dml_audit_failed_total")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref failures, value));
        listener.Start();

        var sql = await GovernedSqlExecutorX1Tests.ExecuteAsync(h, compiled, dml: GovernedSqlExecutorX1Tests.Dml());

        sql.ShouldBe(compiled.Sql);
        h.Connection.Transactions.ShouldHaveSingleItem().Commits.ShouldBe(1);
        Interlocked.Read(ref failures).ShouldBe(1);
    }

    [Fact]
    public async Task Dml_audit_failure_after_commit_is_logged_critical()
    {
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("Sqlite", Update);
        var h = GovernedSqlExecutorX1Tests.CreateHarness("Sqlite", auditSink: (e, _) =>
            e.EventType == AuditEventTypes.WebSqlDmlExecuted ? throw new InvalidOperationException("audit store down") : Task.CompletedTask);

        await GovernedSqlExecutorX1Tests.ExecuteAsync(h, compiled, dml: GovernedSqlExecutorX1Tests.Dml());

        h.Log.Entries.ShouldContain(e => e.Level == Microsoft.Extensions.Logging.LogLevel.Critical);
    }

    [Fact]
    public async Task Intent_row_contains_no_literal_values()
    {
        const string literal = "zz-literal-77";
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("Sqlite", $"UPDATE orders SET amount = '{literal}' WHERE id = 1");
        var h = GovernedSqlExecutorX1Tests.CreateHarness("Sqlite");

        await GovernedSqlExecutorX1Tests.ExecuteAsync(h, compiled, dml: GovernedSqlExecutorX1Tests.Dml());

        var intent = h.Audit.Single(e => e.EventType == AuditEventTypes.WebSqlDmlIntent);
        intent.DetailsJson.ShouldNotContain(literal);
        intent.DetailsJson.ShouldContain("sql.compiled_digest");
    }

    [Fact]
    public async Task Audit_failure_in_failure_path_does_not_mask_statement_error()
    {
        var compiled = await GovernedSqlExecutorX1Tests.CompileAsync("PostgreSQL", "UPDATE orders SET amount = 1 WHERE id > 0");
        var h = GovernedSqlExecutorX1Tests.CreateHarness("PostgreSql", () => new FakeDbCommand { NonQueryResult = 5 }, sessionTransaction: true,
            options: GovernedSqlExecutorX1Tests.Gateway("PostgreSql", maxAffectedRows: 2),
            auditSink: (e, _) => e.EventType == AuditEventTypes.WebSqlDmlRejected ? throw new InvalidOperationException("audit store down") : Task.CompletedTask);

        await Should.ThrowAsync<DmlAffectedRowsLimitException>(() => GovernedSqlExecutorX1Tests.ExecuteAsync(h, compiled, dml: GovernedSqlExecutorX1Tests.Dml()));

        h.Connection.Transactions.ShouldHaveSingleItem().Rollbacks.ShouldBe(1);
    }
}
