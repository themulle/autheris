namespace Autheris.Tests.Unit.Olap;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Connectors;
using Autheris.Application.Connectors.CrossDomain;
using Autheris.Application.Federation;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// PLAN-SEC-OLAP-01: federation gate, classification, ordering (authorize all before any read), guardrails, budgets and audit of the
/// OLAP staging service. Runs the real DuckDB engine and the real gateway SQL engine over recording connector fakes.
/// </summary>
public sealed partial class OlapFederationGateTests
{
    private const string GateDenied = "Cross-source queries are not enabled for this endpoint.";

    private sealed record TableDef(
        string Domain,
        string Table,
        string ConnectorId = "sql-a",
        string SourceName = "",
        DataSourceType Type = DataSourceType.Sql,
        bool Active = true,
        bool Deny = false,
        IReadOnlyDictionary<string, ColumnAccessLevel>? ColumnAccess = null,
        IReadOnlyList<TableColumn>? Columns = null,
        IReadOnlyDictionary<string, MaskingRule>? Rules = null,
        int Rows = 2);

    private sealed class Harness
    {
        public OlapStagingService Sut { get; set; } = null!;
        public IAuditWriter Audit { get; } = Substitute.For<IAuditWriter>();
        public List<string> Reads { get; } = [];
        public List<AuditLogEntry> Records { get; } = [];
        public int MetadataCalls { get; set; }
    }

    private static readonly IReadOnlyList<TableColumn> DefaultColumns =
        [new TableColumn { ColumnName = "id", DataType = "int" }, new TableColumn { ColumnName = "name", DataType = "string" }];

