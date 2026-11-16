namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Data.Common;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Olap;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Extensions.Lakehouse.Interfaces;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine.Governance;

internal sealed partial class CrossTransportParityHarness
{
    public static IReadOnlyList<ParityScenario> BuildScenarios()
    {
        var now = DateTimeOffset.UtcNow;
        var table = DefaultOrdersTable();
        var user = CompleteUser(AliceSid);
        var groupUser = CompleteUser(BobSid, GroupSid);
        var degradedUser = DegradedUser(BobSid, GroupSid);

        return
        [
            new(ParityScenarioId.S01_UserGrant, user, TenantA, table, [AllowUser(AliceSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(true)),
            new(ParityScenarioId.S02_GroupGrant, groupUser, TenantA, table, [AllowGroup(GroupSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(true)),
            new(ParityScenarioId.S03_RoleGrantIgnored, CompleteUser(BobSid, roles: ["Analyst"]), TenantA, table, [], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S04_DenyWinsOverAllow, groupUser, TenantA, table, [AllowUser(BobSid, now), DenyGroup(GroupSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S05_WriteConsentDoesNotGrantRead, user, TenantA, table, [WriteOnlyConsent(AliceSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S06_DegradedGroupsDeny, degradedUser, TenantA, table, [AllowGroup(GroupSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S07_ExpiredConsentDenied, user, TenantA, table, [AllowUser(AliceSid, now, validFrom: now.AddDays(-5), validTo: now.AddDays(-1))], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S08_RevokedConsentDenied, user, TenantA, table, [AllowUser(AliceSid, now, revoked: true)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S09_TenantMismatchDenied, user, TenantA, table, [AllowUser(AliceSid, now, tenant: TenantB)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S10_RowFilterApplied, user, TenantA, table, [AllowUser(AliceSid, now, rowFilters: [RegionFilter()])], [], VirtualFilterSnapshot.Empty, false, false, new(true, HasRowFilter: true)),
            new(ParityScenarioId.S11_MandatoryVirtualFilterApplied, user, TenantA, table, [AllowUser(AliceSid, now)], [], RegionMandatorySnapshot(), false, false, new(true, HasMandatoryFilter: true)),
            new(ParityScenarioId.S12_UnresolvedCallerAttribute, user, TenantA, table, [AllowUser(AliceSid, now, rowFilters: [AttributeFilter()])], [], VirtualFilterSnapshot.Empty, true, false, new(false)),
            new(ParityScenarioId.S13_StrictProfileMasking, user, TenantA, table, [AllowUser(AliceSid, now)], [StrictProfile(AliceSid, now)], VirtualFilterSnapshot.Empty, false, false, new(true, SsnMasked: true)),
            new(ParityScenarioId.S14_ColumnDenyRule, user, TenantA, table, [AllowUser(AliceSid, now, colRules: [SsnRule(ColumnAccessLevel.Deny)])], [], VirtualFilterSnapshot.Empty, false, false, new(true, SsnDenied: true)),
            new(ParityScenarioId.S15_IpRangeMismatchDenied, user, TenantA, table, [AllowUser(AliceSid, now)], [], VirtualFilterSnapshot.Empty, false, true, new(false)),
            new(ParityScenarioId.S16_RestrictedScopeDenied, user, TenantA, table, [AllowUser(AliceSid, now, colRules: [SsnScopeRule("Agent.Read", ColumnAccessLevel.Clear)])], [], VirtualFilterSnapshot.Empty, false, false, new(true, SsnDenied: true)),
            new(ParityScenarioId.S17_RestrictedScopeCleared, CompleteUser(AliceSid, scopes: ["Agent.Read"]), TenantA, table, [AllowUser(AliceSid, now, colRules: [SsnScopeRule("Agent.Read", ColumnAccessLevel.Clear)])], [], VirtualFilterSnapshot.Empty, false, false, new(true)),
            new(ParityScenarioId.S18_DelegatedEnforcementMode, user, TenantA, DelegatedTable(), [AllowUser(AliceSid, now, rowFilters: [RegionFilter()])], [], VirtualFilterSnapshot.Empty, false, false, new(true)),
            new(ParityScenarioId.S19_UnauthenticatedDenied, new ClaimsPrincipal(new ClaimsIdentity()), TenantA, table, [AllowUser(AliceSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S20_InvalidTenantClaimDenied, InvalidTenantUser(AliceSid), TenantA, table, [AllowUser(AliceSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S21_MissingUserSidDenied, MissingSidUser(), TenantA, table, [AllowUser(AliceSid, now)], [], VirtualFilterSnapshot.Empty, false, false, new(false)),
            new(ParityScenarioId.S22_ForeignTenantObject, user, TenantA, DefaultOrdersTable(), [AllowUser(AliceSid, now, tenant: TenantB)], [], VirtualFilterSnapshot.Empty, false, false, new(false))
        ];
    }

    private static TableMetadata DefaultOrdersTable() => new()
    {
        Identifier = OrdersId,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "default", SchemaName = "main", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
        TenantColumnName = "tenant_id",
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "ssn", DataType = "varchar", IsSensitive = true },
            new TableColumn { ColumnName = "tenant_id", DataType = "varchar" }
        ]
    };

    private static TableMetadata DelegatedTable() => new()
    {
        Identifier = OrdersId,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "default", SchemaName = "main", TableName = "orders", SourceType = "Databricks", EnforcementMode = EnforcementMode.Delegated, IsActive = true },
        TenantColumnName = "tenant_id",
        Columns = DefaultOrdersTable().Columns
    };

    private static ClaimsPrincipal CompleteUser(Sid sid, Sid? group = null, string[]? roles = null, string[]? scopes = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid.Value),
            new("sub", sid.Value),
            new("tenant_id", TenantA.Value),
            GroupResolutionClaims.CreateMarker(),
            GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
        };
        if (group.HasValue) claims.Add(new Claim(ClaimTypes.GroupSid, group.Value.Value, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline));
        if (roles != null) claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        if (scopes != null) claims.AddRange(scopes.Select(sc => new Claim("scp", sc)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal DegradedUser(Sid sid, Sid group) => new(new ClaimsIdentity(
    [
        new(ClaimTypes.PrimarySid, sid.Value),
        new("sub", sid.Value),
        new("tenant_id", TenantA.Value),
        GroupResolutionClaims.CreateMarker(),
        GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Degraded),
        new(ClaimTypes.GroupSid, group.Value, ClaimValueTypes.String, IdentityClaimIssuers.Pipeline)
    ], "Bearer"));

    private static ClaimsPrincipal InvalidTenantUser(Sid sid) => new(new ClaimsIdentity(
    [
        new(ClaimTypes.PrimarySid, sid.Value),
        new("sub", sid.Value),
        new("tenant_id", "invalid!tenant/name"),
        GroupResolutionClaims.CreateMarker(),
        GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
    ], "Bearer"));

    private static ClaimsPrincipal MissingSidUser() => new(new ClaimsIdentity(
    [
        new("tenant_id", TenantA.Value),
        GroupResolutionClaims.CreateMarker(),
        GroupResolutionClaims.CreateStatus(GroupResolutionStatus.Complete)
    ], "Bearer"));

    private static Consent AllowUser(Sid sid, DateTimeOffset now, DateTimeOffset? validFrom = null, DateTimeOffset? validTo = null,
        bool revoked = false, TenantId? tenant = null, IReadOnlyList<ConsentRowFilter>? rowFilters = null, IReadOnlyList<ConsentColumnRule>? colRules = null) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = tenant ?? TenantA,
        GranteeType = GranteeType.User,
        GranteeSid = sid,
        Effect = ConsentEffect.Allow,
        ValidFrom = validFrom ?? now.AddDays(-1),
        ValidTo = validTo ?? now.AddDays(30),
        IsRevoked = revoked,
        RowFilters = rowFilters ?? [],
        ColumnRules = colRules ?? []
    };

    private static Consent AllowGroup(Sid groupSid, DateTimeOffset now) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = TenantA,
        GranteeType = GranteeType.Group,
        GranteeSid = groupSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = now.AddDays(-1),
        ValidTo = now.AddDays(30)
    };

    private static Consent DenyGroup(Sid groupSid, DateTimeOffset now) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = TenantA,
        GranteeType = GranteeType.Group,
        GranteeSid = groupSid,
        Effect = ConsentEffect.Deny,
        ValidFrom = now.AddDays(-1),
        ValidTo = now.AddDays(30)
    };

    private static Consent WriteOnlyConsent(Sid sid, DateTimeOffset now) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = TenantA,
        GranteeType = GranteeType.User,
        GranteeSid = sid,
        Effect = ConsentEffect.Allow,
        ValidFrom = now.AddDays(-1),
        ValidTo = now.AddDays(30),
        WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["region"], WriteRowScope.AllRows)]
    };

    private static ConsentRowFilter RegionFilter() => new() { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueJson = "\"EMEA\"" };
    private static ConsentRowFilter AttributeFilter() => new() { ColumnName = "region", Operator = "EQ", ValueType = "string", ValueSource = "USER_ATTRIBUTE", UserAttribute = "department" };
    private static ConsentColumnRule SsnRule(ColumnAccessLevel level) => new() { ColumnName = "ssn", AccessLevel = level };
    private static ConsentColumnRule SsnScopeRule(string scope, ColumnAccessLevel level) => new() { ColumnName = "ssn", AccessLevel = level, ApplicableScopes = [scope] };

    private static AccessProfile StrictProfile(Sid sid, DateTimeOffset now) => new()
    {
        ProfileId = "strict-1",
        Name = "Strict",
        TenantId = TenantA,
        MaskingMode = MaskingPolicyMode.Strict,
        TargetTables = ["*.*"],
        Assignments = [new AccessProfileAssignment { SubjectType = AccessProfileSubjectType.User, Subject = sid.Value, AssignedAt = now.AddDays(-1), ExpiresAt = now.AddDays(30) }]
    };

    private static VirtualFilterSnapshot RegionMandatorySnapshot()
    {
        var filter = new VirtualFilter
        {
            TenantId = TenantA,
            Name = "vf_region",
            Source = "default",
            KeyColumns = ["orders.region"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("default", "main", "orders"),
                FromAlias = "orders",
                Where = [new FilterCondition("region", FilterConditionOperator.IsNotNull)]
            }
        };
        var profile = new VirtualFilterAccessProfile
        {
            TenantId = TenantA,
            Name = "vf_prof",
            GranteeSid = AliceSid,
            Scope = "default.*.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings = [new FilterBinding { FilterName = "vf_region" }]
        };
        return new VirtualFilterSnapshot(1, [filter], [profile]);
    }

    // Minimal In-Memory Test Collaborators (Zero Substitute.For)
    private sealed class InMemoryTableRepo(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class InMemoryConsentRepo(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.TenantId == tenantId && c.TableIdentifier.Equals(table)).ToList());
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.TenantId == tenantId).ToList());
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new ConsentPage(consents, null));
    }

    private sealed class InMemoryAccessProfileRepo(IReadOnlyList<AccessProfile> profiles) : IAccessProfileRepository
    {
        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default) => Task.FromResult(profiles);
        public Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default) => Task.FromResult(profiles);
        public Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) => Task.FromResult<AccessProfile?>(null);
        public Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(TenantId tenantId, IReadOnlyCollection<string> users, IReadOnlyCollection<string> groups, IReadOnlyCollection<string> roles, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<AccessProfile>>(profiles.Where(p => p.TenantId == tenantId).ToList());
        public Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct = default) => Task.FromResult(assignment);
        public Task<AccessProfileAssignment> ApproveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, string approvedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccessProfileAssignment> RenewAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<RevokedRoleConsent>> GetRevokedRoleConsentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RevokedRoleConsent>>([]);
        public Task<IReadOnlyList<DeactivatedProfileAssignment>> GetDeactivatedAssignmentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeactivatedProfileAssignment>>([]);
    }

