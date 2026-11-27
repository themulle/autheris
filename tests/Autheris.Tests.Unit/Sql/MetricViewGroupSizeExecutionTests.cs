namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Security;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class MetricViewGroupSizeExecutionTests
{
    private const string Tenant = "tenant_a";

    [Fact]
    [Trait("Category", "Security")]
    public async Task SmallGroupStrategyMergeIntoOther_StillSuppresses()
    {
        var analytics = new AnalyticsOptions { SmallGroupStrategy = SmallGroupStrategy.MergeIntoOther };
        var rewriter = new GovernedSqlRewriter(Options.Create(new GatewayOptions()), analyticsOptions: Options.Create(analytics));
        var view = new TableMetadata
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            Table = new Table { Id = Guid.NewGuid(), SchemaName = "analytics", TableName = "sales_mv", ObjectKind = DiscoveryObjectKind.MetricView }
        };
        int k = await rewriter.ResolveMetricViewMinGroupSizeAsync(view, new TenantId(Tenant), CancellationToken.None);
        k.ShouldBe(5);

        await using var raw = new SequentialAccessReader(
            ["region", MetricViewStatementGuard.HiddenCountAlias],
            [typeof(string), typeof(long)],
            [["EU", 12L], ["US", 4L]]);
        await using var wrapper = new MetricViewGroupSizeDataReader(raw, hiddenOrdinal: 1, minGroupSize: k);

        var surviving = new List<string>();
        while (await wrapper.ReadAsync(CancellationToken.None))
        {
            surviving.Add(wrapper.GetString(0));
        }

        surviving.ShouldBe(["EU"]);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task SequentialAccessReader_DropsSmallGroups_HidesInternalCountColumn_AndHandlesZeroRows()
    {
        await using var raw = new SequentialAccessReader(
            ["region", MetricViewStatementGuard.HiddenCountAlias, "total_rev"],
            [typeof(string), typeof(long), typeof(decimal)],
            [
                ["EU", 12L, 500m],
                ["US", 4L, 90m],
                ["APAC", DBNull.Value, 10m],
                ["LATAM", 5L, 250m]
            ]);

        await using var wrapper = new MetricViewGroupSizeDataReader(raw, hiddenOrdinal: 1, minGroupSize: 5);
        var cols = SqlResultColumns.Describe(wrapper);
        cols.Select(c => c.Name).ShouldBe(["region", "total_rev"]);
        wrapper.FieldCount.ShouldBe(2);
        Should.Throw<ArgumentOutOfRangeException>(() => wrapper.GetOrdinal(MetricViewStatementGuard.HiddenCountAlias));

        var surviving = new List<(string Region, decimal Revenue)>();
        while (await wrapper.ReadAsync(CancellationToken.None))
        {
            surviving.Add((wrapper.GetString(0), wrapper.GetDecimal(1)));
        }

        surviving.ShouldBe([("EU", 500m), ("LATAM", 250m)]);

        // Zero rows: column metadata still strips the internal count column.
        await using var zeroRaw = new SequentialAccessReader(
            ["region", MetricViewStatementGuard.HiddenCountAlias],
            [typeof(string), typeof(long)],
            []);
        await using var zeroWrapper = new MetricViewGroupSizeDataReader(zeroRaw, hiddenOrdinal: 1, minGroupSize: 5);
        SqlResultColumns.Describe(zeroWrapper).Select(c => c.Name).ShouldBe(["region"]);
        (await zeroWrapper.ReadAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Executor_FiltersSmallGroups_HidesCountColumn_AndFailsClosedIfHiddenColumnMissing()
    {
        var opts = new GatewayOptions();
        opts.DataSources.Connections["dbx"] = new DataSourceConnectionOptions
        {
            Provider = "Databricks",
            ConnectionString = "Host=example"
        };

        var mvId = new TableIdentity("analytics", "sales_mv");
        var engine = new FastSqlEngine();
        var catalog = new InMemoryTableCatalog([
            new TableCatalogEntry(
                mvId,
                [
                    new CatalogColumn("region", "string"),
                    new CatalogColumn("tenant_id", "string"),
                    new CatalogColumn("revenue", "decimal(18,2)"),
                    new CatalogColumn("row_count", "bigint")
                ],
                "tenant_id",
                ImmutableArray<string>.Empty,
                Version: 1,
                default,
                IsMetricView: true)
        ], "analytics");

        var compiled = engine.Compile(
            "SELECT region, MEASURE(revenue) AS total_rev, MEASURE(\"row_count\") AS __autheris_mv_group_count FROM analytics.sales_mv GROUP BY region".AsMemory(),
            new CompileRequest
            {
                TargetDialect = TargetSqlDialect.Databricks,
                TokenGuards = SqlTokenSecurityOptions.Strict,
                AllowExperimentalDialect = true,
                AllowedFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "measure" },
                Policy = new GovernancePolicy
                {
                    Catalog = catalog,
                    RowFilters = new AllowAllPolicyProvider(),
                    Masks = new NoMaskProvider(),
                    Tenant = new TenantBinding("__autheris_tenant", Tenant, SqlParameterType.String)
                }
            },
            CancellationToken.None);

        DbDataReader nextReader = new SequentialAccessReader(
            ["region", "total_rev", MetricViewStatementGuard.HiddenCountAlias],
            [typeof(string), typeof(decimal), typeof(long)],
            [
                ["EU", 500m, 10L],
                ["US", 20m, 2L]
            ]);

        var conn = new FakeDbConnection(() => new FakeDbCommand
        {
            OnExecuteReader = (_, _) => Task.FromResult(nextReader)
        });
        var factory = Substitute.For<ISqlConnectionFactory>();
        factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbConnection>(conn));
        var session = Substitute.For<IDbSessionContextInitializer>();
        session.InitializeSessionAsync(Arg.Any<DbConnection>(), Arg.Any<DatabaseDialect>(), Arg.Any<TenantId>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DbTransaction?>(null));

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var wrappedOpts = Options.Create(opts);
        var executor = new GovernedSqlExecutor(
            new GovernedSqlRewriter(wrappedOpts, sqlEngine: engine),
            wrappedOpts,
            AuditWriterTestServices.Over(auditRepo),
            PassThroughExecutionGuard.Instance,
            factory,
            logger: NullLogger<GovernedSqlExecutor>.Instance,
            sessionInitializer: session);

        var rewrite = new GovernedRewrite(
            compiled,
            [],
            "dbx",
            DeliveredRowLimit: 10,
            MetricViewHiddenCountAlias: MetricViewStatementGuard.HiddenCountAlias,
            MetricViewMinGroupSize: 5);

        var principal = Autheris.Tests.Unit.TestSupport.PipelinePrincipals.MarkComplete(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-MV"), new Claim("tenant_id", Tenant)], "Test")));

        var collectedCols = new List<string>();
        var collectedRows = new List<Dictionary<string, object?>>();
        var masksSeen = ImmutableArray<bool>.Empty;

        await executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("SELECT region, MEASURE(revenue) AS total_rev FROM analytics.sales_mv GROUP BY region"),
            principal,
            new TenantId(Tenant),
            rewrite,
            new DmlAuditContext(),
            new Dictionary<string, object?>(),
            async (reader, info, ct) =>
            {
                collectedCols.AddRange(info.Columns.Select(c => c.Name));
                masksSeen = info.MaskedColumns;
                while (await reader.ReadAsync(ct))
                {
                    var d = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        d[info.Columns[i].Name] = reader.GetValue(i);
                    }

                    collectedRows.Add(d);
                }
            },
            CancellationToken.None);

        collectedCols.ShouldBe(["region", "total_rev"]);
        masksSeen.ToArray().ShouldBe([false, false]);
        var row = collectedRows.ShouldHaveSingleItem();
        row["region"].ShouldBe("EU");
        row.ContainsKey(MetricViewStatementGuard.HiddenCountAlias).ShouldBeFalse();

        // Missing hidden count column in database result must fail closed with CompilerInvariant.
        nextReader = new SequentialAccessReader(["region", "total_rev"], [typeof(string), typeof(decimal)], [["EU", 500m]]);
        var missingEx = await Should.ThrowAsync<GovernedSqlException>(() => executor.ExecuteRewrittenAsync(
            new GovernedSqlQueryRequest("SELECT region, MEASURE(revenue) AS total_rev FROM analytics.sales_mv GROUP BY region"),
            principal,
            new TenantId(Tenant),
            rewrite,
            new DmlAuditContext(),
            new Dictionary<string, object?>(),
            (_, _, _) => Task.CompletedTask,
            CancellationToken.None));
        missingEx.Code.ShouldBe(GovernedSqlErrorCodes.CompilerInvariant);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task GovernancePolicyFactory_ExemptsInternalDeniedCountColumnOnlyForMetricView()
    {
        var opts = new GatewayOptions();
        opts.DataSources.Connections["dbx"] = new DataSourceConnectionOptions
        {
            Provider = "Databricks",
            ConnectionString = "Host=example"
        };
        var mv = new TableMetadata
        {
            Identifier = new TableIdentifier("dbx", "analytics", "sales_mv"),
            TenantColumnName = "tenant_id",
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceType = "Databricks",
                SourceName = "dbx",
                SchemaName = "analytics",
                TableName = "sales_mv",
                ObjectKind = DiscoveryObjectKind.MetricView,
                MinGroupSize = 5,
                MetricViewCountMeasure = "row_count",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "region", DataType = "string", Role = MetricViewColumnRole.Dimension },
                new TableColumn { ColumnName = "tenant_id", DataType = "string", Role = MetricViewColumnRole.Dimension },
                new TableColumn { ColumnName = "revenue", DataType = "decimal(18,2)", Role = MetricViewColumnRole.Measure },
                new TableColumn { ColumnName = "row_count", DataType = "bigint", Role = MetricViewColumnRole.Measure }
            ]
        };

        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(mv.Identifier, Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(mv));
        var catalog = new InMemoryTableCatalog([
            new TableCatalogEntry(
                new TableIdentity("analytics", "sales_mv"),
                [
                    new CatalogColumn("region", "string"),
                    new CatalogColumn("tenant_id", "string"),
                    new CatalogColumn("revenue", "decimal(18,2)"),
                    new CatalogColumn("row_count", "bigint")
                ],
                "tenant_id",
                ImmutableArray<string>.Empty,
                Version: 1,
                default,
                IsMetricView: true)
        ], "analytics");

        var policyFactory = new GovernancePolicyFactory(new StaticCatalogProvider(catalog), tableRepo, Options.Create(opts));
        var decision = TableAccessDecision.Allowed(
            mv.Identifier,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["region"] = ColumnAccessLevel.Clear,
                ["tenant_id"] = ColumnAccessLevel.Clear,
                ["revenue"] = ColumnAccessLevel.Clear,
                ["row_count"] = ColumnAccessLevel.Deny
            });
        var decisions = new Dictionary<TableIdentity, TableAccessDecision>
        {
            [new TableIdentity("analytics", "sales_mv")] = decision
        };
        var metadata = new Dictionary<TableIdentity, TableMetadata>
        {
            [new TableIdentity("analytics", "sales_mv")] = mv
        };
        var input = new GovernedPolicyInput(
            "dbx",
            new TenantId(Tenant),
            decisions,
            new HashSet<TableIdentity> { new("analytics", "sales_mv") },
            GovernedSubqueryStrategy.Correlated)
        {
            Metadata = metadata,
            MetricViewInternalCountColumn = "row_count"
        };

        var policy = await policyFactory.CreateAsync(input, CancellationToken.None);
        policy.Masks.HasMask(new TableIdentity("analytics", "sales_mv"), "row_count").ShouldBeFalse();
        policy.Catalog.Resolve(new TableIdentity("analytics", "sales_mv").ToQualifiedName())!.IsMetricView.ShouldBeTrue();
    }

    private sealed class SequentialAccessReader : DbDataReader, IDbColumnSchemaGenerator
    {
        private readonly string[] _names;
        private readonly Type[] _types;
        private readonly object?[][] _rows;
        private int _rowIndex = -1;
        private int _lastReadOrdinal = -1;

        public SequentialAccessReader(string[] names, Type[] types, object?[][] rows)
        {
            _names = names;
            _types = types;
            _rows = rows;
        }

        public override int FieldCount => _names.Length;
        public override bool HasRows => _rows.Length > 0;
        public override bool IsClosed => false;
        public override int Depth => 0;
        public override int RecordsAffected => -1;
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));

        public override bool Read()
        {
            if (_rowIndex + 1 >= _rows.Length)
            {
                return false;
            }

            _rowIndex++;
            _lastReadOrdinal = -1;
            return true;
        }

        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override bool NextResult() => false;
        public override string GetName(int ordinal) => _names[ordinal];
        public override string GetDataTypeName(int ordinal) => _types[ordinal].Name;
        public override Type GetFieldType(int ordinal) => _types[ordinal];

        public override int GetOrdinal(string name)
        {
            for (int i = 0; i < _names.Length; i++)
            {
                if (string.Equals(_names[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(name), $"Column '{name}' was not found.");
        }

        private void EnsureSequentialOrdinal(int ordinal)
        {
            if (ordinal < _lastReadOrdinal)
            {
                throw new InvalidOperationException($"SequentialAccess violation: ordinal {ordinal} read after {_lastReadOrdinal}.");
            }

            _lastReadOrdinal = ordinal;
        }

        public override bool IsDBNull(int ordinal)
        {
            EnsureSequentialOrdinal(ordinal);
            return _rows[_rowIndex][ordinal] is null or DBNull;
        }

        public override object GetValue(int ordinal)
        {
            EnsureSequentialOrdinal(ordinal);
            return _rows[_rowIndex][ordinal] ?? DBNull.Value;
        }

        public override int GetValues(object[] values) => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => (bool)GetValue(ordinal);
        public override byte GetByte(int ordinal) => (byte)GetValue(ordinal);
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override char GetChar(int ordinal) => (char)GetValue(ordinal);
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => throw new NotSupportedException();
        public override DateTime GetDateTime(int ordinal) => (DateTime)GetValue(ordinal);
        public override decimal GetDecimal(int ordinal) => (decimal)GetValue(ordinal);
        public override double GetDouble(int ordinal) => (double)GetValue(ordinal);
        public override float GetFloat(int ordinal) => (float)GetValue(ordinal);
        public override Guid GetGuid(int ordinal) => (Guid)GetValue(ordinal);
        public override short GetInt16(int ordinal) => (short)GetValue(ordinal);
        public override int GetInt32(int ordinal) => (int)GetValue(ordinal);
        public override long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);
        public override string GetString(int ordinal) => (string)GetValue(ordinal);
        public override System.Collections.IEnumerator GetEnumerator() => new DbEnumerator(this);

        public System.Collections.ObjectModel.ReadOnlyCollection<DbColumn> GetColumnSchema() =>
            _names.Select((n, i) => (DbColumn)new FakeColumn(n, i, _types[i])).ToList().AsReadOnly();

        private sealed class FakeColumn : DbColumn
        {
            public FakeColumn(string name, int ordinal, Type type)
            {
                ColumnName = name;
                ColumnOrdinal = ordinal;
                DataType = type;
                DataTypeName = type.Name;
                AllowDBNull = true;
            }
        }
    }

    private sealed class AllowAllPolicyProvider : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
    }

    private sealed class NoMaskProvider : IColumnMaskProvider
    {
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private sealed class StaticCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct) =>
            new(catalog);
    }
}
