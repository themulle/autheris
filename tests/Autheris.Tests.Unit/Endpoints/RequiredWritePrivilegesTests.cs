namespace Autheris.Tests.Unit.Endpoints;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class RequiredWritePrivilegesTests
{
    private const string Tenant = "tenant-a";
    private const string SecretReadConnection = "Host=secret-read-host;Password=super-secret-read-pw";
    private const string SecretWriteConnection = "Host=secret-write-host;Password=super-secret-write-pw";

    [Fact]
    public async Task NonAdminCaller_Receives403_WhileGovernanceAdminAndClusterAdminReceive200()
    {
        var opts = BuildOptions("PostgreSql");
        var tables = new FakeMetadataRepo([OrdersTable("public")]);
        var consents = new FakeConsentRepo([]);
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));

        var consumerResult = await AdminWritePrivilegesEndpoints.GetAsync(
            "crm",
            BuildContext("Consumer"),
            opts,
            tables,
            consents,
            time);
        var (consumerStatus, _) = await ExecuteResultAsync(consumerResult);
        consumerStatus.ShouldBe(StatusCodes.Status403Forbidden);

        var govAdminResult = await AdminWritePrivilegesEndpoints.GetAsync(
            "crm",
            BuildContext("GovernanceAdmin"),
            opts,
            tables,
            consents,
            time);
        var (govStatus, _) = await ExecuteResultAsync(govAdminResult);
        govStatus.ShouldBe(StatusCodes.Status200OK);

        var clusterAdminResult = await AdminWritePrivilegesEndpoints.GetAsync(
            "crm",
            BuildContext("ClusterAdmin"),
            opts,
            tables,
            consents,
            time);
        var (clusterStatus, _) = await ExecuteResultAsync(clusterAdminResult);
        // ClusterAdmin does not imply GovernanceAdmin; the route and handler require GovernanceAdmin.
        clusterStatus.ShouldBe(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task Report_ProducesLeastPrivilegeGrants_ExcludesRevokedAndExpiredConsents_AndNeverExposesConnections()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var opts = BuildOptions("PostgreSql");
        var ordersId = new TableIdentifier("crm", "sales", "orders");
        var auditNoteId = new TableIdentifier("crm", "sales", "notes");
        var tables = new FakeMetadataRepo([OrdersTable("sales", "orders"), OrdersTable("sales", "notes")]);

        var activeInsert = MakeConsent(
            ordersId,
            now,
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["amount", "status"], WriteRowScope.AllRows));

        var activeUpdate = MakeConsent(
            ordersId,
            now,
            new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["status"], WriteRowScope.OwnRows));

        var revokedDelete = MakeConsent(
            auditNoteId,
            now,
            new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows),
            isRevoked: true);

        var expiredInsert = MakeConsent(
            auditNoteId,
            now,
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["body"], WriteRowScope.AllRows),
            validToOverride: now.AddMinutes(-1));

        var consents = new FakeConsentRepo([activeInsert, activeUpdate, revokedDelete, expiredInsert]);

        IResult result = await AdminWritePrivilegesEndpoints.GetAsync(
            "crm",
            BuildContext("GovernanceAdmin"),
            opts,
            tables,
            consents,
            new FixedTimeProvider(now));

        var (status, body) = await ExecuteResultAsync(result);
        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldNotContain(SecretReadConnection);
        body.ShouldNotContain(SecretWriteConnection);
        body.ShouldNotContain("super-secret");
        body.ShouldNotContain("\"table\":\"notes\"", Case.Sensitive); // inactive table consents excluded

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("tables").EnumerateArray()
            .ShouldAllBe(t => t.GetProperty("table").GetString() != "notes");
        var grants = doc.RootElement.GetProperty("grantStatements").EnumerateArray().Select(e => e.GetString()!).ToList();
        grants.ShouldContain(g => g.Contains("GRANT INSERT (\"amount\", \"status\", \"tenant_id\") ON \"sales\".\"orders\"", StringComparison.Ordinal));
        grants.ShouldContain(g => g.Contains("GRANT UPDATE (\"status\") ON \"sales\".\"orders\"", StringComparison.Ordinal));
        grants.ShouldContain(g => g.Contains("GRANT SELECT (\"id\", \"tenant_id\") ON \"sales\".\"orders\"", StringComparison.Ordinal));
        grants.ShouldNotContain(g => g.Contains("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnknownDataSource_Returns404()
    {
        var opts = BuildOptions("PostgreSql");
        var result = await AdminWritePrivilegesEndpoints.GetAsync(
            "nonexistent",
            BuildContext("GovernanceAdmin"),
            opts,
            new FakeMetadataRepo([]),
            new FakeConsentRepo([]),
            new FixedTimeProvider(DateTimeOffset.UtcNow));

        var (status, _) = await ExecuteResultAsync(result);
        status.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task SqlServerDialect_UsesBracketedIdentifiers_AndTableLevelInsert()
    {
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var opts = BuildOptions("SqlServer");
        var ordersId = new TableIdentifier("crm", "dbo", "orders");
        var tables = new FakeMetadataRepo([OrdersTable("dbo")]);
        var consent = MakeConsent(
            ordersId,
            now,
            new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["amount"], WriteRowScope.AllRows),
            new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.OwnRows));

        IResult result = await AdminWritePrivilegesEndpoints.GetAsync(
            "crm",
            BuildContext("GovernanceAdmin"),
            opts,
            tables,
            new FakeConsentRepo([consent]),
            new FixedTimeProvider(now));

        var (status, body) = await ExecuteResultAsync(result);
        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldContain("GRANT INSERT ON [dbo].[orders]");
        body.ShouldContain("GRANT DELETE ON [dbo].[orders]");
        body.ShouldContain("GRANT SELECT ([id], [tenant_id]) ON [dbo].[orders]");
    }

    private static IOptions<GatewayOptions> BuildOptions(string provider) =>
        Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = true },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["crm"] = new DataSourceConnectionOptions
                    {
                        Provider = provider,
                        ConnectionString = SecretReadConnection,
                        WriteConnectionString = SecretWriteConnection
                    }
                }
            }
        });

    private static TableMetadata OrdersTable(string schema, string tableName = "orders") => new()
    {
        Identifier = new TableIdentifier("crm", schema, tableName),
        PrimaryKeyColumns = ["id"],
        Table = new Table { SourceName = "crm", SchemaName = schema, TableName = tableName, IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" },
            new TableColumn { ColumnName = "amount", DataType = "integer" },
            new TableColumn { ColumnName = "status", DataType = "varchar" }
        ]
    };

    private static Consent MakeConsent(
        TableIdentifier table,
        DateTimeOffset now,
        params WriteRule[] writeRules) =>
        MakeConsent(table, now, writeRules, isRevoked: false, validToOverride: null);

    private static Consent MakeConsent(
        TableIdentifier table,
        DateTimeOffset now,
        WriteRule rule,
        bool isRevoked = false,
        DateTimeOffset? validToOverride = null) =>
        MakeConsent(table, now, [rule], isRevoked, validToOverride);

    private static Consent MakeConsent(
        TableIdentifier table,
        DateTimeOffset now,
        WriteRule[] writeRules,
        bool isRevoked,
        DateTimeOffset? validToOverride) => new()
        {
            Id = Guid.NewGuid(),
            TenantId = new TenantId(Tenant),
            GranteeType = GranteeType.User,
            GranteeSid = new Sid("S-1-5-21-WRITER"),
            TableIdentifier = table,
            Effect = ConsentEffect.Allow,
            ValidFrom = now.AddDays(-2),
            ValidTo = validToOverride ?? now.AddDays(7),
            IsRevoked = isRevoked,
            WriteRules = writeRules
        };

    private static HttpContext BuildContext(string role) => new DefaultHttpContext
    {
        RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider(),
        User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.PrimarySid, "S-1-5-21-CALLER"),
                new Claim("tenant_id", Tenant),
                new Claim(ClaimTypes.Role, role)
            ],
            "TestAuth")),
        Response = { Body = new MemoryStream() }
    };

    private static async Task<(int Status, string Body)> ExecuteResultAsync(IResult result)
    {
        var ctx = BuildContext("GovernanceAdmin");
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class FakeMetadataRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier t, CancellationToken ct = default) => Task.FromResult(tables.FirstOrDefault(x => x.Identifier.Equals(t)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier t, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class FakeConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default)
        {
            var items = consents.Where(c => c.TenantId == query.Tenant).Take(query.EffectivePageSize).ToList();
            return Task.FromResult(new ConsentPage(items, null));
        }
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }
}
