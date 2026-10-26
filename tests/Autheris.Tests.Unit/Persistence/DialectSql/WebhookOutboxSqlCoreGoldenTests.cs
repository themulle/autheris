using System.Data.Common;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Persistence.DialectSql;

/// <summary>ARCH-MNT-22 WP2 L2 runtime golden: the shared webhook outbox core issues the pre-refactor SQL with the pre-refactor binding (incl. divergence D5).</summary>
public sealed class WebhookOutboxSqlCoreGoldenTests
{
    private const string Area = "WebhookOutbox";
    private static readonly DateTimeOffset Now = new(2026, 10, 26, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Later = Now.AddMinutes(7);

    public static TheoryData<string> Dialects() => DialectKit.Names();

    private static WebhookOutboxMessage Message(string? owner = "tenant-a", string? eventTenant = "tenant-b") =>
        new("w1", WebhookSubscriptionSource.CloudEvent, "sub1", owner, eventTenant, "ev1", "type.x", "src", Now, "{\"p\":1}", 0, 8,
            WebhookOutboxState.Pending, Later, null, null, null, null, Now.AddSeconds(-5));

    private static string Resolve(string fragment) => fragment switch
    {
        "{field:WebhookOutboxMapping.ReturningAliasO}" => WebhookOutboxMapping.ReturningAliasO,
        "{field:WebhookOutboxMapping.OutputInserted}" => WebhookOutboxMapping.OutputInserted,
        _ => fragment
    };

    private static string ClaimSql(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.WebhookOutboxSql.ClaimBatch(),
        "SqlServer" => SqlServerGovernanceRepository.WebhookOutboxSql.ClaimBatch(),
        _ => SqliteGovernanceRepository.WebhookOutboxSql.ClaimBatch()
    };

    private static string EnqueueSql(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.WebhookOutboxSql.Enqueue,
        "SqlServer" => SqlServerGovernanceRepository.WebhookOutboxSql.Enqueue,
        _ => SqliteGovernanceRepository.WebhookOutboxSql.Enqueue
    };

    private static string FencedSql(string dialect, string setClause) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.WebhookOutboxSql.FencedUpdate(setClause),
        "SqlServer" => SqlServerGovernanceRepository.WebhookOutboxSql.FencedUpdate(setClause),
        _ => SqliteGovernanceRepository.WebhookOutboxSql.FencedUpdate(setClause)
    };

    private static Action<DbCommand, WebhookFencedValues> Binder(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.BindFencedUpdate,
        "SqlServer" => SqlServerGovernanceRepository.BindFencedUpdate,
        _ => SqliteGovernanceRepository.BindFencedUpdate
    };

    private static string DeferSql(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.WebhookOutboxSql.DeferSubscription,
        "SqlServer" => SqlServerGovernanceRepository.WebhookOutboxSql.DeferSubscription,
        _ => SqliteGovernanceRepository.WebhookOutboxSql.DeferSubscription
    };

