namespace Autheris.Tests.Unit.VirtualFilters.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;
using static Autheris.Tests.Unit.VirtualFilters.Federation.FederatedVfPipelineHarness;

/// <summary>
/// F-GOV-14 WI-18 (SEC-1): access decisions in the WebSQL cross-source path are keyed by the full, source-qualified
/// table name. Two tables of different sources with the same schema and table name keep their own decisions, and a
/// table without a decision is denied instead of being read unrestricted.
/// </summary>
public sealed class FederatedDecisionKeyTests
{
    private const string AliceSid = "S-1-5-21-VF-ALICE";

    private static readonly TableMetadata CrmCustomers = HttpTable("crm_api", "public", "customers", "id", "name", "status");
    private static readonly TableMetadata ErpCustomers = SqlTable("erp", "public", "customers", "id", "credit");

    private const string HttpFirst =
        "SELECT c.id, c.status, e.credit FROM crm_api.public.customers c JOIN erp.public.customers e ON c.id = e.id";

    private const string SqlFirst =
        "SELECT c.id, c.status, e.credit FROM erp.public.customers e JOIN crm_api.public.customers c ON c.id = e.id";

    private static Consent Allow(TableIdentifier table, params ConsentRowFilter[] filters) => new()
    {
        TableIdentifier = table,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid(AliceSid),
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = filters
    };

    private static ConsentRowFilter StatusOpen() => new() { ColumnName = "status", Operator = "EQ", ValueType = "string", ValueJson = "\"OPEN\"" };

    private static IConsentRepository Consents(params Consent[] consents)
    {
        var repo = Substitute.For<IConsentRepository>();
        repo.GetActiveConsentsForSubjectsAsync(Arg.Any<IEnumerable<Sid>>(), Arg.Any<TableIdentifier>(), Arg.Any<DateTimeOffset>(), Arg.Any<TenantId?>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.TableIdentifier == ci.ArgAt<TableIdentifier>(1)).ToList()));
        return repo;
    }

    private static VirtualFilterSnapshot VirtualFilterOnCrmCustomers()
    {
        var filter = VirtualFilterModelTests.DavidFilter("vf_customers") with { TenantId = Tenant };
        var profile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "alice",
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(AliceSid),
            Scope = "crm_api.*.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings =
            [
                new FilterBinding
                {
                    FilterName = "vf_customers",
                    TargetPattern = "crm_api.public.customers",
                    ColumnMap = new Dictionary<string, string> { ["client_id"] = "id" }
                }
            ]
        };
        return new VirtualFilterSnapshot(1, [filter], [profile]);
    }

    private sealed class Pipeline
    {
        public required FederatedDuckDbExecutionService Service { get; init; }
        public required StubTableConnector Crm { get; init; }
        public required StubTableConnector Erp { get; init; }
    }

    private static Pipeline Build(IConsentRepository consents, VirtualFilterSnapshot? snapshot = null)
    {
        var crm = new StubTableConnector("crm_api",
        [
            Row(("id", "1"), ("name", "Alpha"), ("status", "OPEN")),
            Row(("id", "2"), ("name", "Beta"), ("status", "CLOSED")),
            Row(("id", "3"), ("name", "Gamma"), ("status", "OPEN"))
        ]);
        var erp = new StubTableConnector("erp",
        [
            Row(("id", "1"), ("credit", "100")),
            Row(("id", "2"), ("credit", "200")),
            Row(("id", "3"), ("credit", "300"))
        ]);

        var tables = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(CrmCustomers.Identifier, Arg.Any<CancellationToken>()).Returns(CrmCustomers);
        tables.GetTableMetadataAsync(ErpCustomers.Identifier, Arg.Any<CancellationToken>()).Returns(ErpCustomers);

        var options = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                Enabled = true,
                DefaultDataSourceName = "erp",
                AllowedDataSources = ["crm_api", "erp"],
                CrossSource = new CrossSourceOptions { Enabled = true, AllowedTransports = ["WebSql"] }
            },
            DuckDbOlap = new DuckDbOlapOptions { Enabled = true, MaxMemory = "256MB", MaxTempDirectorySize = "0B" }
        };

        var resolver = new MandatoryRowFilterResolver(
            new MandatoryRowFilterResolverTests.FixedSnapshot(snapshot ?? new VirtualFilterSnapshot(1, [], [])),
            new MandatoryRowFilterResolverTests.PlaceholderPredicates());

        var service = new FederatedDuckDbExecutionService(
            Options.Create(options),
            Substitute.For<IAuditLogRepository>(),
            new CrossSourceQueryRouter(tables, options.WebSql.CrossSource, options.WebSql),
            new CrossSourcePlanner(FastSqlEngine.Default),
            Staging(StubRegistry.For((CrmCustomers, crm), (ErpCustomers, erp)), options),
            new DuckDbOlapEngine(options.DuckDbOlap, NullLogger<DuckDbOlapEngine>.Instance),
            sqlEngine: FastSqlEngine.Default,
            consentResolution: new ConsentResolutionService(),
            consentRepository: consents,
            mandatoryFilters: resolver);

        return new Pipeline { Service = service, Crm = crm, Erp = erp };
    }

    private static async Task<List<string>> RunAsync(Pipeline pipeline, string sql)
    {
        var result = await pipeline.Service.ExecuteQueryBufferedAsync(new GovernedSqlQueryRequest(sql, Transport: "WebSql"), User(AliceSid), Tenant);
        return result.Rows
            .Select(r => $"{r["id"]}|{r["status"]}|{r["credit"]}")
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
    }

    [Theory]
    [InlineData(HttpFirst)]
    [InlineData(SqlFirst)]
    public async Task SameSchemaAndTableName_InTwoSources_EachTableKeepsItsOwnRowFilter(string sql)
    {
        // ST-1 / ST-2: the unrestricted decision of erp.public.customers must not replace the row filter of the HTTP table.
        var pipeline = Build(Consents(Allow(CrmCustomers.Identifier, StatusOpen()), Allow(ErpCustomers.Identifier)));

        var rows = await RunAsync(pipeline, sql);

        rows.ShouldBe(["1|OPEN|100", "3|OPEN|300"]);
    }

    [Theory]
    [InlineData(HttpFirst)]
    [InlineData(SqlFirst)]
    public async Task SameSchemaAndTableName_VirtualFilterOnTheHttpTable_IsNeverDroppedByTheOtherDecision(string sql)
    {
        // ST-1 / ST-2: the HTTP table keeps its own decision with the applied Virtual Filter; until the gateway-side
        // enforcement exists that decision is denied (403), the rows are never returned unfiltered.
        var pipeline = Build(Consents(Allow(CrmCustomers.Identifier), Allow(ErpCustomers.Identifier)), VirtualFilterOnCrmCustomers());

        await Should.ThrowAsync<GatewayForbiddenException>(() => RunAsync(pipeline, sql));
    }
}
