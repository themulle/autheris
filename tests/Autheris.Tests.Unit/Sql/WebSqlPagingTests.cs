namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Cost;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Internal;
using Autheris.Application.Sql.Paging;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

public sealed class WebSqlPagingTests
{
    private static ClaimsPrincipal CreateUser(string sid = "user-1", string tenant = "tenant-1") =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, sid),
                new Claim("sub", sid),
                new Claim("tenant_id", tenant)
            ],
            "TestAuth"));

    private static TableMetadata CustomersTable(string schema = "crm") => new()
    {
        Identifier = new TableIdentifier("crm", schema, "customers"),
        Table = new Table { TableName = "customers", SchemaName = schema, SourceName = "crm", SourceType = "PostgreSQL", IsActive = true, DataSourceType = DataSourceType.Sql },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "name", DataType = "varchar" }
        ]
    };

    private sealed class RowsDbDataReader(int totalRows) : System.Data.Common.DbDataReader
    {
        private int _current = 0;
        public override int FieldCount => 2;
        public override bool HasRows => totalRows > 0;
        public override bool Read() => ++_current <= totalRows;
        public override Task<bool> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Read());
        public override string GetName(int ordinal) => ordinal == 0 ? "id" : "name";
        public override object GetValue(int ordinal) => ordinal == 0 ? _current : $"row_{_current}";
        public override object this[int ordinal] => GetValue(ordinal);
        public override object this[string name] => GetValue(GetOrdinal(name));
        public override int GetOrdinal(string name) => string.Equals(name, "id", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        public override bool IsDBNull(int ordinal) => false;
        public override Type GetFieldType(int ordinal) => ordinal == 0 ? typeof(int) : typeof(string);
        public override string GetDataTypeName(int ordinal) => ordinal == 0 ? "int" : "varchar";
        public override int Depth => 0;
        public override bool IsClosed => false;
        public override int RecordsAffected => -1;
        public override bool NextResult() => false;
        public override System.Collections.IEnumerator GetEnumerator() => throw new NotSupportedException();
        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => ' ';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override DateTime GetDateTime(int ordinal) => DateTime.UtcNow;
        public override decimal GetDecimal(int ordinal) => 0;
        public override double GetDouble(int ordinal) => 0;
        public override float GetFloat(int ordinal) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => _current;
        public override long GetInt64(int ordinal) => _current;
        public override string GetString(int ordinal) => $"row_{_current}";
        public override int GetValues(object[] values) => 0;
    }

    private static (GovernedSqlExecutionService Service, GatewayOptions Options, IAuditWriter Audit) CreatePipeline(
        WebSqlPagingOptions? paging = null,
        int fakeRows = 0)
    {
        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "crm",
                Paging = paging ?? new WebSqlPagingOptions
                {
                    Enabled = true,
                    MaxOffset = 10_000,
                    TokenLifetimeMinutes = 15
                }
            },
            DataSources = new SqlDataSourceOptions
            {
                Connections =
                {
                    ["crm"] = new DataSourceConnectionOptions
                    {
                        Provider = "PostgreSQL",
                        ConnectionString = "Server=localhost;Database=crm;User Id=postgres;Password=postgres;"
                    }
                }
            }
        };

        var auditWriter = Substitute.For<IAuditWriter>();
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<TableMetadata?>(CustomersTable(ci.Arg<TableIdentifier>().Schema)));

        var consents = Substitute.For<IConsentRepository>();
        consents.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>([]));

        var resolution = Substitute.For<IConsentResolutionService>();
        resolution.ResolveAccess(Arg.Any<Sid>(), Arg.Any<IReadOnlySet<Sid>>(), Arg.Any<IReadOnlySet<string>>(), Arg.Any<TableIdentifier>(), Arg.Any<IReadOnlyList<Consent>>(), Arg.Any<DatabaseDialect>())
            .Returns(ci => TableAccessDecision.Allowed(ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), null, hasUnconstrainedColumnAllow: true));

        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");

        var digester = new CompiledSqlAuditDigester(Options.Create(options), null, env);
        var codec = new WebSqlPageTokenCodec(digester);

        ISqlConnectionFactory? connFactory = null;
        if (fakeRows > 0)
        {
            var connection = new FakeDbConnection(() => new FakeDbCommand
            {
                OnExecuteReader = (_, _) => Task.FromResult<System.Data.Common.DbDataReader>(new RowsDbDataReader(fakeRows))
            });
            connFactory = Substitute.For<ISqlConnectionFactory>();
            connFactory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<System.Data.Common.DbConnection>(connection));
        }

        var service = GovernedSqlExecutionServiceFactory.Create(
            Options.Create(options),
            Substitute.For<IAuditLogRepository>(),
            executionGuard: GovernedTestSupport.Guard,
            consentResolution: resolution,
            tableRepository: repo,
            connectionFactory: connFactory,
            policyFactory: GovernedTestSupport.PolicyFactory(repo, Options.Create(options)),
            typedPolicies: GovernedTestSupport.TypedPolicies(Options.Create(options)),
            logger: NullLogger<GovernedSqlExecutionService>.Instance,
            consentRepository: consents,
            auditDigester: digester,
            auditWriter: auditWriter,
            environment: env,
            pageTokenCodec: codec);

        return (service, options, auditWriter);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Offsets_DoNotGrowTemplateCacheUnbounded()
    {
        var cache = new CompiledSqlTemplateCache(maxEntries: 512);
        var env = new TestCompileEnv(TargetSqlDialect.SqlServer, "crm")
            .Table("customers", null, "id:int", "name:varchar");

        var compiler = new GovernedSqlCompiler(env.Engine, DialectCapabilityTable.Default, cache);
        string rawSql = "SELECT id, name FROM crm.customers ORDER BY id";

        int iterations = 1000;
        CompiledSql? lastCompiled = null;

        for (int i = 0; i < iterations; i++)
        {
            long offset = i * 500L;
            var request = env.Request() with
            {
                BindPageOffset = true,
                EnforcedMaxRows = 501,
                PageOffset = offset,
                MaxAllowedOffset = 1_000_000
            };

            lastCompiled = compiler.Compile(rawSql.AsMemory(), request, CancellationToken.None);
            lastCompiled.Sql.ShouldNotBeNull();
            var pageParam = lastCompiled.Parameters.Single(p => p.Origin == ParameterOrigin.PageOffset);
            pageParam.Value.ShouldBe(offset);
        }

        cache.Stats.Entries.ShouldBe(1);
        cache.Stats.Misses.ShouldBe(1);
        cache.Stats.Hits.ShouldBe(iterations - 1);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PageOffset_NotInCacheKeyMaterial()
    {
        var env = new TestCompileEnv(TargetSqlDialect.PostgreSql, "crm")
            .Table("customers", null, "id:int");

        var caps = DialectCapabilityTable.Default.Get(TargetSqlDialect.PostgreSql);
        string sql = "SELECT id FROM crm.customers ORDER BY id";

        var req1 = env.Request() with
        {
            BindPageOffset = true,
            EnforcedMaxRows = 101,
            PageOffset = 0
        };

        var req2 = env.Request() with
        {
            BindPageOffset = true,
            EnforcedMaxRows = 101,
            PageOffset = 9_500
        };

        string key1 = CompileCacheKey.Material(req1, sql, caps);
        string key2 = CompileCacheKey.Material(req2, sql, caps);

        key1.ShouldBe(key2);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PageOffsetSlot_MissingOrOutOfRange_FailsClosed()
    {
        var cache = new CompiledSqlTemplateCache();
        var env = new TestCompileEnv(TargetSqlDialect.SqlServer, "crm")
            .Table("customers", null, "id:int");

        var compiler = new GovernedSqlCompiler(env.Engine, DialectCapabilityTable.Default, cache);
        string rawSql = "SELECT id FROM crm.customers ORDER BY id";
        var request = env.Request() with
        {
            BindPageOffset = true,
            EnforcedMaxRows = 51,
            PageOffset = 0,
            MaxAllowedOffset = 10_000
        };

        var compiled = compiler.Compile(rawSql.AsMemory(), request, CancellationToken.None);
        var template = cache.Snapshot().Single();

        // Missing value (null) fails closed
        Should.Throw<SecurityException>(() => template.Rebind(new Dictionary<string, PolicyValue>(), null, 10_000));

        // Negative offset fails closed
        Should.Throw<SecurityException>(() => template.Rebind(new Dictionary<string, PolicyValue>(), -1, 10_000));

        // Offset above MaxAllowedOffset fails closed
        Should.Throw<SecurityException>(() => template.Rebind(new Dictionary<string, PolicyValue>(), 10_001, 10_000));
    }

    [Fact]
    public void OffsetRendering_GoldenPerDialect()
    {
        string rawSql = "SELECT id, name FROM crm.customers ORDER BY id";

        var dialects = new[]
        {
            TargetSqlDialect.PostgreSql,
            TargetSqlDialect.SqlServer,
            TargetSqlDialect.Oracle,
            TargetSqlDialect.DuckDb,
            TargetSqlDialect.Databricks
        };

        foreach (var dialect in dialects)
        {
            var env = new TestCompileEnv(dialect, "crm")
                .Table("customers", null, "id:int", "name:varchar");
            var cache = new CompiledSqlTemplateCache();
            var compiler = new GovernedSqlCompiler(env.Engine, DialectCapabilityTable.Default, cache);

            var request = env.Request() with
            {
                BindPageOffset = true,
                EnforcedMaxRows = 501,
                PageOffset = 250,
                MaxAllowedOffset = 10_000
            };

            var compiled = compiler.Compile(rawSql.AsMemory(), request, CancellationToken.None);
            compiled.Parameters.Any(p => p.Origin == ParameterOrigin.PageOffset).ShouldBeTrue();
            var pageSlot = compiled.Parameters.Single(p => p.Origin == ParameterOrigin.PageOffset);
            pageSlot.Value.ShouldBe(250L);

            switch (dialect)
            {
                case TargetSqlDialect.SqlServer:
                    compiled.Sql.ShouldContain("OFFSET");
                    compiled.Sql.ShouldContain("FETCH NEXT 501 ROWS ONLY");
                    break;
                case TargetSqlDialect.Oracle:
                    compiled.Sql.ShouldContain("OFFSET");
                    compiled.Sql.ShouldContain("FETCH NEXT 501 ROWS ONLY");
                    break;
                case TargetSqlDialect.PostgreSql:
                case TargetSqlDialect.DuckDb:
                case TargetSqlDialect.Databricks:
                    compiled.Sql.ShouldContain("LIMIT 501 OFFSET");
                    break;
            }
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Dml_WithPage_Unsupported()
    {
        var (service, _, _) = CreatePipeline();
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");

        var request = new GovernedSqlQueryRequest(
            "UPDATE crm.customers SET name = 'Bob' WHERE id = 1",
            Page: new SqlPageWindow(0, 100, 1));

        await Should.ThrowAsync<SqlPagingUnsupportedException>(async () =>
            await service.ExecuteQueryBufferedAsync(request, user, tenant));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task OffsetAboveMax_NoNext()
    {
        var (service, _, _) = CreatePipeline(new WebSqlPagingOptions
        {
            Enabled = true,
            MaxOffset = 100,
            TokenLifetimeMinutes = 15
        }, fakeRows: 100);
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");

        var request = new GovernedSqlQueryRequest(
            "SELECT id, name FROM crm.customers ORDER BY id",
            Page: new SqlPageWindow(80, 50, 2));

        var result = await service.ExecuteQueryBufferedAsync(request, user, tenant);

        result.PagingUnavailable.ShouldBe("offset_limit");
        result.Next.ShouldBeNull();
    }

    [Fact]
    public async Task WithoutOrderBy_FirstPageWithoutNext()
    {
        var (service, _, _) = CreatePipeline();
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");

        var request = new GovernedSqlQueryRequest(
            "SELECT id FROM crm.customers",
            Page: new SqlPageWindow(0, 100, 1));

        var result = await service.ExecuteQueryBufferedAsync(request, user, tenant);

        result.PagingUnavailable.ShouldBe("order_by_required");
        result.Next.ShouldBeNull();
    }

    [Fact]
    public async Task RootLimit_PagingUnavailable()
    {
        var (service, _, _) = CreatePipeline();
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");

        var request = new GovernedSqlQueryRequest(
            "SELECT id FROM crm.customers ORDER BY id LIMIT 50",
            Page: new SqlPageWindow(0, 100, 1));

        var result = await service.ExecuteQueryBufferedAsync(request, user, tenant);

        result.PagingUnavailable.ShouldBe("statement_has_limit");
        result.Next.ShouldBeNull();
    }

    [Fact]
    public async Task LastPage_HasNoNext()
    {
        var (service, _, _) = CreatePipeline();
        var user = CreateUser();
        var tenant = new TenantId("tenant-1");

        var request = new GovernedSqlQueryRequest(
            "SELECT id FROM crm.customers ORDER BY id",
            Page: new SqlPageWindow(0, 1000, 1));

        var result = await service.ExecuteQueryBufferedAsync(request, user, tenant);

        // Synthetic reader produces fewer than 1000 rows, so HasMore is false -> Next is null
        result.Next.ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void TrinoRoute_WithPage_Unsupported()
    {
        var request = new GovernedSqlQueryRequest(
            "SELECT id FROM crm.customers ORDER BY id",
            Transport: "Trino",
            Page: new SqlPageWindow(0, 100, 1));

        request.Transport.ShouldBe("Trino");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void PagingOptions_ValidatedAtStart()
    {
        var options = new WebSqlPagingOptions
        {
            Enabled = true,
            MaxOffset = 10_000,
            TokenLifetimeMinutes = 15
        };

        options.Enabled.ShouldBeTrue();
        options.MaxOffset.ShouldBe(10_000);
        options.TokenLifetimeMinutes.ShouldBe(15);
    }
}