    private static string ListSql(string dialect, WebhookOwnerScope scope) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.WebhookOutboxSql.ListDeadLetters(scope),
        "SqlServer" => SqlServerGovernanceRepository.WebhookOutboxSql.ListDeadLetters(scope),
        _ => SqliteGovernanceRepository.WebhookOutboxSql.ListDeadLetters(scope)
    };

    private static string CountSql(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.WebhookOutboxSql.CountPending,
        "SqlServer" => SqlServerGovernanceRepository.WebhookOutboxSql.CountPending,
        _ => SqliteGovernanceRepository.WebhookOutboxSql.CountPending
    };

    private static string PurgeSql(string dialect) => dialect switch
    {
        "PostgreSql" => PostgreSqlGovernanceRepository.WebhookOutboxSql.Purge,
        "SqlServer" => SqlServerGovernanceRepository.WebhookOutboxSql.Purge,
        _ => SqliteGovernanceRepository.WebhookOutboxSql.Purge
    };

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Enqueue_IssuesGoldenSql_WithGoldenBinding(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        var inserted = await WebhookOutboxSqlCore.EnqueueAsync(conn, EnqueueSql(dialect), Message(), CancellationToken.None);

        inserted.ShouldBeTrue();
        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("EnqueueAsync(WebhookOutboxMessage)", "INSERT INTO"));
        command.TransactionSet.ShouldBeFalse();
        kit.ShouldBindExactly(
            command,
            ("@id", "w1"), ("@source", 1), ("@sub", "sub1"), ("@owner", "tenant-a"), ("@evTenant", "tenant-b"), ("@evId", "ev1"),
            ("@evType", "type.x"), ("@evSource", "src"), ("@evTime", WebhookOutboxMapping.Ms(Now)), ("@payload", "{\"p\":1}"),
            ("@attempt", 0), ("@maxAttempts", 8), ("@state", 0), ("@next", WebhookOutboxMapping.Ms(Later)), ("@created", WebhookOutboxMapping.Ms(Now.AddSeconds(-5))));
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Enqueue_ClusterRow_BindsDbNullOwnerAndEventTenant(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        await WebhookOutboxSqlCore.EnqueueAsync(conn, EnqueueSql(dialect), Message(owner: null, eventTenant: null), CancellationToken.None);

        var parameters = conn.Commands.Single().Parameters;
        parameters.Single(p => p.Name == "@owner").Value.ShouldBe(DBNull.Value);
        parameters.Single(p => p.Name == "@evTenant").Value.ShouldBe(DBNull.Value);
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ClaimBatch_IssuesGoldenSql_WithClampedLimitAndGoldenBinding(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var lease = TimeSpan.FromSeconds(30);

        var rows = await WebhookOutboxSqlCore.ClaimBatchAsync(conn, ClaimSql(dialect), 5000, Now, lease, "worker-1", CancellationToken.None);

        rows.ShouldBeEmpty();
        var command = conn.Commands.Single();
        var fragments = kit.Golden(Area).Of("ClaimBatchAsync");
        var start = fragments.ToList().FindIndex(f => f.Contains("picked", StringComparison.Ordinal) || f.Contains("UPDATE WEBHOOK_OUTBOX", StringComparison.Ordinal));
        var end = fragments.ToList().FindIndex(start, f => f == "@now");
        command.CommandText.ShouldBe(string.Concat(fragments.Skip(start).Take(end - start).Select(Resolve)));
        kit.ShouldBindExactly(
            command,
            ("@now", WebhookOutboxMapping.Ms(Now)), ("@leaseUntil", WebhookOutboxMapping.Ms(Now + lease)), ("@owner", "worker-1"), ("@limit", 1000));
    }

    public static TheoryData<string, string> FencedCases()
    {
        var data = new TheoryData<string, string>();
        foreach (var dialect in new[] { "PostgreSql", "SqlServer", "Sqlite" })
        {
            foreach (var kind in new[] { "Complete", "Reschedule", "DeadLetter" })
            {
                data.Add(dialect, kind);
            }
        }

        return data;
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(FencedCases))]
    public async Task FencedUpdate_IssuesGoldenSql_WithDialectSpecificBinding_D5(string dialect, string kind)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var (setClause, status, error, next) = kind switch
        {
            "Complete" => ("state = 2, lease_until_ms = NULL, last_status_code = @status, last_error_code = NULL, updated_ms = @now", (int?)200, (string?)null, (long?)null),
            "Reschedule" => ("state = 0, lease_until_ms = NULL, lease_owner = NULL, next_attempt_ms = @next, last_status_code = @status, last_error_code = @error, updated_ms = @now", 503, "http_5xx", WebhookOutboxMapping.Ms(Later)),
            _ => ("state = 3, lease_until_ms = NULL, last_status_code = @status, last_error_code = @error, updated_ms = @now", (int?)null, "dead", (long?)null)
        };
        var values = new WebhookFencedValues(setClause, status, error, next, Now);

        var updated = await WebhookOutboxSqlCore.FencedUpdateAsync(conn, FencedSql(dialect, setClause), "w1", "worker-1", values, Binder(dialect), CancellationToken.None);

        updated.ShouldBeTrue();
        var command = conn.Commands.Single();
        var fragments = kit.Golden(Area).Of("FencedUpdateAsync");
        command.CommandText.ShouldBe(
            fragments.Single(f => f.StartsWith("UPDATE ", StringComparison.Ordinal)) + setClause + fragments.Single(f => f.StartsWith(" WHERE id = @id", StringComparison.Ordinal)));
        command.TransactionSet.ShouldBeFalse();

        var usesStatus = setClause.Contains("@status", StringComparison.Ordinal);
        var usesError = setClause.Contains("@error", StringComparison.Ordinal);
        var usesNext = setClause.Contains("@next", StringComparison.Ordinal);
        kit.ShouldBindLike(command, reference =>
        {
            kit.Add(reference, "@id", "w1");
            kit.Add(reference, "@owner", "worker-1");
            if (dialect == "Sqlite")
            {
                kit.Add(reference, "@status", (object?)status ?? DBNull.Value);
                kit.Add(reference, "@error", (object?)error ?? DBNull.Value);
                kit.Add(reference, "@next", (object?)next ?? DBNull.Value);
                kit.Add(reference, "@now", WebhookOutboxMapping.Ms(Now));
                return;
            }

            if (usesStatus)
            {
                if (dialect == "PostgreSql")
                {
                    reference.Parameters.Add(new NpgsqlParameter("@status", NpgsqlDbType.Integer) { Value = (object?)status ?? DBNull.Value });
                }
                else
                {
                    kit.Add(reference, "@status", (object?)status ?? DBNull.Value);
                }
            }

            if (usesError)
            {
                kit.Add(reference, "@error", (object?)error ?? DBNull.Value);
            }

            kit.Add(reference, "@now", WebhookOutboxMapping.Ms(Now));
            if (usesNext)
            {
                kit.Add(reference, "@next", next!.Value);
            }
        });
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public void FencedUpdate_KeepsLeaseOwnerFenceInSql(string dialect)
    {
        var sql = FencedSql(dialect, "state = 2");

        sql.ShouldContain("WHERE id = @id AND state = 1 AND lease_owner = @owner;");
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task DeferSubscription_IssuesGoldenSql_WithGoldenBinding(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        var affected = await WebhookOutboxSqlCore.DeferSubscriptionAsync(conn, DeferSql(dialect), WebhookSubscriptionSource.CloudEvent, "sub1", Later, CancellationToken.None);

        affected.ShouldBe(1);
        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("DeferSubscriptionAsync", "SET next_attempt_ms = @until"));
        kit.ShouldBindExactly(command, ("@until", WebhookOutboxMapping.Ms(Later)), ("@source", 1), ("@sub", "sub1"));
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ListDeadLetters_WithTenant_BindsTenantPredicate(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var scope = new WebhookOwnerScope("tenant-a", false);

        await WebhookOutboxSqlCore.ListDeadLettersAsync(conn, ListSql(dialect, scope), scope, 0, CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(ExpectedListSql(kit, "owner_tenant_id = @tenant"));
        command.CommandText.ShouldContain("owner_tenant_id = @tenant");
        kit.ShouldBindExactly(command, ("@tenant", "tenant-a"), ("@limit", 1));
    }

    [Trait("Category", "Security")]
    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task ListDeadLetters_ClusterScope_UsesNullOwnerPredicateAndBindsNoTenant(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        var scope = new WebhookOwnerScope(null, true);

        await WebhookOutboxSqlCore.ListDeadLettersAsync(conn, ListSql(dialect, scope), scope, 50, CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(ExpectedListSql(kit, "owner_tenant_id IS NULL"));
        kit.ShouldBindExactly(command, ("@limit", 50));
    }

    private static string ExpectedListSql(DialectKit kit, string ownerPredicate)
    {
        var f = kit.Golden(Area).Of("ListDeadLettersAsync");
        var select = f.Single(x => x.Contains("SELECT ", StringComparison.Ordinal));
        var columns = f.Single(x => x.StartsWith("id, subscription_id", StringComparison.Ordinal));
        var where = f.Single(x => x.Contains("WHERE state = 3 AND", StringComparison.Ordinal));
        var order = f.Single(x => x.Contains("ORDER BY created_ms", StringComparison.Ordinal));
        return select + columns + where + f.Single(x => x == ownerPredicate) + order;
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task CountPending_IssuesGoldenSql(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();
        conn.ScalarResult = 7;

        var count = await WebhookOutboxSqlCore.CountPendingAsync(conn, CountSql(dialect), CancellationToken.None);

        count.ShouldBe(7L);
        conn.Commands.Single().CommandText.ShouldBe(kit.Golden(Area).Fragment("CountPendingAsync", "SELECT COUNT(*)"));
        conn.Commands.Single().Parameters.ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(Dialects))]
    public async Task Purge_IssuesGoldenSql_WithGoldenBinding(string dialect)
    {
        var kit = DialectKit.Of(dialect);
        using var conn = kit.NewConnection();

        await WebhookOutboxSqlCore.PurgeAsync(conn, PurgeSql(dialect), Now, Later, CancellationToken.None);

        var command = conn.Commands.Single();
        command.CommandText.ShouldBe(kit.Golden(Area).Fragment("PurgeAsync(DateTimeOffset)", "DELETE FROM"));
        kit.ShouldBindExactly(command, ("@delivered", WebhookOutboxMapping.Ms(Now)), ("@dead", WebhookOutboxMapping.Ms(Later)));
    }
}