    private static Harness Build(
        IReadOnlyList<TableDef> defs,
        CrossSourceOptions? cross = null,
        DuckDbOlapOptions? olap = null,
        Microsoft.Extensions.Logging.ILogger<OlapStagingService>? logger = null)
    {
        var domains = defs.Select(d => d.Domain).Distinct().ToList();
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { AllowedDataSources = domains, CrossSource = cross ?? new CrossSourceOptions() },
            DuckDbOlap = olap ?? new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", MaxThreads = 1, QueryTimeoutSeconds = 20 }
        });

        var metas = defs.ToDictionary(
            d => new TableIdentifier(d.Domain, "public", d.Table),
            d => new TableMetadata
            {
                Identifier = new TableIdentifier(d.Domain, "public", d.Table),
                Table = new Table { SourceName = d.SourceName, DataSourceType = d.Type, IsActive = d.Active },
                Columns = d.Columns ?? DefaultColumns,
                ColumnMaskingRules = d.Rules ?? new Dictionary<string, MaskingRule>()
            });

        var harness = new Harness();

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            harness.MetadataCalls++;
            return metas.TryGetValue(ci.Arg<TableIdentifier>(), out var m) ? m : null;
        });
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(_ => (IReadOnlyList<TableMetadata>)metas.Values.ToList());

        var resolver = Substitute.For<ICrossDomainAccessResolver>();
        resolver.ResolveAccessAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<TableIdentifier>(), Arg.Any<TableMetadata>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<TableIdentifier>();
                var def = defs.First(d => d.Domain == id.Domain && d.Table == id.TableName);
                return def.Deny
                    ? TableAccessDecision.Denied(id, "no grant")
                    : TableAccessDecision.Allowed(id, def.ColumnAccess ?? new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
            });

        var registry = Substitute.For<IAutherisConnectorRegistry>();
        registry.TryGetConnectorForTable(Arg.Any<TableIdentifier>(), out Arg.Any<IAutherisConnector>()!).Returns(ci =>
        {
            var id = ci.Arg<TableIdentifier>();
            var def = defs.First(d => d.Domain == id.Domain && d.Table == id.TableName);
            var connector = Substitute.For<IAutherisConnector>();
            connector.ConnectorId.Returns(def.ConnectorId);
            var splits = Substitute.For<IConnectorSplitManager>();
            var source = Substitute.For<IConnectorRecordSource>();
            connector.SplitManager.Returns(splits);
            connector.RecordSource.Returns(source);
            splits.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    harness.Reads.Add(id.ToString());
                    return (IReadOnlyList<ConnectorSplit>)new[] { ConnectorSplit.Default() };
                });
            source.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
                .Returns(_ => (IReadOnlyList<IReadOnlyDictionary<string, object?>>)Enumerable.Range(1, def.Rows)
                    .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i, ["name"] = "n" + i })
                    .ToList());
            ci[1] = connector;
            return true;
        });

        var masking = Substitute.For<IColumnMaskingProvider>();
        masking.MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>()).Returns("***");

        harness.Audit.RecordRequiredAsync(Arg.Do<AuditLogEntry>(harness.Records.Add), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        harness.Sut = new OlapStagingService(
            new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance),
            repo, registry, resolver, masking, options, harness.Audit, logger ?? NullLogger<OlapStagingService>.Instance,
            new FederationGate(options), FastSqlEngine.Default, new OlapRequestLimiter(options));

        return harness;
    }

    private static CrossSourceOptions Open(params string[] transports) => new() { Enabled = true, AllowedTransports = [.. transports] };

    private static SecurityPrincipalContext Caller() => new()
    {
        UserSid = new Sid("S-1-5-21-USER"),
        TenantId = new TenantId("tenant-1"),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer"
    };

    private static ClaimsPrincipal User() => new(new ClaimsIdentity([new Claim("user_sid", "S-1-5-21-USER")], "Bearer"));

    private static Task<OlapStagingOutcome> Run(Harness h, string sql, params string[] tables) =>
        h.Sut.ExecuteAsync(User(), Caller(), new OlapQueryCommand(sql, tables), isDevelopment: false, "trace-1", CancellationToken.None);

    private const string JoinSql = "SELECT a.id, b.name FROM orders a JOIN customers b ON a.id = b.id";

    private static readonly TableDef[] TwoSources =
    [
        new("sales", "orders", ConnectorId: "sql-a"),
        new("crm", "customers", ConnectorId: "sql-b")
    ];

    // ---- gate ----

    [Fact]
    public async Task CrossSourceOlap_TwoSources_FederationDisabled_Returns403_AndNoConnectorIsRead()
    {
        var h = Build(TwoSources);

        var outcome = await Run(h, JoinSql, "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(403);
        outcome.Error.ShouldBe(GateDenied);
        outcome.Result.ShouldBeNull();
        h.Reads.ShouldBeEmpty();
        h.Records.ShouldNotContain(r => r.EventType == "OLAP_TABLE_STAGED");
    }

    [Fact]
    public async Task CrossSourceOlap_FederationEnabled_OlapNotInAllowedTransports_Returns403()
    {
        var h = Build(TwoSources, new CrossSourceOptions { Enabled = true });

        var outcome = await Run(h, JoinSql, "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(403);
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrossSourceOlap_EnabledAndOlapAllowed_Succeeds()
    {
        var h = Build(TwoSources, Open("Olap"));

        var outcome = await Run(h, JoinSql, "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(200);
        outcome.Result!.Rows.Count.ShouldBe(2);
        var query = h.Records.Single(r => r.EventType == "OLAP_QUERY");
        using var doc = JsonDocument.Parse(query.DetailsJson!);
        doc.RootElement.GetProperty("crossSource").GetBoolean().ShouldBeTrue();
        doc.RootElement.GetProperty("transport").GetString().ShouldBe("Olap");
        doc.RootElement.GetProperty("traceId").GetString().ShouldBe("trace-1");
        query.DetailsJson.ShouldNotContain("JOIN");
    }

    [Fact]
    public async Task CrossSourceOlap_EmptyAllowedTransports_Returns403()
    {
        var h = Build(TwoSources, Open());

        (await Run(h, JoinSql, "sales.public.orders", "crm.public.customers")).StatusCode.ShouldBe(403);
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrossSourceOlap_TransportMatchIsCaseInsensitive()
    {
        var h = Build(TwoSources, Open("olap"));

        (await Run(h, JoinSql, "sales.public.orders", "crm.public.customers")).StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task SingleSourceOlap_TwoTablesSameSource_FederationDisabled_StillSucceeds()
    {
        var h = Build([new TableDef("sales", "orders"), new TableDef("sales", "customers")]);

        var outcome = await Run(h, JoinSql, "sales.public.orders", "sales.public.customers");

        outcome.StatusCode.ShouldBe(200);
        var query = h.Records.Single(r => r.EventType == "OLAP_QUERY");
        query.DetailsJson.ShouldContain("\"crossSource\":false");
    }

    [Fact]
    public async Task NonSqlSource_Alone_IsTreatedAsCrossSource()
    {
        var h = Build([new TableDef("api", "orders", ConnectorId: "http-a", Type: DataSourceType.HttpDeclarative)]);

        var outcome = await Run(h, "SELECT id FROM orders", "api.public.orders");

        outcome.StatusCode.ShouldBe(403);
        outcome.Error.ShouldBe(GateDenied);
        h.Reads.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(DataSourceType.HttpPlugin)]
    [InlineData(DataSourceType.LakehouseIceberg)]
    [InlineData(DataSourceType.LakehouseDelta)]
    public async Task UnsupportedSourceTypes_AreRejected_EvenWhenTheGateIsOpen(DataSourceType type)
    {
        var h = Build([new TableDef("api", "orders", ConnectorId: "plugin-a", Type: type)], Open("Olap"));

        var outcome = await Run(h, "SELECT id FROM orders", "api.public.orders");

        outcome.StatusCode.ShouldBe(403);
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Denial_OfSecondTable_ReturnsGenericDenial_BeforeGate_AndNeverReadsFirstTable()
    {
        var defs = new[] { new TableDef("sales", "orders", ConnectorId: "sql-a"), new TableDef("crm", "customers", ConnectorId: "sql-b", Deny: true) };

        var closed = Build(defs);
        var open = Build(defs, Open("Olap"));
        var a = await Run(closed, JoinSql, "sales.public.orders", "crm.public.customers");
        var b = await Run(open, JoinSql, "sales.public.orders", "crm.public.customers");

        a.StatusCode.ShouldBe(403);
        a.Error.ShouldBe("Access denied to table 'crm.public.customers'.");
        b.Error.ShouldBe(a.Error);
        closed.Reads.ShouldBeEmpty();
        open.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Denial_AuthorizesEveryTable_RegardlessOfPosition()
    {
        var first = Build([new TableDef("sales", "orders", Deny: true), new TableDef("crm", "customers", ConnectorId: "sql-b")]);
        var last = Build([new TableDef("sales", "orders"), new TableDef("crm", "customers", ConnectorId: "sql-b", Deny: true)]);

        await Run(first, JoinSql, "sales.public.orders", "crm.public.customers");
        await Run(last, JoinSql, "sales.public.orders", "crm.public.customers");

        first.Reads.ShouldBeEmpty();
        last.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task GateDenialBody_IsIdentical_ForDisabledAndTransportNotAllowed_AndContainsNoTableName()
    {
        var disabled = await Run(Build(TwoSources), JoinSql, "sales.public.orders", "crm.public.customers");
        var notListed = await Run(Build(TwoSources, new CrossSourceOptions { Enabled = true }), JoinSql, "sales.public.orders", "crm.public.customers");

        disabled.Error.ShouldBe(notListed.Error);
        disabled.ErrorCode.ShouldBe(notListed.ErrorCode);
        disabled.StatusCode.ShouldBe(notListed.StatusCode);
        disabled.Error!.ShouldNotContain("orders");
        disabled.Error!.ShouldNotContain("customers");
        disabled.Details.ShouldBeNull();
    }

    [Fact]
    public async Task CrossSourceOlap_TableCountAboveCrossSourceMax_Returns400()
    {
        var defs = Enumerable.Range(0, 3).Select(i => new TableDef("d" + i, "t" + i, ConnectorId: "c" + i)).ToArray();
        var cross = new CrossSourceOptions { Enabled = true, AllowedTransports = ["Olap"], MaxTableCount = 2 };
        var h = Build(defs, cross);

        var outcome = await Run(h, "SELECT 1", "d0.public.t0", "d1.public.t1", "d2.public.t2");

        outcome.StatusCode.ShouldBe(400);
        h.Reads.ShouldBeEmpty();
    }

    [Theory]
    [InlineData("SELECT a.id FROM orders a CROSS JOIN customers b")]
    [InlineData("SELECT a.id FROM orders a JOIN customers b ON a.id > b.id")]
    [InlineData("SELECT a.id FROM orders a, customers b")]
    public async Task CrossSourceOlap_CartesianAndNonEquiJoins_AreRejected(string sql)
    {
        var h = Build(TwoSources, Open("Olap"));

        var outcome = await Run(h, sql, "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(400);
        outcome.Result.ShouldBeNull();
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrossSourceOlap_StagedBytesAboveBudget_Returns400()
    {
        var olap = new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", MaxThreads = 1, MaxStagedBytesPerTable = 10 };
        var h = Build(TwoSources, Open("Olap"), olap);

        var outcome = await Run(h, JoinSql, "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(400);
        outcome.Result.ShouldBeNull();
    }

    [Fact]
    public async Task SingleSourceOlap_StagedBytesAboveBudget_Returns400()
    {
        var olap = new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", MaxThreads = 1, MaxStagedBytesPerTable = 10 };
        var h = Build([new TableDef("sales", "orders")], null, olap);

        (await Run(h, "SELECT id FROM orders", "sales.public.orders")).StatusCode.ShouldBe(400);
    }

    [Fact]
    public async Task GateDenial_WritesRequiredDenyAuditRecord_AndAuditFailureStillDenies()
    {
        var h = Build(TwoSources);
        h.Audit.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new AuditUnavailableException("down"));

        var outcome = await Run(h, JoinSql, "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(403);
        await h.Audit.Received().RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == "OLAP_DENIED" && e.Decision == "DENY"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GateDenial_RecordsOlapDenied_WithoutSqlText()
    {
        var h = Build(TwoSources);

        await Run(h, JoinSql, "sales.public.orders", "crm.public.customers");

        var denied = h.Records.Single(r => r.EventType == "OLAP_DENIED");
        denied.Decision.ShouldBe("DENY");
        denied.TraceId.ShouldBe("trace-1");
        denied.DetailsJson.ShouldContain("sqlSha256");
        denied.DetailsJson.ShouldNotContain("JOIN");
    }

    // ---- classification ----

    [Fact]
    public async Task Classification_SameSourceNameDifferentCase_IsOneSource()
    {
        var h = Build([new TableDef("sales", "orders", SourceName: "Main"), new TableDef("sales", "customers", SourceName: "main")]);

        (await Run(h, JoinSql, "sales.public.orders", "sales.public.customers")).StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Classification_SameSourceNameDifferentConnectorId_IsCrossSource()
    {
        var h = Build([new TableDef("sales", "orders", ConnectorId: "sql-a", SourceName: "main"), new TableDef("sales", "customers", ConnectorId: "sql-b", SourceName: "main")]);

        (await Run(h, JoinSql, "sales.public.orders", "sales.public.customers")).StatusCode.ShouldBe(403);
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task Classification_DifferentDomainsWithoutSourceName_IsCrossSource()
    {
        var h = Build([new TableDef("sales", "orders"), new TableDef("crm", "customers")]);

        (await Run(h, JoinSql, "sales.public.orders", "crm.public.customers")).StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task Dedupe_SameTableUnderTwoSpellings_IsOneTable()
    {
        var h = Build([new TableDef("sales", "orders")]);

        var outcome = await Run(h, "SELECT id FROM orders", "sales.public.orders", "SALES.PUBLIC.ORDERS");

        outcome.StatusCode.ShouldBe(200);
        h.Reads.Count.ShouldBe(1);
    }

    [Fact]
    public async Task StagingNameCollision_IsRejected_BeforeAnyRead()
    {
        var h = Build([new TableDef("sales", "a-b"), new TableDef("sales", "a_b")]);

        var outcome = await Run(h, "SELECT 1", "sales.public.a-b", "sales.public.a_b");

        outcome.StatusCode.ShouldBe(403);
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task InactiveTable_IsDenied()
    {
        var h = Build([new TableDef("sales", "orders", Active: false)]);

        (await Run(h, "SELECT id FROM orders", "sales.public.orders")).StatusCode.ShouldBe(403);
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task AmbiguousBareName_AnswersLikeNotFound()
    {
        var h = Build([new TableDef("sales", "orders"), new TableDef("crm", "orders", ConnectorId: "sql-b")]);

        var outcome = await Run(h, "SELECT id FROM orders", "orders");

        outcome.StatusCode.ShouldBe(403);
        outcome.Error.ShouldBe("Access denied to table 'orders'.");
        h.Reads.ShouldBeEmpty();
    }

    // ---- guardrails ----

    private static readonly IReadOnlyList<TableColumn> PiiColumns =
    [
        new TableColumn { ColumnName = "id", DataType = "int" },
        new TableColumn { ColumnName = "ssn", DataType = "string", IsSensitive = true }
    ];

    private static TableDef PiiTable(string domain, string table, string connector, string ruleType) => new(
        domain, table, ConnectorId: connector, Columns: PiiColumns,
        ColumnAccess: new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["ssn"] = ColumnAccessLevel.Mask },
        Rules: new Dictionary<string, MaskingRule> { ["ssn"] = new MaskingRule { RuleType = ruleType } });

    [Fact]
    public async Task CrossSourceOlap_JoinOnRedactedColumn_Rejected_WithGenericBody()
    {
        var h = Build([PiiTable("sales", "orders", "sql-a", "REDACT"), PiiTable("crm", "customers", "sql-b", "REDACT")], Open("Olap"));

        var outcome = await Run(h, "SELECT a.id FROM orders a JOIN customers b ON a.ssn = b.ssn", "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(403);
        outcome.Error!.ShouldNotContain("ssn");
        outcome.Error!.ShouldNotContain("orders");
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrossSourceOlap_FilterOnRedactedColumn_Rejected()
    {
        var h = Build([PiiTable("sales", "orders", "sql-a", "REDACT"), PiiTable("crm", "customers", "sql-b", "REDACT")], Open("Olap"));

        var outcome = await Run(h, "SELECT a.id FROM orders a JOIN customers b ON a.id = b.id WHERE a.ssn = 'x'", "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(403);
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrossSourceOlap_UnqualifiedMaskedColumn_Rejected()
    {
        var h = Build([PiiTable("sales", "orders", "sql-a", "REDACT"), PiiTable("crm", "customers", "sql-b", "REDACT")], Open("Olap"));

        var outcome = await Run(h, "SELECT a.id FROM orders a JOIN customers b ON a.id = b.id WHERE ssn = 'x'", "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(403);
    }

    [Fact]
    public async Task CrossSourceOlap_JoinOnHmacColumn_Allowed_ParityWithWebSql()
    {
        var h = Build([PiiTable("sales", "orders", "sql-a", "HMAC"), PiiTable("crm", "customers", "sql-b", "HMAC")], Open("Olap"));

        var outcome = await Run(h, "SELECT a.id FROM orders a JOIN customers b ON a.ssn = b.ssn", "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(200);
    }

    [Theory]
    [InlineData("HASH")]
    [InlineData("HMAC_SHA256")]
    [InlineData("PARTIAL_MASK")]
    [InlineData("REDACT")]
    public async Task CrossSourceOlap_JoinOnMaskedColumn_NonHmacRuleTypes_AreRejected(string ruleType)
    {
        var h = Build([PiiTable("sales", "orders", "sql-a", ruleType), PiiTable("crm", "customers", "sql-b", ruleType)], Open("Olap"));

        var outcome = await Run(h, "SELECT a.id FROM orders a JOIN customers b ON a.ssn = b.ssn", "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(403);
    }

    [Theory]
    [InlineData("SELECT a.id FROM orders a NATURAL JOIN customers b")]
    [InlineData("SELECT * FROM orders a JOIN customers b ON a.id = b.id WHERE COLUMNS(*) = 'x'")]
    [InlineData("SELECT COLUMNS('^s') FROM orders a JOIN customers b ON a.id = b.id")]
    [InlineData("SELECT * EXCLUDE (name) FROM orders a JOIN customers b ON a.id = b.id")]
    [InlineData("SELECT * REPLACE (1 AS id) FROM orders a JOIN customers b ON a.id = b.id")]
    [InlineData("SELECT #1 FROM orders a JOIN customers b ON a.id = b.id")]
    [InlineData("SELECT a.id, count(*) FROM orders a JOIN customers b ON a.id = b.id GROUP BY ALL")]
    [InlineData("SELECT a.id FROM orders a JOIN customers b ON a.id = b.id ORDER BY ALL")]
    [InlineData("SELECT a.id FROM orders a JOIN customers b ON a.id = b.id QUALIFY row_number() OVER () = 1")]
    [InlineData("SELECT a.id FROM orders a JOIN customers b ON a.id = b.id TABLESAMPLE 10%")]
    [InlineData("SELECT a.id FROM orders a, LATERAL (SELECT b.id FROM customers b WHERE b.id = a.id) x")]
    [InlineData("SELECT a.id FROM orders a JOIN customers b ON a.id = b.id WHERE lpad(a.name, 100000) = 'x'")]
    [InlineData("SELECT query('SELECT 1') FROM orders a JOIN customers b ON a.id = b.id")]
    [InlineData("SELECT * FROM orders a POSITIONAL JOIN customers b")]
    [InlineData("SELECT a.id FROM orders a JOIN customers b ON a.id = b.id JOIN other c ON c.id = a.id")]
    [InlineData("SELECT a.id FROM orders a JOIN information_schema.tables b ON a.id = 1")]
    public async Task CrossSourceOlap_DifferentialCorpus_IsRejected_WithZeroReads(string sql)
    {
        var h = Build(TwoSources, Open("Olap"));

        var outcome = await Run(h, sql, "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBeOneOf(400, 403);
        outcome.Result.ShouldBeNull();
        h.Reads.ShouldBeEmpty();
    }

    [Fact]
    public async Task CrossSourceOlap_AliasedAndDomainViewNames_AreMappedToStagedTables()
    {
        var h = Build(TwoSources, Open("Olap"));

        var outcome = await Run(h, "SELECT o.id FROM sales_orders o JOIN crm_customers c ON o.id = c.id", "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task CrossSourceOlap_CteOverStagedTables_Succeeds()
    {
        var h = Build(TwoSources, Open("Olap"));

        var outcome = await Run(h, "WITH j AS (SELECT a.id AS id FROM orders a JOIN customers b ON a.id = b.id) SELECT count(*) FROM j", "sales.public.orders", "crm.public.customers");

        outcome.StatusCode.ShouldBe(200);
    }

    // ---- all requests ----

    [Fact]
    public async Task DeniedColumn_IsNotStaged_AndBehavesLikeAnUnknownColumn()
    {
        var def = new TableDef(
            "sales", "orders", Columns: PiiColumns,
            ColumnAccess: new Dictionary<string, ColumnAccessLevel> { ["id"] = ColumnAccessLevel.Clear, ["ssn"] = ColumnAccessLevel.Deny });
        var h = Build([def]);

        var star = await Run(h, "SELECT * FROM orders", "sales.public.orders");
        var denied = await Run(h, "SELECT ssn FROM orders", "sales.public.orders");

        star.StatusCode.ShouldBe(200);
        star.Result!.Columns.ToArray().ShouldBe(new[] { "id" });
        denied.StatusCode.ShouldBe(400);
        denied.Error!.ShouldNotContain("ssn");
    }

    [Fact]
    public async Task SecondStatement_IsRejected_BeforeAnyTableIsResolved()
    {
        var h = Build([new TableDef("sales", "orders")]);

        var outcome = await Run(h, "SELECT 1; SELECT 2", "sales.public.orders");

        outcome.StatusCode.ShouldBe(400);
        h.Reads.ShouldBeEmpty();
        h.MetadataCalls.ShouldBe(0);
    }

    [Fact]
    public async Task OverlongSql_IsRejected_BeforeAnyTableIsResolved()
    {
        var h = Build([new TableDef("sales", "orders")], olap: new DuckDbOlapOptions { Enabled = true, MaxSqlLength = 20 });

        var outcome = await Run(h, "SELECT id FROM orders WHERE id > 100000000", "sales.public.orders");

        outcome.StatusCode.ShouldBe(400);
        h.MetadataCalls.ShouldBe(0);
    }

    [Fact]
    public async Task TenantAtItsConcurrencyCap_IsRefusedWith429()
    {
        var olap = new DuckDbOlapOptions { Enabled = true, MaxConcurrentQueriesPerTenant = 1, MaxConcurrentQueries = 4, MaxMemory = "256MB", MaxThreads = 1 };
        var h = Build([new TableDef("sales", "orders")], null, olap);
        var limiter = new OlapRequestLimiter(Options.Create(new GatewayOptions { DuckDbOlap = olap }));

        using var held = await limiter.TryAcquireAsync("tenant-1", CancellationToken.None);
        var refused = await limiter.TryAcquireAsync("tenant-1", CancellationToken.None);
        using var otherTenant = await limiter.TryAcquireAsync("tenant-2", CancellationToken.None);

        held.ShouldNotBeNull();
        refused.ShouldBeNull();
        otherTenant.ShouldNotBeNull();
        (await Run(h, "SELECT id FROM orders", "sales.public.orders")).StatusCode.ShouldBe(200);
    }

    [Fact]
    public async Task Lease_IsReleased_AfterDispose()
    {
        var olap = new DuckDbOlapOptions { MaxConcurrentQueriesPerTenant = 1, MaxConcurrentQueries = 2 };
        var limiter = new OlapRequestLimiter(Options.Create(new GatewayOptions { DuckDbOlap = olap }));

        var first = await limiter.TryAcquireAsync("t", CancellationToken.None);
        first!.Dispose();
        using var second = await limiter.TryAcquireAsync("t", CancellationToken.None);

        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task Limiter_RemovesIdleTenantEntries()
    {
        var limiter = new OlapRequestLimiter(Options.Create(new GatewayOptions { DuckDbOlap = new DuckDbOlapOptions() }));

        for (int i = 0; i < 50; i++)
        {
            using var lease = await limiter.TryAcquireAsync("tenant-" + i, CancellationToken.None);
            lease.ShouldNotBeNull();
        }

        limiter.TrackedTenants.ShouldBe(0);
    }

    [Fact]
    public async Task RequestWithoutTableNames_AlsoTakesTheLease()
    {
        var olap = new DuckDbOlapOptions { Enabled = true, MaxConcurrentQueriesPerTenant = 1, MaxConcurrentQueries = 2, MaxMemory = "256MB", MaxThreads = 1 };
        var options = Options.Create(new GatewayOptions { DuckDbOlap = olap, WebSql = new WebSqlOptions() });
        var limiter = new OlapRequestLimiter(options);
        var sut = new OlapStagingService(
            new DuckDbOlapEngine(options, NullLogger<DuckDbOlapEngine>.Instance),
            Substitute.For<ITableMetadataRepository>(), Substitute.For<IAutherisConnectorRegistry>(), Substitute.For<ICrossDomainAccessResolver>(),
            Substitute.For<IColumnMaskingProvider>(), options, Substitute.For<IAuditWriter>(), NullLogger<OlapStagingService>.Instance,
            new FederationGate(options), FastSqlEngine.Default, limiter);

        using var held = await limiter.TryAcquireAsync("tenant-1", CancellationToken.None);
        var outcome = await sut.ExecuteAsync(User(), Caller(), new OlapQueryCommand("SELECT 1"), isDevelopment: false, "t", CancellationToken.None);

        outcome.StatusCode.ShouldBe(429);
    }
}
