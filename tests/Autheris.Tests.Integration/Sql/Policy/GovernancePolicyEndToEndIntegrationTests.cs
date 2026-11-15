namespace Autheris.Tests.Integration.Sql.Policy;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Microsoft.Extensions.Options;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// WP-X0l end to end against real PostgreSQL: the live decision producer (consent resolution, typed row policy,
/// typed masks) feeds the assembly, and <c>ISqlEngine.Compile</c> + <see cref="PostgreSqlCompiledSqlBinder"/> execute
/// against PostgreSQL.
/// </summary>
public sealed class GovernancePolicyEndToEndIntegrationTests
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly Sid User = new("S-1");
    private static readonly TableIdentifier OrdersId = new("d", "public", "orders");
    private static readonly TableIdentity Orders = new("public", "orders");

    private static readonly TableMetadata Meta = new()
    {
        Identifier = OrdersId,
        Table = new Table { SourceName = "ds", SchemaName = "public", TableName = "orders", SourceType = "PostgreSQL", IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "INTEGER" },
            new TableColumn { ColumnName = "tenant_id", DataType = "TEXT" },
            new TableColumn { ColumnName = "region", DataType = "TEXT" },
            new TableColumn { ColumnName = "email", DataType = "TEXT" },
            new TableColumn { ColumnName = "ssn", DataType = "TEXT" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["email"] = new() { RuleType = "REDACT" } }
    };

    private static Consent EuConsent() => new()
    {
        TableIdentifier = OrdersId,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = User,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = [new ConsentRowFilter { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EU\"" }],
        ColumnRules =
        [
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "tenant_id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask },
            new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Deny }
        ]
    };

    private static async Task<CompiledSql> CompileAsync(string sql, params Consent[] consents)
    {
        var repo = new StubConsentRepository(consents);
        var accessPolicy = new TableAccessPolicy(repo, new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance, new GatewayOptions(), NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()),
            maskPolicyFactory: new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(Options.Create(new GatewayOptions()))));
        var decision = await accessPolicy.DecideAsync(
            new TableAccessQuery(User, Tenant, new HashSet<Sid>(), new HashSet<string>(), Meta), CancellationToken.None);

        var catalog = new InMemoryTableCatalog([new TableCatalogEntry(Orders, ImmutableArray.Create(
            new CatalogColumn("id", "INTEGER"), new CatalogColumn("tenant_id", "TEXT"), new CatalogColumn("region", "TEXT"),
            new CatalogColumn("email", "TEXT"), new CatalogColumn("ssn", "TEXT")), "tenant_id", 1)], "public");
        var provider = new StubCatalogProvider(catalog);

        var policy = await new GovernancePolicyFactory(provider, new StubTableMetadataRepository(), Options.Create(new GatewayOptions())).CreateAsync(
            new GovernedPolicyInput("ds", Tenant, new Dictionary<TableIdentity, TableAccessDecision> { [Orders] = decision },
                new HashSet<TableIdentity>(), GovernedSubqueryStrategy.Correlated)
            { Metadata = new Dictionary<TableIdentity, TableMetadata> { [Orders] = Meta } },
            CancellationToken.None);
        var request = CompileRequestFactory.Create(policy, TargetSqlDialect.PostgreSql, new WebSqlOptions(), isDml: false, 100, new HashSet<string>());

        return new FastSqlEngine().Compile(sql.AsMemory(), request, CancellationToken.None);
    }

    [RequiresContainerRuntimeFact]
    public async Task ConsentRowFilter_TenantIsolation_MaskAndDeny_AreAllEnforcedFromOneAssembledPolicy()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable)
        {
            return;
        }

        var compiled = await CompileAsync("SELECT id, email, ssn FROM orders ORDER BY id", EuConsent());
        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE public.orders (id integer, tenant_id text, region text, email text, ssn text);
                INSERT INTO public.orders VALUES
                    (1, 'acme', 'EU', 'alice@x.example', '111'),
                    (2, 'acme', 'US', 'bob@x.example', '222'),
                    (3, 'other', 'EU', 'carol@x.example', '333'),
                    (4, 'ACME', 'EU', 'dave@x.example', '444');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        new PostgreSqlCompiledSqlBinder().Bind(command, compiled, new Dictionary<string, object?>());
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object[reader.FieldCount];
            reader.GetValues(row);
            rows.Add(row.Select(v => v is DBNull ? null : v).ToArray());
        }

        // Only the acme/EU row: the US row by the consent filter, 'other' and 'ACME' by the exact tenant comparison.
        rows.Count.ShouldBe(1);
        Convert.ToInt32(rows[0][0], System.Globalization.CultureInfo.InvariantCulture).ShouldBe(1);
        rows[0][1].ShouldBe("***");
        rows[0][2].ShouldBeNull();
    }

    [Fact]
    public async Task NoConsent_DeniesTheTable()
    {
        await Should.ThrowAsync<SecurityException>(() => CompileAsync("SELECT id FROM orders"));
    }

    private sealed class StubConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default)
            => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default)
            => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult(consents);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default)
            => Task.FromResult(false);
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default)
            => Task.FromResult(new Autheris.Application.Governance.Paging.ConsentPage(consents, null));
        public Task ReplaceConsentsAsync(
            IReadOnlyList<Guid> revokeConsentIds,
            IReadOnlyList<Consent> create,
            Sid actor,
            string reason,
            ConsentRevocationAuthority authority,
            IReadOnlyList<AuditLogEntry>? auditEntries = null,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class StubCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct = default) =>
            new(catalog);
    }

    private sealed class StubTableMetadataRepository : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult<TableMetadata?>(null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }
}
