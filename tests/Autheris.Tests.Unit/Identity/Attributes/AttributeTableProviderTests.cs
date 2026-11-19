namespace Autheris.Tests.Unit.Identity.Attributes;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using Microsoft.Extensions.Logging.Abstractions;
using static CallerAttributeTestSupport;

/// <summary>PLAN-GOV-129 test 6, ST-129-12 (BC-129-03, BC-129-09, BC-129-11): unit contract of the privileged attribute-table lookup.</summary>
[Trait("Category", "Security")]
public sealed class AttributeTableProviderTests
{
    private readonly List<(string Table, string Oid, object? Value, string? Tenant, string? Issuer)> _rows = [];
    private readonly List<string> _logs = [];

    private void Row(string oid, object value, string tenant = "tenant_a") =>
        _rows.Add(("person_attributes", oid, value, tenant, "iss"));

    private void GlobalRow(string oid, object value, string issuer) =>
        _rows.Add(("global_attributes", oid, value, null, issuer));

    private DbDataReader ExecuteInMemoryQuery(string sql, IReadOnlyDictionary<string, object?> parameters)
    {
        if (sql.Contains("no_such_table_with_secret_name", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("relation \"no_such_table_with_secret_name\" does not exist");
        }

        static bool TryParam(IReadOnlyDictionary<string, object?> p, string name, out object? val) =>
            p.TryGetValue("@" + name, out val) || p.TryGetValue(name, out val);

        var table = sql.Contains("global_attributes", StringComparison.Ordinal) ? "global_attributes" : "person_attributes";
        TryParam(parameters, AttributeTableQuery.SubjectParameter, out var subjectObj);
        var hasTenant = TryParam(parameters, AttributeTableQuery.TenantParameter, out var tenantObj);
        var hasIssuer = TryParam(parameters, AttributeTableQuery.IssuerParameter, out var issuerObj);

        var matched = _rows
            .Where(r => r.Table == table
                && string.Equals(r.Oid, subjectObj as string, StringComparison.Ordinal)
                && (!hasTenant || string.Equals(r.Tenant, tenantObj as string, StringComparison.Ordinal))
                && (!hasIssuer || string.Equals(r.Issuer, issuerObj as string, StringComparison.Ordinal)));

        var cols = new List<string> { "cost_center" };
        if (hasTenant) cols.Add("tenant_id");
        if (hasIssuer) cols.Add("issuer");

        var projected = matched.Select(r =>
        {
            var row = new List<object?> { r.Value };
            if (hasTenant) row.Add(r.Tenant);
            if (hasIssuer) row.Add(r.Issuer);
            return row.ToArray();
        }).ToList();

        return new StubDbDataReader(cols, projected);
    }

    private AttributeTableProvider Provider(
        bool lookupAllowed = true, bool dedicated = true, bool acknowledged = false, string environment = "Production",
        IEnumerable<string>? exempt = null, IDbSessionContextInitializer? session = null, string provider = "PostgreSql")
    {
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            CallerAttributes = Options(),
            DataSources = new SqlDataSourceOptions
            {
                TenantColumnExemptTables = (exempt ?? []).ToList(),
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hr_db"] = new() { Provider = provider, ConnectionString = "ignored", CallerAttributeLookupAllowed = lookupAllowed, CallerAttributeDedicatedLogin = dedicated, CallerAttributeSharedLoginAcknowledged = acknowledged }
                }
            }
        });
        IHostEnvironment env = new TestHostEnvironment(environment);
        var logger = Substitute.For<ILogger<AttributeTableProvider>>();
        logger.When(l => l.Log(Arg.Any<LogLevel>(), Arg.Any<EventId>(), Arg.Any<object>(), Arg.Any<Exception?>(), Arg.Any<Func<object, Exception?, string>>()))
            .Do(c => _logs.Add(c.ArgAt<object>(2).ToString()!));
        return new AttributeTableProvider(new CallbackFactory(ExecuteInMemoryQuery), options, env, logger, session);
    }

    private static CallerAttributeDefinition Definition(string table = "person_attributes", string? tenant = "tenant_id", string? issuer = null, int max = 5, string schema = "", CallerAttributeCardinality cardinality = CallerAttributeCardinality.List)
    {
        var config = new List<(string, string)>
        {
            ("dataSource", "hr_db"), ("table", table), ("subjectKeyKind", "EntraObjectId"), ("subjectKeyColumn", "person_oid"), ("valueColumn", "cost_center")
        };
        if (schema.Length > 0) config.Add(("schema", schema));
        if (tenant is not null) config.Add(("tenantColumn", tenant));
        if (issuer is not null) config.Add(("issuerColumn", issuer));
        return CallerAttributeTestSupport.Definition("cost_center", "attribute-table", CallerAttributeType.Text, cardinality, max, 60, config.ToArray());
    }

    private static string[] Values(CallerAttributeResult r) => Enumerable.Range(0, r.Value!.Count).Select(i => (string)r.Value[i]).ToArray();

    [Fact]
    public async Task Resolves_TheValuesOfTheSubject_Only()
    {
        Row(UserOid, "100");
        Row(UserOid, "200");
        Row(OtherOid, "999");

        var result = await Provider().ResolveAsync(UserSubject(), Definition(), default);

        result.Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
        Values(result).Order().ShouldBe(["100", "200"]);
    }

    [Fact]
    public async Task ST_129_12_TwoTenants_WithTheSameSubjectKey_NeverMix()
    {
        Row(UserOid, "100", "tenant_a");
        Row(UserOid, "777", "tenant_b");

        var result = await Provider().ResolveAsync(UserSubject(), Definition(), default);

        Values(result).ShouldBe(["100"]);
    }

    [Fact]
    public async Task ST_129_12_TenantIdsDifferingOnlyByCase_ProduceNoMatch_WhateverTheCollation()
    {
        Row(UserOid, "100", "TENANT_A");   // SQLite = is case sensitive, SQL Server default collation is not: the provider compares ordinally too

        var result = await Provider().ResolveAsync(UserSubject(), Definition(), default);

        result.Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ST_129_12b_RowOfAnotherTenantCase_ReturnedDespiteTheWhere_FailsClosedAsOverCardinality_BC_129_11()
    {
        // Defence in depth: a source that ignores the WHERE clause (collation quirk) returns a differently-cased tenant row.
        var options = Microsoft.Extensions.Options.Options.Create(new GatewayOptions
        {
            CallerAttributes = Options(),
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["hr_db"] = new() { Provider = "PostgreSql", ConnectionString = "ignored", CallerAttributeLookupAllowed = true, CallerAttributeDedicatedLogin = true }
                }
            }
        });
        IHostEnvironment env = new TestHostEnvironment("Production");
        var factory = new CallbackFactory((_, _) => new StubDbDataReader(["cost_center", "tenant_id"], [["leak", "TENANT_A"]]));
        var provider = new AttributeTableProvider(factory, options, env, NullLogger<AttributeTableProvider>.Instance, null);

        var result = await provider.ResolveAsync(UserSubject(), Definition(), default);

        result.Outcome.ShouldBe(CallerAttributeOutcome.OverCardinality);
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task NoMatchingRow_IsMissing()
    {
        (await Provider().ResolveAsync(UserSubject(), Definition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task ApplicationSubject_AndSubjectsWithoutTheKey_AreMissing()
    {
        Row(UserOid, "100");
        var app = CallerSubjectFactory.Create(Principal(Bearer, [("idtyp", "app"), ("appid", "aaaaaaaa-0000-0000-0000-000000000001")]), Tenant, Options());
        (await Provider().ResolveAsync(app, Definition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
        (await Provider().ResolveAsync(CallerSubject.NoSubject(Tenant), Definition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Missing);
    }

    [Fact]
    public async Task OverTheCap_IsOverCardinality_NeverTruncated()
    {
        for (int i = 0; i < 6; i++) Row(UserOid, $"v{i}");
        var result = await Provider().ResolveAsync(UserSubject(), Definition(max: 5), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.OverCardinality);
        result.Value.ShouldBeNull();
    }

    [Fact]
    public async Task ExactlyTheCap_Resolves()
    {
        for (int i = 0; i < 5; i++) Row(UserOid, $"v{i}");
        (await Provider().ResolveAsync(UserSubject(), Definition(max: 5), default)).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
    }

    [Fact]
    public async Task ST_129_12_LookupNotAllowedOnTheConnection_IsFailed()
    {
        Row(UserOid, "100");
        var result = await Provider(lookupAllowed: false).ResolveAsync(UserSubject(), Definition(), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        result.ReasonCode.ShouldBe("SourceNotAllowed");
    }

    [Fact]
    public async Task ST_129_12_SharedLogin_IsRefused_OutsideDevelopment_EvenWhenAcknowledged()
    {
        Row(UserOid, "100");
        (await Provider(dedicated: false, acknowledged: true, environment: "Production").ResolveAsync(UserSubject(), Definition(), default)).ReasonCode.ShouldBe("SourceNotAllowed");
        (await Provider(dedicated: false, acknowledged: false, environment: "Development").ResolveAsync(UserSubject(), Definition(), default)).ReasonCode.ShouldBe("SourceNotAllowed");
        (await Provider(dedicated: false, acknowledged: true, environment: "Development").ResolveAsync(UserSubject(), Definition(), default)).Outcome.ShouldBe(CallerAttributeOutcome.Resolved);
    }

    [Fact]
    public async Task ST_129_12_UnsupportedDialect_IsFailed()
    {
        var result = await Provider(provider: "Oracle").ResolveAsync(UserSubject(), Definition(), default);
        result.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        result.ReasonCode.ShouldBe("SourceNotAllowed");
    }

    [Fact]
    public async Task ST_129_12_TenantExemptTable_WithoutIssuerColumn_IsRefused()
    {
        GlobalRow(UserOid, "100", TrustedIssuer);
        var withoutIssuer = await Provider(exempt: ["global_attributes"]).ResolveAsync(UserSubject(), Definition("global_attributes", tenant: null), default);
        withoutIssuer.Outcome.ShouldBe(CallerAttributeOutcome.Failed);
        withoutIssuer.ReasonCode.ShouldBe("TenantColumnRequired");
    }

    [Fact]
    public async Task TenantExemptTable_WithAnIssuerColumn_IsReadByIssuer()
    {
        GlobalRow(UserOid, "100", TrustedIssuer);
        GlobalRow(UserOid, "555", "https://other.example/");
        var result = await Provider(exempt: ["global_attributes"]).ResolveAsync(UserSubject(), Definition("global_attributes", tenant: null, issuer: "issuer"), default);
        Values(result).ShouldBe(["100"]);
    }

    [Fact]
    public async Task TableWithoutTenantColumn_ThatIsNotExempt_IsRefused()
    {
        GlobalRow(UserOid, "100", TrustedIssuer);
        var result = await Provider().ResolveAsync(UserSubject(), Definition("global_attributes", tenant: null, issuer: "issuer"), default);
        result.ReasonCode.ShouldBe("TenantColumnRequired");
    }

    [Fact]
    public async Task DatabaseError_IsFailed_AndTheMessageNeverReachesTheLog()
    {
        var def = Definition("no_such_table_with_secret_name");
        var provider = Provider();
        var ex = await Should.ThrowAsync<CallerAttributeSourceException>(() => provider.ResolveAsync(UserSubject(), def, default).AsTask());
        ex.Message.ShouldNotContain("no_such_table");
        ex.InnerException.ShouldNotBeNull();
        _logs.ShouldAllBe(l => !l.Contains("no_such_table") && !l.Contains(UserOid));
    }

    [Fact]
    public async Task ST_129_12_TheSessionCarriesTheTenantOnly()
    {
        Row(UserOid, "100");
        var session = Substitute.For<IDbSessionContextInitializer>();
        await Provider(session: session).ResolveAsync(UserSubject(), Definition(), default);
        await session.Received(1).InitializeSessionAsync(
            Arg.Any<DbConnection>(), Arg.Any<DbTransaction?>(), DatabaseDialect.PostgreSql, Tenant, null, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NumericValues_AreReturnedAsInvariantStrings()
    {
        Row(UserOid, 42);
        var result = await Provider().ResolveAsync(UserSubject(), Definition(), default);
        Values(result).ShouldBe(["42"]);
    }

    [Fact]
    public async Task KeyValue_WithQuotes_IsBound_NotInterpolated()
    {
        Row("x' OR '1'='1", "leak");
        var subject = new CallerSubject(Tenant, CallerSubjectKind.User, null, [], TrustedIssuer, Bearer,
            System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new Dictionary<SubjectKeyKind, string> { [SubjectKeyKind.EntraObjectId] = "x' OR '1'='1" }));
        var result = await Provider().ResolveAsync(subject, Definition(), default);
        Values(result).ShouldBe(["leak"]);   // matches the literal key only
        Row(UserOid, "mine");
        (await Provider().ResolveAsync(UserSubject(), Definition(), default)).Value!.Count.ShouldBe(1);
    }

    private sealed class CallbackFactory(Func<string, IReadOnlyDictionary<string, object?>, DbDataReader> execute) : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default) =>
            Task.FromResult<DbConnection>(new CallbackDbConnection(execute));
    }
}

/// <summary>Save-time validation of attribute-table definitions (BC-129-03, BC-129-11, F-9).</summary>
public sealed class AttributeTableDefinitionValidatorTests
{
    private static AttributeTableDefinitionValidator Validator(bool lookup = true, string[]? exempt = null) => new(Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        DataSources = new SqlDataSourceOptions
        {
            TenantColumnExemptTables = (exempt ?? []).ToList(),
            Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
            {
                ["hr_db"] = new() { Provider = "PostgreSql", ConnectionString = "x", CallerAttributeLookupAllowed = lookup }
            }
        }
    }));

    private static CallerAttributeDefinition Def(params (string, string)[] config) =>
        CallerAttributeTestSupport.Definition("cost_center", "attribute-table", config: config);

    private static readonly (string, string)[] Valid =
    [
        ("dataSource", "hr_db"), ("table", "person_attributes"), ("subjectKeyKind", "EntraObjectId"), ("subjectKeyColumn", "person_oid"), ("valueColumn", "cost_center"), ("tenantColumn", "tenant_id")
    ];

    [Fact]
    public void ValidDefinition_Passes() => Validator().Validate(Def(Valid));

    [Fact]
    public void LookupNotAllowed_IsRefused() =>
        Should.Throw<CallerAttributeAdminException>(() => Validator(lookup: false).Validate(Def(Valid)));

    [Fact]
    public void UnknownDataSource_IsRefused() =>
        Should.Throw<CallerAttributeAdminException>(() => Validator().Validate(Def(Valid.Select(c => c.Item1 == "dataSource" ? ("dataSource", "nope") : c).ToArray())));

    [Fact]
    public void NoTenantColumn_IsRefused_ExemptNeedsAnIssuerColumn()
    {
        var withoutTenant = Valid.Where(c => c.Item1 != "tenantColumn").ToArray();
        Should.Throw<CallerAttributeAdminException>(() => Validator().Validate(Def(withoutTenant)));
        Should.Throw<CallerAttributeAdminException>(() => Validator(exempt: ["person_attributes"]).Validate(Def(withoutTenant)));
        Validator(exempt: ["person_attributes"]).Validate(Def([.. withoutTenant, ("issuerColumn", "issuer")]));
    }

    [Fact]
    public void UnsafeIdentifier_IsRefused() =>
        Should.Throw<CallerAttributeAdminException>(() => Validator().Validate(Def(Valid.Select(c => c.Item1 == "table" ? ("table", "#t") : c).ToArray())));
}
