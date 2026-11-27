namespace Autheris.Tests.Unit.Sql.ResultCache;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Quota;
using Autheris.Application.Sql;
using Autheris.Application.Sql.ResultCache;
using Autheris.Domain.Audit;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Options;
using Shouldly;
using Xunit;

/// <summary>The result cache in the governed executor, plain (non-paged) requests, over the real pipeline.</summary>
public sealed class GovernedSqlExecutorResultCacheTests
{
    private const string OtherGroup = "S-1-5-21-1000000001-1000000002-1000000003-2001";

    private static async Task<Exception> Fails(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new Xunit.Sdk.XunitException("The request was expected to fail.");
    }

    private static readonly string[] AllRows = ["0:name0", "1:name1", "2:name2", "3:name3", "4:name4"];

    [Fact]
    public async Task Hit_serves_rows_without_connection_and_settles_zero_cost()
    {
        var quota = new ScriptedCostQuota();
        using var pipeline = new GovernedResultCachePipeline(costQuota: quota);

        var first = await pipeline.RunAsync();
        var second = await pipeline.RunAsync();

        first.Rows.ShouldBe(AllRows);
        second.Rows.ShouldBe(AllRows);
        pipeline.Executions.ShouldBe(1);
        pipeline.ConnectionsOpened.ShouldBe(1);
        first.Outcomes.Select(o => o.Status).ShouldBe([WebSqlResultCacheStatus.Miss, WebSqlResultCacheStatus.Store]);
        second.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        second.Outcomes[0].AgeSeconds.ShouldNotBeNull();

        var settled = quota.Settled;
        settled.Count.ShouldBe(2);
        settled[1].ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hit_writes_allow_audit_before_first_row_SECRC16()
    {
        using var pipeline = new GovernedResultCachePipeline();

        await pipeline.RunAsync();
        pipeline.Events.Clear();
        pipeline.AuditEntries.Clear();
        await pipeline.RunAsync();

        pipeline.Events.ShouldBe(["audit", "row"]);
        var entry = pipeline.AuditEntries.ShouldHaveSingleItem();
        entry.EventType.ShouldBe(AuditEventTypes.WebSqlQuery);
        entry.Decision.ShouldBe("ALLOW");
        entry.DetailsJson.ShouldContain("\"resultCache\":\"hit\"");
        entry.DetailsJson.ShouldContain("resultCacheAgeSeconds");
        entry.DetailsJson.ShouldContain("resultCacheStoredTraceId");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Failing_audit_on_hit_serves_no_rows_SECRC16()
    {
        using var pipeline = new GovernedResultCachePipeline();
        await pipeline.RunAsync();
        pipeline.FailAudit = true;
        var served = 0;

        var failure = await Fails(() => pipeline.RunAsync(writer: async (reader, token) =>
        {
            while (await reader.ReadAsync(token))
            {
                served++;
            }
        }));

        failure.ShouldBeOfType<AuditUnavailableException>();

        served.ShouldBe(0);
        pipeline.Executions.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Rewrite_denial_never_reads_cache_SECRC01()
    {
        using var pipeline = new GovernedResultCachePipeline();
        await pipeline.RunAsync();
        pipeline.Spy.Lookups.ShouldBe(1);

        await Fails(() => pipeline.RunAsync(sql: "SELECT xp_cmdshell('whoami') AS c FROM crm.customers ORDER BY id"));

        pipeline.Spy.Lookups.ShouldBe(1);
        pipeline.AuditEntries.ShouldContain(e => e.EventType == AuditEventTypes.WebSqlQueryDenied);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Max_megabytes_zero_disables_everything_and_clears_SECRC08()
    {
        using var pipeline = new GovernedResultCachePipeline();
        await pipeline.RunAsync();
        pipeline.Cache.EntryCount.ShouldBe(1);

        pipeline.Monitor.SetCurrent(new GatewayOptions { WebSql = new WebSqlOptions { ResultCacheMaxMegabytes = 0 } });

        // The reload alone empties the store; no request has to take part for that.
        pipeline.Cache.EntryCount.ShouldBe(0);
        var run = await pipeline.RunAsync(participate: false);

        run.Outcomes.ShouldBeEmpty();
        run.Rows.ShouldBe(AllRows);
        pipeline.Executions.ShouldBe(2);
        pipeline.Spy.Lookups.ShouldBe(1);
        pipeline.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Hit_is_refused_like_a_miss_when_the_read_lease_limit_is_exhausted_SECRC16()
    {
        var gate = new SwitchableReadGate();
        using var pipeline = new GovernedResultCachePipeline(gate: gate);
        await pipeline.RunAsync();
        gate.Active.ShouldBe(0);

        gate.Deny = true;
        var failure = await Fails(() => pipeline.RunAsync());

        failure.ShouldBeOfType<Autheris.Domain.Exceptions.GatewayThrottledException>();
        pipeline.Executions.ShouldBe(1);
        gate.Deny = false;
        var served = await pipeline.RunAsync();
        served.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        gate.Active.ShouldBe(0);
    }

    [Fact]
    public async Task Failing_writer_on_a_hit_still_settles_zero_cost()
    {
        var quota = new ScriptedCostQuota();
        using var pipeline = new GovernedResultCachePipeline(costQuota: quota);
        await pipeline.RunAsync();

        await Fails(() => pipeline.RunAsync(writer: (_, _) => throw new InvalidOperationException("client gone")));

        quota.Settled[^1].ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Windows_auth_settings_are_part_of_the_key_SECRC02()
    {
        using var pipeline = new GovernedResultCachePipeline();
        await pipeline.RunAsync();
        var connection = pipeline.Options.DataSources!.Connections["crm"];
        pipeline.Options.DataSources.Connections["crm"] = new DataSourceConnectionOptions
        {
            Provider = connection.Provider,
            ConnectionString = connection.ConnectionString,
            ResultCacheTtlSeconds = connection.ResultCacheTtlSeconds,
            WindowsAuth = new SqlServerWindowsAuthOptions { Domain = "CORP", Username = "svc-other" },
        };

        await pipeline.RunAsync();

        pipeline.Spy.Hashes.Distinct().Count().ShouldBe(2);
        pipeline.Executions.ShouldBe(2);
    }

    [Fact]
    public async Task Request_without_participation_never_touches_the_cache()
    {
        using var pipeline = new GovernedResultCachePipeline();

        var first = await pipeline.RunAsync(participate: false);
        var second = await pipeline.RunAsync(participate: false);

        first.Outcomes.ShouldBeEmpty();
        second.Outcomes.ShouldBeEmpty();
        pipeline.Executions.ShouldBe(2);
        pipeline.Spy.Lookups.ShouldBe(0);
        pipeline.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Source_without_ttl_never_touches_the_cache()
    {
        using var pipeline = new GovernedResultCachePipeline(sourceTtl: 0);

        var run = await pipeline.RunAsync();
        await pipeline.RunAsync();

        run.Outcomes.ShouldBeEmpty();
        pipeline.Executions.ShouldBe(2);
        pipeline.Spy.Lookups.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Different_user_never_hits_other_users_entry_SECRC19()
    {
        using var pipeline = new GovernedResultCachePipeline();

        await pipeline.RunAsync(GovernedResultCachePipeline.User("user-1"));
        var other = await pipeline.RunAsync(GovernedResultCachePipeline.User("user-2"));
        var otherAgain = await pipeline.RunAsync(GovernedResultCachePipeline.User("user-2"));

        other.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        otherAgain.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        pipeline.Executions.ShouldBe(2);
        pipeline.Spy.Hashes.Distinct().Count().ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Same_sid_with_different_groups_misses()
    {
        using var pipeline = new GovernedResultCachePipeline();

        await pipeline.RunAsync(GovernedResultCachePipeline.User("user-1"));
        var grouped = await pipeline.RunAsync(GovernedResultCachePipeline.User("user-1", OtherGroup));

        grouped.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        pipeline.Executions.ShouldBe(2);
    }

    [Fact]
    public async Task Volatile_function_is_ineligible_and_never_stored()
    {
        using var pipeline = new GovernedResultCachePipeline();
        const string Volatile = "SELECT id, now() AS name FROM crm.customers ORDER BY id";

        var first = await pipeline.RunAsync(sql: Volatile);
        var second = await pipeline.RunAsync(sql: Volatile);

        first.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Ineligible);
        first.Outcomes[0].Reason.ShouldBe(WebSqlResultCacheReasons.VolatileFunction);
        second.Outcomes[0].Reason.ShouldBe(WebSqlResultCacheReasons.VolatileFunction);
        pipeline.Executions.ShouldBe(2);
        pipeline.Spy.Lookups.ShouldBe(0);
        pipeline.Cache.EntryCount.ShouldBe(0);
        pipeline.AuditEntries.Last().DetailsJson.ShouldContain("\"resultCache\":\"ineligible\"");
    }

    [Fact]
    public async Task Identical_requests_build_the_identical_key_and_other_sql_another()
    {
        using var pipeline = new GovernedResultCachePipeline();

        await pipeline.RunAsync();
        await pipeline.RunAsync();
        await pipeline.RunAsync(sql: "SELECT name, id FROM crm.customers ORDER BY id");

        pipeline.Spy.Hashes.Count.ShouldBe(3);
        pipeline.Spy.Hashes[0].ShouldBe(pipeline.Spy.Hashes[1]);
        pipeline.Spy.Hashes[2].ShouldNotBe(pipeline.Spy.Hashes[0]);
        pipeline.Spy.Hashes[0].ShouldMatch("^[0-9a-f]{64}$");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Null_and_value_optional_filter_do_not_share_an_entry()
    {
        const string optionalSql = "SELECT id, name FROM crm.customers WHERE (@name IS NULL OR name = @name) ORDER BY id";
        using var pipeline = new GovernedResultCachePipeline();
        var nullName = new Dictionary<string, object?> { ["name"] = null };
        var valueName = new Dictionary<string, object?> { ["name"] = "name1" };

        var first = await pipeline.RunAsync(sql: optionalSql, parameters: nullName);
        var second = await pipeline.RunAsync(sql: optionalSql, parameters: valueName);
        var third = await pipeline.RunAsync(sql: optionalSql, parameters: nullName);

        first.Outcomes.Select(o => o.Status).ShouldBe([WebSqlResultCacheStatus.Miss, WebSqlResultCacheStatus.Store]);
        second.Outcomes.Select(o => o.Status).ShouldBe([WebSqlResultCacheStatus.Miss, WebSqlResultCacheStatus.Store]);
        third.Outcomes.ShouldHaveSingleItem().Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        pipeline.Spy.Hashes[0].ShouldNotBe(pipeline.Spy.Hashes[1]);
        pipeline.Spy.Hashes[0].ShouldBe(pipeline.Spy.Hashes[2]);
    }

    [Fact]
    public async Task Governance_change_after_store_gives_a_miss()
    {
        using var pipeline = new GovernedResultCachePipeline();
        await pipeline.RunAsync();

        pipeline.Store.Epoch++;
        pipeline.Governance.InvalidateTenant(GovernedResultCachePipeline.Tenant.Value);
        var run = await pipeline.RunAsync();

        run.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        pipeline.Executions.ShouldBe(2);
    }

    [Fact]
    public async Task Without_epoch_services_nothing_is_ever_served()
    {
        using var pipeline = new GovernedResultCachePipeline(withEpochServices: false);

        await pipeline.RunAsync();
        var second = await pipeline.RunAsync();

        second.Outcomes.ShouldNotContain(o => o.Status == WebSqlResultCacheStatus.Hit);
        pipeline.Executions.ShouldBe(2);
        pipeline.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Bypass_reexecutes_and_refreshes_the_entry()
    {
        using var pipeline = new GovernedResultCachePipeline();
        await pipeline.RunAsync();
        pipeline.Source = () => ResultCacheFixtures.Rows(3);

        var cached = await pipeline.RunAsync();
        var bypass = await pipeline.RunAsync(mode: WebSqlResultCacheMode.Bypass);
        var refreshed = await pipeline.RunAsync();

        cached.Rows.ShouldBe(AllRows);
        bypass.Rows.ShouldBe(["0:name0", "1:name1", "2:name2"]);
        bypass.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Bypass);
        refreshed.Rows.ShouldBe(bypass.Rows);
        refreshed.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        pipeline.Executions.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Bypass_failure_keeps_the_previous_entry_SECRC12()
    {
        using var pipeline = new GovernedResultCachePipeline();
        await pipeline.RunAsync();
        pipeline.Source = () => throw new TimeoutException("source down");

        await Fails(() => pipeline.RunAsync(mode: WebSqlResultCacheMode.Bypass));
        pipeline.Source = () => ResultCacheFixtures.Rows(1);
        var after = await pipeline.RunAsync();

        after.Rows.ShouldBe(AllRows);
        after.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Hit);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Parquet_early_stop_not_stored_SECRC11()
    {
        using var pipeline = new GovernedResultCachePipeline();
        var read = 0;

        var first = await pipeline.RunAsync(format: "parquet", writer: async (reader, token) =>
        {
            while (read < 2 && await reader.ReadAsync(token))
            {
                read++;
            }
        });
        var second = await pipeline.RunAsync(format: "parquet");

        first.Outcomes.Last().Status.ShouldBe(WebSqlResultCacheStatus.Miss);
        first.Outcomes.Last().Reason.ShouldBe(WebSqlResultCacheReasons.Incomplete);
        second.Outcomes.Select(o => o.Status).ShouldNotContain(WebSqlResultCacheStatus.Hit);
        pipeline.Executions.ShouldBe(2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Timeout_exception_cancel_not_stored_SECRC11()
    {
        using var pipeline = new GovernedResultCachePipeline();

        pipeline.Source = () => throw new TimeoutException("slow");
        await Fails(() => pipeline.RunAsync());
        pipeline.Cache.EntryCount.ShouldBe(0);

        pipeline.Source = () => ResultCacheFixtures.Rows(5);
        await Fails(() => pipeline.RunAsync(writer: async (reader, token) =>
        {
            await reader.ReadAsync(token);
            throw new InvalidOperationException("writer failed");
        }));
        pipeline.Cache.EntryCount.ShouldBe(0);

        using var cts = new CancellationTokenSource();
        await Fails(() => pipeline.RunAsync(ct: cts.Token, writer: async (reader, token) =>
        {
            await reader.ReadAsync(token);
            await cts.CancelAsync();
            token.ThrowIfCancellationRequested();
        }));
        pipeline.Cache.EntryCount.ShouldBe(0);
    }

    [Fact]
    public async Task Row_limit_truncation_is_stored_and_replayed_as_truncated()
    {
        using var pipeline = new GovernedResultCachePipeline(defaultMaxRows: 3);
        var truncated = new List<bool>();

        async Task Write(DbDataReader reader, CancellationToken token)
        {
            while (await reader.ReadAsync(token))
            {
            }

            truncated.Add(reader is IRowLimitProbe { HasMoreRows: true });
        }

        await pipeline.RunAsync(writer: Write);
        var hit = await pipeline.RunAsync(writer: Write);

        truncated.ShouldBe([true, true]);
        hit.Outcomes[0].Status.ShouldBe(WebSqlResultCacheStatus.Hit);
        pipeline.Executions.ShouldBe(1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Metric_labels_and_logs_contain_no_sql_sid_key_SECRC19_20()
    {
        var tags = new List<(string Name, string Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter == GatewayDiagnostics.Meter && instrument.Name.StartsWith("autheris_websql_result_cache", StringComparison.Ordinal))
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, measurementTags, _) =>
        {
            lock (tags)
            {
                foreach (var tag in measurementTags)
                {
                    tags.Add((tag.Key, Convert.ToString(tag.Value) ?? string.Empty));
                }
            }
        });
        listener.Start();
        var logger = new CapturedLogger();
        using var pipeline = new GovernedResultCachePipeline(logger: logger);

        await pipeline.RunAsync();
        await pipeline.RunAsync();
        await pipeline.RunAsync(GovernedResultCachePipeline.User("secret-sid-42"));
        await pipeline.RunAsync(sql: "SELECT id, now() AS name FROM crm.customers ORDER BY id");
        listener.RecordObservableInstruments();

        string[] outcomes = ["hit", "miss", "store", "bypass", "ineligible"];
        var reasons = typeof(WebSqlResultCacheReasons).GetFields().Concat(typeof(WebSqlResultCacheInvalidationReasons).GetFields())
            .Select(f => (string)f.GetRawConstantValue()!).Append(string.Empty).ToHashSet();
        lock (tags)
        {
            tags.ShouldNotBeEmpty();
            foreach (var (name, value) in tags.Where(t => t.Name is "source" or "outcome" or "reason"))
            {
                value.ShouldNotMatch("^[0-9a-f]{64}$");
                value.ShouldNotContain("SELECT", Case.Insensitive);
                value.ShouldNotContain("secret-sid-42");
                if (name == "outcome")
                {
                    outcomes.ShouldContain(value);
                }

                if (name == "reason")
                {
                    reasons.ShouldContain(value);
                }
            }

            tags.Select(t => t.Name).Distinct().ShouldBeSubsetOf(["source", "outcome", "reason"]);
        }

        lock (logger.Messages)
        {
            foreach (var message in logger.Messages.Where(m => m.Contains("result cache", StringComparison.OrdinalIgnoreCase)))
            {
                message.ShouldNotContain("secret-sid-42");
                message.ShouldNotContain("customers");
                Regex.IsMatch(message, "[0-9a-f]{64}").ShouldBeFalse();
            }
        }
    }
}
