namespace Autheris.Tests.Integration.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Policy;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Unit.Identity.Attributes;
using Npgsql;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>PLAN-GOV-129 end-to-end PostgreSQL execution of caller-attribute row-filter policy per user.</summary>
[Trait("Category", "Security")]
public sealed class TableAccessPolicyCallerAttributeIntegrationTests
{
    private static readonly TenantId Tenant = new("acme");
    private static readonly TableIdentifier Orders = new("d", "public", "orders");
    private static readonly Sid AnalystGroup = new("S-analyst-group");
    private const string OidA = "11111111-1111-1111-1111-111111111111";
    private const string OidB = "22222222-2222-2222-2222-222222222222";
    private const string OidC = "33333333-3333-3333-3333-333333333333";

    private static TableMetadata Meta() => new()
    {
        Identifier = Orders,
        Table = new Table { SourceName = "ds", SchemaName = "public", TableName = "orders", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
        Columns = [new TableColumn { ColumnName = "cost_center", DataType = "TEXT" }, new TableColumn { ColumnName = "id", DataType = "INTEGER" }]
    };

    private static ClaimsPrincipal Person(string oid) => CallerAttributeTestSupport.Principal(CallerAttributeTestSupport.Bearer, [("oid", oid)]);

    [RequiresContainerRuntimeFact]
    public async Task ST_129_Postgres_EndToEnd_EachPersonSeesOnlyTheirRows()
    {
        await using var db = await PostgreSqlTestDatabase.CreateAsync();
        if (!db.IsAvailable) return;

        await using var connection = new NpgsqlConnection(db.ConnectionString);
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE public.orders (id INTEGER, cost_center TEXT);
                INSERT INTO public.orders VALUES (1, 'CC100'), (2, 'CC200'), (3, 'CC100'), (4, 'CC300');
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var def = CallerAttributeTestSupport.Definition("cost_center", "claims", CallerAttributeType.Text, CallerAttributeCardinality.List, 50, 60) with { Tenant = Tenant };
        using var resolver = new CallerAttributeResolver(
            new StaticRegistry(def),
            [new MapProvider(new Dictionary<string, string[]> { [OidA] = ["CC100"], [OidB] = ["CC200"] })],
            Microsoft.Extensions.Options.Options.Create(new GatewayOptions { CallerAttributes = CallerAttributeTestSupport.Options() }));
        var consent = new Consent
        {
            TableIdentifier = Orders,
            TenantId = Tenant,
            GranteeType = GranteeType.Group,
            GranteeSid = AnalystGroup,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            RowFilters = [new ConsentRowFilter { ColumnName = "cost_center", Operator = "EQ", ValueJson = "[]", ValueSource = "USER_ATTRIBUTE", UserAttribute = "cost_center" }]
        };
        var policy = new TableAccessPolicy(
            new StaticConsentRepository(consent),
            new ConsentResolutionService(),
            Autheris.Application.Policy.Services.NullConsentCacheService.Instance,
            new GatewayOptions(),
            NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()),
            callerAttributes: resolver);

        async Task<int[]> Rows(string oid)
        {
            var decision = await policy.DecideAsync(
                new TableAccessQuery(new Sid(oid), Tenant, new HashSet<Sid> { AnalystGroup }, new HashSet<string>(), Meta(), Principal: Person(oid)),
                CancellationToken.None);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"SELECT id FROM public.orders AS {RowFilterAliases.Target} WHERE " + decision.CombinedRowFilterSql;
            foreach (var (name, value) in decision.RowFilterParameters ?? new Dictionary<string, object?>())
            {
                cmd.Parameters.AddWithValue(name.TrimStart('@'), value ?? DBNull.Value);
            }

            var ids = new List<int>();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) ids.Add(reader.GetInt32(0));
            return ids.Order().ToArray();
        }

        (await Rows(OidA)).ShouldBe([1, 3]);
        (await Rows(OidB)).ShouldBe([2]);
        (await Rows(OidC)).ShouldBeEmpty();
    }

    private sealed class MapProvider(Dictionary<string, string[]> byOid) : ICallerAttributeProvider
    {
        public string Kind => "claims";

        public ValueTask<CallerAttributeResult> ResolveAsync(CallerSubject subject, CallerAttributeDefinition definition, CancellationToken ct)
        {
            var oid = subject.KeyFor(SubjectKeyKind.EntraObjectId);
            return ValueTask.FromResult(oid is not null && byOid.TryGetValue(oid, out var values)
                ? CallerAttributeResult.Resolved(new CallerAttributeValue(values.Cast<object>().ToList(), SqlParameterType.String))
                : CallerAttributeResult.Missing());
        }
    }

    private sealed class StaticRegistry(CallerAttributeDefinition definition) : ICallerAttributeRegistry
    {
        public ValueTask<CallerAttributeSnapshot> GetAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(new CallerAttributeSnapshot(1, [definition], []));

        public void Invalidate() { }
    }

    private sealed class StaticConsentRepository(Consent consent) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([consent]);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([consent]);

        public Task<Consent> CreateConsentAsync(Consent item, CancellationToken ct = default) => Task.FromResult(item);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new ConsentPage([consent], null));
    }
}
