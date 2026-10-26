using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>ARCH-MNT-22 WP1 L2 runtime golden: the shared ITSM outbox core issues the pre-refactor SQL with the pre-refactor binding.</summary>
public sealed class ItsmOutboxSqlCoreGoldenTests
{
    private const string Area = "ItsmOutbox";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 30, 0, TimeSpan.Zero);

    public static TheoryData<string> Dialects() => DialectKit.Names();

    private static string Sql(string dialect, string method) => (dialect, method) switch
    {
        ("PostgreSql", "Insert") => PostgreSqlGovernanceRepository.ItsmOutboxSql.Insert,
        ("PostgreSql", "GetPending") => PostgreSqlGovernanceRepository.ItsmOutboxSql.GetPending,
        ("PostgreSql", "MarkCompleted") => PostgreSqlGovernanceRepository.ItsmOutboxSql.MarkCompleted,
        ("PostgreSql", "MarkFailed") => PostgreSqlGovernanceRepository.ItsmOutboxSql.MarkFailed,
        ("PostgreSql", "GetDeadLetters") => PostgreSqlGovernanceRepository.ItsmOutboxSql.GetDeadLetters,
        ("SqlServer", "Insert") => SqlServerGovernanceRepository.ItsmOutboxSql.Insert,
        ("SqlServer", "GetPending") => SqlServerGovernanceRepository.ItsmOutboxSql.GetPending,
        ("SqlServer", "MarkCompleted") => SqlServerGovernanceRepository.ItsmOutboxSql.MarkCompleted,
        ("SqlServer", "MarkFailed") => SqlServerGovernanceRepository.ItsmOutboxSql.MarkFailed,
        ("SqlServer", "GetDeadLetters") => SqlServerGovernanceRepository.ItsmOutboxSql.GetDeadLetters,
        ("Sqlite", "Insert") => SqliteGovernanceRepository.ItsmOutboxSql.Insert,
        ("Sqlite", "GetPending") => SqliteGovernanceRepository.ItsmOutboxSql.GetPending,
        ("Sqlite", "MarkCompleted") => SqliteGovernanceRepository.ItsmOutboxSql.MarkCompleted,
        ("Sqlite", "MarkFailed") => SqliteGovernanceRepository.ItsmOutboxSql.MarkFailed,
        ("Sqlite", "GetDeadLetters") => SqliteGovernanceRepository.ItsmOutboxSql.GetDeadLetters,
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    private static ItsmOutboxMessage Message(DateTimeOffset? next) =>
        new("m1", "req1", "tenant-a", "CONSENT_REQUEST", "{\"k\":1}", "ServiceNow", ItsmOutboxStatus.Pending, 2, 5, Now, next, "boom");

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Enqueue_IssuesGoldenSql_WithGoldenBinding(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var message = Message(Now.AddMinutes(5));

        await ItsmOutboxSqlCore.EnqueueAsync(conn, Sql(dialect, "Insert"), message, CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("EnqueueAsync(ItsmOutboxMessage)", "INSERT INTO ITSM_OUTBOX"));
        command.TransactionSet.ShouldBeFalse();
        kit.ShouldBindExactly(
            command,
            ("@id", "m1"), ("@requestId", "req1"), ("@tenantId", "tenant-a"), ("@eventType", "CONSENT_REQUEST"), ("@payloadJson", "{\"k\":1}"),
            ("@preferredSystem", "ServiceNow"), ("@status", 0), ("@retryCount", 2), ("@maxRetries", 5),
            ("@createdAt", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("@nextRetryAt", Now.AddMinutes(5).ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("@lastError", "boom"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Enqueue_WithoutNextRetryAndError_BindsDbNull(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var message = Message(null) with { LastError = null };

        await ItsmOutboxSqlCore.EnqueueAsync(conn, Sql(dialect, "Insert"), message, CancellationToken.None);

        var parameters = conn.Commands.Single().Parameters;
        parameters.Single(p => p.Name == "@nextRetryAt").Value.ShouldBe(DBNull.Value);
        parameters.Single(p => p.Name == "@lastError").Value.ShouldBe(DBNull.Value);
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetPending_IssuesGoldenSql_WithClampedBatchAndClockNow(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        var rows = await ItsmOutboxSqlCore.GetPendingAsync(conn, Sql(dialect, "GetPending"), 0, new FakeTimeProvider(Now), CancellationToken.None);

        rows.ShouldBeEmpty();
        var command = conn.Commands.Single();
        command.Kind.ShouldBe("Reader");
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("GetPendingMessagesAsync", "status IN (0, 1)"));
        kit.ShouldBindExactly(command, ("@now", Now.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("@batchSize", 1));
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task MarkCompleted_IssuesGoldenSql_WithGoldenBinding(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        await ItsmOutboxSqlCore.MarkCompletedAsync(conn, Sql(dialect, "MarkCompleted"), "m1", CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("MarkCompletedAsync", "last_error = NULL"));
        kit.ShouldBindExactly(command, ("@status", 2), ("@id", "m1"));
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("PostgreSql", false)]
    [InlineData("PostgreSql", true)]
    [InlineData("SqlServer", false)]
    [InlineData("SqlServer", true)]
    [InlineData("Sqlite", false)]
    [InlineData("Sqlite", true)]
    public async Task MarkFailed_IssuesGoldenSql_WithGoldenBinding(string dialect, bool deadLetter)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var delay = TimeSpan.FromMinutes(3);

        await ItsmOutboxSqlCore.MarkFailedAsync(conn, Sql(dialect, "MarkFailed"), "m1", "err", delay, deadLetter, new FakeTimeProvider(Now), CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("MarkFailedAsync", "retry_count = retry_count + 1"));
        kit.ShouldBindExactly(
            command,
            ("@status", deadLetter ? (int)ItsmOutboxStatus.DeadLetter : (int)ItsmOutboxStatus.Pending),
            ("@nextRetryAt", deadLetter ? DBNull.Value : Now.Add(delay).ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("@lastError", "err"),
            ("@id", "m1"));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task GetDeadLetters_IssuesGoldenSql_WithClampedLimit(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        await ItsmOutboxSqlCore.GetDeadLettersAsync(conn, Sql(dialect, "GetDeadLetters"), -5, CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("GetDeadLetterMessagesAsync", "WHERE status = @status"));
        kit.ShouldBindExactly(command, ("@status", (int)ItsmOutboxStatus.DeadLetter), ("@limit", 1));
    }

    [Fact]
    public async Task ReadMessage_MapsAllColumns()
    {
        using var conn = DialectKit.Sqlite.NewConnection();
        var table = new System.Data.DataTable();
        foreach (var (name, type) in new (string, Type)[]
                 {
                     ("id", typeof(string)), ("request_id", typeof(string)), ("tenant_id", typeof(string)), ("event_type", typeof(string)),
                     ("payload_json", typeof(string)), ("preferred_system", typeof(string)), ("status", typeof(int)), ("retry_count", typeof(int)),
                     ("max_retries", typeof(int)), ("created_at", typeof(string)), ("next_retry_at", typeof(string)), ("last_error", typeof(string))
                 })
        {
            table.Columns.Add(name, type);
        }

        table.Rows.Add("m1", "req1", "tenant-a", "E", "{}", "SN", 1, 2, 5, Now.ToString("O"), DBNull.Value, DBNull.Value);
        conn.EnqueueReader(table);

        var rows = await ItsmOutboxSqlCore.GetDeadLettersAsync(conn, "SELECT 1", 10, CancellationToken.None);

        var row = rows.Single();
        row.Id.ShouldBe("m1");
        row.Status.ShouldBe((ItsmOutboxStatus)1);
        row.CreatedAt.ShouldBe(Now);
        row.NextRetryAt.ShouldBeNull();
        row.LastError.ShouldBeNull();
    }
}
