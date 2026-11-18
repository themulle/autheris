namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WebSqlMaskedFlagTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Masked_flag_is_strictly_boolean_and_carries_no_classification_or_mask_function()
    {
        var tenant = new TenantId("tenant-1");
        var user = new Sid("S-1-5-21-USER-1");
        var tableId = new TableIdentifier("crm", "public", "patients");
        var ssnId = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = tableId,
            OwnerTenantId = tenant.Value,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "crm", SchemaName = "public", TableName = "patients", IsActive = true },
            Columns =
            [
                new TableColumn { Id = ssnId, ColumnName = "ssn", DataType = "varchar" },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "city", DataType = "varchar" }
            ],
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = [new ColumnClassification { Scope = ClassificationScopes.Global, TableColumnId = ssnId.ToString(), Code = ClassificationScopes.PiiSpecialCode, Source = "OWNER", AssignedAt = FixedNow }]
            }
        };

        var grant = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = tenant,
            TableIdentifier = tableId,
            GranteeType = GranteeType.User,
            GranteeSid = user,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(10),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Mask },
                new ConsentColumnRule { ColumnName = "city", AccessLevel = ColumnAccessLevel.Clear }
            ]
        };

        var classRepo = new TestInMemoryClassificationStore(tenant.Value);
        await classRepo.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
        {
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiSpecialCode,
            MaskFunction = MaskFunction.Hash,
            KeyRef = "test-hmac-key",
            Version = 1,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        });

        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var gwOpts = new GatewayOptions { WebSql = new WebSqlOptions { Enabled = true, DefaultDataSourceName = "crm" } };
        var maskProvider = new SqlDataMaskingProvider(Options.Create(gwOpts), new FakeClassificationSecretProvider(), timeProvider: time);
        var resolver = new ClassificationAccessResolver(maskProvider, classRepo, time);
        var rowFactory = new RowPolicyFactory(new PolicyPredicateRenderer());
        var policy = new TableAccessPolicy(
            new TestInMemoryConsentRepository([grant], filterWindow: false),
            new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance),
            new TestNoOpConsentCache(),
            gwOpts,
            new NoOpMandatoryRowFilterResolver(),
            rowPolicyFactory: rowFactory,
            maskPolicyFactory: new ColumnMaskPolicyFactory(maskProvider),
            rowScopeCompiler: new RowScopeCompiler(rowPolicyFactory: rowFactory, classificationResolver: resolver, timeProvider: time),
            timeProvider: time,
            classificationResolver: resolver);

        var decision = await policy.DecideAsync(new TableAccessQuery(user, tenant, new HashSet<Sid>(), new HashSet<string>(), meta), CancellationToken.None);
        decision.ColumnAccess["ssn"].ShouldBe(ColumnAccessLevel.Mask);
        decision.MaskedColumnsAudit.ShouldContain(m => m.Column == "ssn" && m.MaskFunction == "HASH");

        var sqlService = new FakeGovernedSqlService(
            [new SqlResultColumn("ssn", typeof(string)), new SqlResultColumn("city", typeof(string))],
            [decision.ColumnAccess["ssn"] != ColumnAccessLevel.Clear, decision.ColumnAccess["city"] != ColumnAccessLevel.Clear]);

        var httpContext = new DefaultHttpContext();
        httpContext.Items[SecurityPrincipalContext.TenantIdItemKey] = tenant;
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, user.Value),
            new Claim("sub", user.Value),
            new Claim("tenant_id", tenant.Value)
        ], "TestAuth"));
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new WebSqlEndpoints.WebSqlRequestDto("SELECT ssn, city FROM patients", null, "crm"))));
        httpContext.Response.Body = new MemoryStream();

        await WebSqlEndpoints.HandleWebSqlRequest(httpContext, sqlService, Options.Create(gwOpts), NullLoggerFactory.Instance);

        httpContext.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseJson = await new StreamReader(httpContext.Response.Body).ReadToEndAsync();

        using var doc = JsonDocument.Parse(responseJson);
        var cols = doc.RootElement.GetProperty("columns");
        cols[0].GetProperty("name").GetString().ShouldBe("ssn");
        cols[0].GetProperty("masked").ValueKind.ShouldBe(JsonValueKind.True);
        cols[1].GetProperty("name").GetString().ShouldBe("city");
        cols[1].GetProperty("masked").ValueKind.ShouldBe(JsonValueKind.False);

        responseJson.ShouldNotContain("PII_SPECIAL");
        responseJson.ShouldNotContain("PII");
        responseJson.ShouldNotContain("HASH");
        responseJson.ShouldNotContain("maskFunction");
        responseJson.ShouldNotContain("classifications");
        responseJson.ShouldNotContain("keyRef");
        responseJson.ShouldNotContain("test-hmac-key");
    }

    private sealed class FakeGovernedSqlService(IReadOnlyList<SqlResultColumn> columns, IReadOnlyList<bool> masked) : IGovernedSqlExecutionService
    {
        public Task ExecuteGovernedQueryAsync(
            GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenant,
            Func<DbDataReader, GovernedResultInfo, CancellationToken, Task> streamResult, CancellationToken ct = default) =>
            streamResult(new EmptyTwoColumnReader(), new GovernedResultInfo(columns, masked.ToImmutableArray()), ct);

        public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class EmptyTwoColumnReader : DbDataReader
    {
        public override int FieldCount => 2;
        public override string GetName(int ordinal) => ordinal == 0 ? "ssn" : "city";
        public override Type GetFieldType(int ordinal) => typeof(string);
        public override string GetDataTypeName(int ordinal) => "varchar";
        public override Task<bool> ReadAsync(CancellationToken ct) => Task.FromResult(false);
        public override bool Read() => false;
        public override int Depth => 0;
        public override bool HasRows => false;
        public override bool IsClosed => false;
        public override int RecordsAffected => 0;
        public override object this[int ordinal] => string.Empty;
        public override object this[string name] => string.Empty;
        public override bool GetBoolean(int ordinal) => false;
        public override byte GetByte(int ordinal) => 0;
        public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length) => 0;
        public override char GetChar(int ordinal) => '\0';
        public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length) => 0;
        public override DateTime GetDateTime(int ordinal) => default;
        public override decimal GetDecimal(int ordinal) => 0;
        public override double GetDouble(int ordinal) => 0;
        public override IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
        public override float GetFloat(int ordinal) => 0;
        public override Guid GetGuid(int ordinal) => Guid.Empty;
        public override short GetInt16(int ordinal) => 0;
        public override int GetInt32(int ordinal) => 0;
        public override long GetInt64(int ordinal) => 0;
        public override int GetOrdinal(string name) => name == "ssn" ? 0 : 1;
        public override string GetString(int ordinal) => string.Empty;
        public override object GetValue(int ordinal) => string.Empty;
        public override int GetValues(object[] values) => 0;
        public override bool IsDBNull(int ordinal) => true;
        public override bool NextResult() => false;
    }
}