    private sealed class NoOpAuditRepo : IAuditLogRepository
    {
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordAuditEventAsync(AuditLogEntry entry, DbTransaction existingTx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class AllowingTransportGate : IDataSourceTransportGate
    {
        public static readonly AllowingTransportGate Instance = new();
        public DataSourceTransportFlags Ingress => DataSourceTransportFlags.WebSql;
        public IReadOnlyList<string> CheckedSources => [];
        public Task EnsureAsync(string? dataSourceName, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureAllAsync(IEnumerable<string?> dataSourceNames, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourcesAsync(IEnumerable<string?> catalogSourceNames, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnsureCatalogSourceAsync(string? catalogSourceName, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsAllowed(string? dataSourceName) => true;
    }

    private sealed class PassthroughMaskingProvider : IColumnMaskingProvider
    {
        public object? MaskValue(string columnName, object? rawValue, MaskingRule rule) => "[REDACTED]";
    }

    private sealed class FixedVirtualFilterSnapshot(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class PlaceholderVirtualFilterPredicates : IVirtualFilterPredicateBuilder
    {
        public string Build(VirtualFilter filter, FilterBinding binding, TableMetadata target, DatabaseDialect dialect) => "region IS NOT NULL";
    }

    private sealed class FixedCallerAttributeResolver(bool failResolution) : ICallerAttributeResolver
    {
        public bool IsEnabled => true;
        public CallerAttributePadding Padding => CallerAttributePadding.Default;
        public int BindReserve => 8;
        public CallerSubject CreateSubject(ClaimsPrincipal? principal, TenantId tenant, bool longLived = false) =>
            new(tenant, principal is null ? CallerSubjectKind.None : CallerSubjectKind.User, null,
                principal?.Claims.ToList() ?? [], null, principal?.Identity?.AuthenticationType);
        public ValueTask<CallerAttributeSet> ResolveAsync(CallerSubject subject, IReadOnlyCollection<string> names, CancellationToken ct) =>
            failResolution
                ? ValueTask.FromResult(new CallerAttributeSet(
                    names.ToDictionary(n => n, _ => CallerAttributeResult.Failed("unavailable"), StringComparer.Ordinal)))
                : ValueTask.FromResult(CallerAttributeSet.Empty);
    }

    private sealed class FixedIpResolver(IPAddress ip) : IClientIpResolver
    {
        public IPAddress ResolveClientIp() => ip;
    }

    private sealed class InMemoryCatalogProvider(ITableMetadataRepository repo) : IGovernedCatalogProvider
    {
        public async ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct)
        {
            var meta = await repo.GetTableMetadataAsync(OrdersId, ct);
            var cols = meta?.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray() ?? [];
            return new InMemoryTableCatalog([new TableCatalogEntry(new TableIdentity("main", "orders"), cols, "tenant_id", 1)], "main");
        }
    }

    private sealed class InMemorySqliteConnectionFactory : ISqlConnectionFactory
    {
        public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            var conn = new SqliteConnection("Data Source=ParityMatrixOrders;Mode=Memory;Cache=Shared");
            await conn.OpenAsync(ct);
            return conn;
        }
    }

    private sealed class StubMcpQueryExecutor : IMcpQueryExecutor
    {
        public Task<string> ExecuteOperationAsync(McpToolDefinition tool, string argumentsJson, McpSessionContext sessionContext, CancellationToken cancellationToken = default)
            => Task.FromResult("""{"rows":[{"id":1}]}""");
    }

    private sealed class StubProcedureInvoker : IProcedureInvoker
    {
        public Task<RawProcedureResult> ExecuteReadAsync(ProcedureDefinition definition, IReadOnlyDictionary<string, object?> clientValues, ProcedureSecurityContext security, int rowBudget, CancellationToken ct)
            => Task.FromResult(new RawProcedureResult(["id", "region", "ssn"], [[1, "EMEA", "123-45-6789"]], false));
    }

    private sealed class StubStagingService : IFederatedStagingService
    {
        public Task<IReadOnlyList<OlapTableSource>> StageAsync(IReadOnlyList<StagingTableRequest> tables, ClaimsPrincipal user, TenantId tenantId, FederationBudget budget, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OlapTableSource>>([]);
    }

    private sealed class StubDuckDbEngine : IDuckDbOlapEngine
    {
        public void Dispose() { }
        public Task<OlapQueryResult> ExecuteOlapQueryAsync(OlapQueryRequest request, CancellationToken ct = default)
            => Task.FromResult(new OlapQueryResult(["id", "region", "ssn"], [], 0, TimeSpan.Zero));
        public Task<OlapQueryResult> ExecuteGeneratedAsync(GeneratedOlapQuery query, IReadOnlyList<OlapTableSource> sources, int? limit = null, CancellationToken ct = default)
            => Task.FromResult(new OlapQueryResult(["id", "region", "ssn"], [], 0, TimeSpan.Zero));
    }

    private sealed class StubIcebergReader : IIcebergMetadataReader
    {
        public ValueTask<IcebergTableMetadata> LoadTableMetadataAsync(string metadataLocation, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new IcebergTableMetadata("uuid", 2, metadataLocation, 1, 0, 1, new IcebergSchema(1, []), new IcebergPartitionSpec(0, []), []));
        public ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(IcebergTableMetadata metadata, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<IcebergDataFile>>([]);
        public ValueTask<IReadOnlyList<IcebergDataFile>> LoadDataFilesAsync(IcebergTableMetadata metadata, string configuredTableLocation, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<IcebergDataFile>>([]);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StaticOkHandler());
        private sealed class StaticOkHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
                => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""[{"id":1,"region":"EMEA"}]""") });
        }
    }
}
