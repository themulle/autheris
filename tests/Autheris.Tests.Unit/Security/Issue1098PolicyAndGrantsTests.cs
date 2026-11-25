namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Caching;
using Autheris.Application.Governance.Ownership;
using Autheris.Application.Governance.Paging;
using Autheris.Application.Governance.Policy;
using Autheris.Application.Identity.Attributes;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Protection;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Policy.Services;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Errors;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.Governance.Classification;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "Security")]
public sealed class Issue1098PolicyAndGrantsTests
{
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");
    private static readonly Sid UserSid = new("S-1-5-21-1098-1-1-1001");
    private static readonly Sid AltUserSid = new("S-1-5-21-1098-1-1-1002");
    private static readonly Sid GroupSid = new("S-1-5-21-1098-1-1-3001");
    private static readonly Sid OtherGroupSid = new("S-1-5-21-1098-1-1-3002");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 25, 8, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier DomainBOrders = new("domainB", "dbo", "orders");

    [Fact]
    public void SubjectSidHelpers_CombineEffectiveSubjectSids_DeduplicateDeterministicallyAndFallBackToUserSid()
    {
        var meta = OrdersMetadata(DomainBOrders);
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, UserSid.Value),
            new Claim("oid", AltUserSid.Value),
            IdentityClaimIssuers.CreateGroupSidClaim(GroupSid.Value),
            IdentityClaimIssuers.CreateGroupSidClaim(OtherGroupSid.Value),
            new Claim("tenant_id", TenantA.Value)
        ], "Bearer");
        var principal = PipelinePrincipals.MarkComplete(new ClaimsPrincipal(identity));
        var subject = SecurityPrincipalContext.FromPrincipal(principal, TenantA, "Bearer");

        var query = TableAccessQuery.ForPrincipal(principal, UserSid, TenantA, meta);
        var expected = new[] { GroupSid, OtherGroupSid, AltUserSid, UserSid }
            .OrderBy(static s => s.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        subject.GetEffectiveSubjectSids().ShouldBe(expected);
        query.GetEffectiveSubjectSids().ShouldBe(expected);

        // Fallback to UserSid when AllUserSids is null or empty.
        var fallbackQuery = new TableAccessQuery(
            UserSid, TenantA, new HashSet<Sid> { GroupSid, new("  ") }, new HashSet<string>(), meta, AllUserSids: new HashSet<Sid>());
        fallbackQuery.GetEffectiveSubjectSids().ShouldBe([UserSid, GroupSid]);
    }

    [Fact]
    public async Task C1_GroupGrantedClassificationAndDeny_AreEnforcedWhenAllUserSidsIsPopulated()
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var classStore = new TestInMemoryClassificationStore(TenantA.Value);
        var resolver = new ClassificationAccessResolver(
            new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), timeProvider: time), classStore, time);

        var colId = Guid.NewGuid();
        var colEmail = Guid.NewGuid();
        var colNotes = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = DomainBOrders,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "domainB", SchemaName = "dbo", TableName = "orders", SourceType = "PostgreSql", IsActive = true },
            Columns =
            [
                new TableColumn { Id = colId, ColumnName = "id", DataType = "int" },
                new TableColumn { Id = colEmail, ColumnName = "email", DataType = "TEXT" },
                new TableColumn { Id = colNotes, ColumnName = "notes", DataType = "TEXT" }
            ],
            ColumnClassifications = new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] =
                [
                    new ColumnClassification
                    {
                        TableColumnId = colEmail.ToString("D"),
                        Scope = ClassificationScopes.Global,
                        Code = ClassificationScopes.PiiCode,
                        Source = "manual",
                        AssignedBy = "owner",
                        AssignedAt = FixedNow.AddDays(-1)
                    }
                ]
            }
        };

        var groupAllow = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = DomainBOrders,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "notes", AccessLevel = ColumnAccessLevel.Clear }
            ]
        };
        var groupColumnDeny = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = DomainBOrders,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupSid,
            Effect = ConsentEffect.Deny,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5),
            ColumnRules = [new ConsentColumnRule { ColumnName = "notes", AccessLevel = ColumnAccessLevel.Deny }]
        };

        var consentRepo = new PatternAwareConsentRepository([groupAllow, groupColumnDeny]);
        var pdp = CreatePdp(resolver, consentRepo, time, protection: ConfidentialProtection());
        var principal = CreatePrincipal(UserSid, GroupSid);
        var subject = SecurityPrincipalContext.FromPrincipal(principal, TenantA, "Bearer");

        var decision = await pdp.DecideAsync(new AccessRequest(subject, meta, AccessOperation.Read, AccessTransport.Rest));
        decision.IsAllowed.ShouldBeTrue(string.Join("; ", decision.DeniedReasons));
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("notes").ShouldBe(ColumnAccessLevel.Deny);
        (decision.ColumnMasks as ColumnMaskPolicy)?.HasMask("email").ShouldBeTrue();

        var batchDecisions = await pdp.DecideManyAsync(subject, [meta], AccessOperation.Read, AccessTransport.Rest);
        batchDecisions[0].GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        batchDecisions[0].GetColumnAccess("notes").ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task C1_GroupTargetedUnmaskGrant_LiftsCategoryMaskWhenAllUserSidsIsPopulated()
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var store = new FakeConsentRepository();
        await store.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = DomainBOrders,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupSid,
            Effect = ConsentEffect.Allow,
            GrantKind = PermissionGrantKind.Unmask,
            TargetColumns = ["email", "api_key"],
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5)
        });

        var registered = new AttributeValidatingConsentRepository(store, new NoOpMaterializationGuard());
        using var unmaskResolver = new UnmaskGrantResolver(registered, new GovernanceEpochProvider(new FixedEpochStore()));

        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            ProtectionClass.Public,
            ProtectionClass.Public,
            TableMaskCategory: null,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new ColumnProtection(ProtectionClass.Public, ClassificationScopes.PiiCode),
                ["api_key"] = new ColumnProtection(ProtectionClass.Public, ClassificationScopes.CredentialsCode)
            },
            Visibility: CatalogVisibilityMode.Open);

        var classStore = new TestInMemoryClassificationStore(TenantA.Value);
        var classResolver = new ClassificationAccessResolver(
            new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), timeProvider: time), classStore, time);

        var pdp = CreatePdp(classResolver, registered, time, unmaskResolver: unmaskResolver, protection: protection);
        var meta = OrdersMetadata(DomainBOrders);
        var principal = CreatePrincipal(UserSid, GroupSid);
        var subject = SecurityPrincipalContext.FromPrincipal(principal, TenantA, "Bearer");

        var decision = await pdp.DecideAsync(new AccessRequest(subject, meta, AccessOperation.Read, AccessTransport.Rest));
        decision.IsAllowed.ShouldBeTrue(string.Join("; ", decision.DeniedReasons));
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("api_key").ShouldBe(ColumnAccessLevel.Mask);
    }

    [Fact]
    public async Task H1_CrossTenantAndCrossDomainPatternConsents_CannotWeakenClassificationOrRowScope()
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var classStore = new TestInMemoryClassificationStore(TenantA.Value);
        var resolver = new ClassificationAccessResolver(
            new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), timeProvider: time), classStore, time);

        var meta = OrdersMetadata(DomainBOrders, emailPii: true);
        var rowFilteredDomainB = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = DomainBOrders,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
            ],
            RowFilters =
            [
                new ConsentRowFilter
                {
                    FilterGroup = 0,
                    ColumnName = "region",
                    Operator = "EQ",
                    ValueType = "string",
                    ValueJson = "\"EU\"",
                    ValueSource = "LITERAL"
                }
            ]
        };

        var unauthorizedDomainAPattern = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            DataSource = "domainB",
            ObjectPattern = "domainB.dbo.*",
            TableIdentifier = default,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            GrantKind = PermissionGrantKind.Data,
            WorkflowMode = ConsentWorkflowMode.Internal,
            ApprovingOwners = ["S-1-5-21-DOMAIN-A-OWNER"],
            ColumnSetMode = ColumnSetMode.AllNonHidden,
            ColumnRules = [new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Clear }],
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5)
        };

        var crossTenantPattern = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantB,
            DataSource = "domainB",
            ObjectPattern = "domainB.dbo.*",
            TableIdentifier = default,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            GrantKind = PermissionGrantKind.Data,
            WorkflowMode = ConsentWorkflowMode.External,
            ColumnSetMode = ColumnSetMode.AllNonHidden,
            ColumnRules = [new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Clear }],
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5)
        };

        var consentRepo = new PatternAwareConsentRepository([rowFilteredDomainB, unauthorizedDomainAPattern, crossTenantPattern]);
        var ownership = new DomainRestrictedOwnership(["S-1-5-21-DOMAIN-B-OWNER"]);
        var rowCompiler = new RowScopeCompiler(classificationResolver: resolver, timeProvider: time);
        var pdp = CreatePdp(
            resolver, consentRepo, time,
            protection: ConfidentialProtection(),
            ownership: ownership,
            rowScopeCompiler: rowCompiler);

        var principal = CreatePrincipal(UserSid);
        var subject = SecurityPrincipalContext.FromPrincipal(principal, TenantA, "Bearer");

        var decision = await pdp.DecideAsync(new AccessRequest(subject, meta, AccessOperation.Read, AccessTransport.Rest)
        {
            SupplementalConsents = [crossTenantPattern]
        });

        decision.IsAllowed.ShouldBeTrue(string.Join("; ", decision.DeniedReasons));
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        decision.CombinedRowFilterSql.ShouldNotBeNullOrWhiteSpace();
        decision.CombinedRowFilterSql.ShouldContain("region");
    }

    [Fact]
    public async Task H2_DecideWriteAsync_EnforcesProtectionMasksAndPersistedClassifications_BlockingDmlSideChannelsUnlessUnmasked()
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var classStore = new TestInMemoryClassificationStore(TenantA.Value);
        classStore.Definitions["LEGACY_INACTIVE"] = new DataClassification
        {
            Scope = TenantA.Value,
            Code = "LEGACY_INACTIVE",
            DisplayName = "Inactive Legacy Code",
            RestrictionRank = 90,
            IsActive = false,
            UpdatedBy = "admin",
            UpdatedAt = FixedNow
        };

        var colSecretId = Guid.NewGuid();
        var colLegacyId = Guid.NewGuid();
        classStore.ColumnClassifications.Add(new ColumnClassification
        {
            TableColumnId = colSecretId.ToString("D"),
            Scope = ClassificationScopes.Global,
            Code = ClassificationScopes.PiiCode,
            Source = "db",
            AssignedBy = "owner",
            AssignedAt = FixedNow
        });
        classStore.ColumnClassifications.Add(new ColumnClassification
        {
            TableColumnId = colLegacyId.ToString("D"),
            Scope = ClassificationScopes.Global,
            Code = "LEGACY_INACTIVE",
            Source = "db",
            AssignedBy = "owner",
            AssignedAt = FixedNow
        });

        var tableGuid = Guid.NewGuid();
        var meta = new TableMetadata
        {
            Identifier = DomainBOrders,
            PrimaryKeyColumns = ["id"],
            TenantColumnName = "tenant_id",
            Table = new Table
            {
                Id = tableGuid,
                SourceName = "domainB",
                SourceType = "PostgreSql",
                SchemaName = "dbo",
                TableName = "orders",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), TableId = tableGuid, ColumnName = "id", DataType = "int" },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableGuid, ColumnName = "tenant_id", DataType = "varchar" },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableGuid, ColumnName = "status", DataType = "varchar" },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableGuid, ColumnName = "amount", DataType = "int" },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableGuid, ColumnName = "email", DataType = "varchar" },
                new TableColumn { Id = Guid.NewGuid(), TableId = tableGuid, ColumnName = "api_key", DataType = "varchar" },
                new TableColumn { Id = colSecretId, TableId = tableGuid, ColumnName = "db_pii", DataType = "varchar" },
                new TableColumn { Id = colLegacyId, TableId = tableGuid, ColumnName = "legacy_col", DataType = "varchar" }
            ]
        };

        var writeConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableId = tableGuid,
            TableIdentifier = DomainBOrders,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            GrantKind = PermissionGrantKind.Data,
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5),
            WriteRules =
            [
                new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["amount", "email", "legacy_col"], WriteRowScope.AllRows)
            ]
        };

        var protection = new EffectiveProtection(
            new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            null,
            ProtectionClass.Internal,
            ProtectionClass.Internal,
            TableMaskCategory: null,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new ColumnProtection(ProtectionClass.Internal, ClassificationScopes.PiiCode),
                ["api_key"] = new ColumnProtection(ProtectionClass.Internal, ClassificationScopes.CredentialsCode)
            },
            Visibility: CatalogVisibilityMode.Open);

        var resolver = new ClassificationAccessResolver(
            new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), timeProvider: time), classStore, time);
        var consentStore = new FakeConsentRepository();
        await consentStore.CreateConsentAsync(writeConsent);

        var opts = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = true, MaxAffectedRows = 100, AllowedDataSources = ["domainB"] },
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["domainB"] = new DataSourceConnectionOptions
                    {
                        Provider = "PostgreSql",
                        ConnectionString = "Host=read.internal;Database=app",
                        WriteConnectionString = "Host=write.internal;Database=app",
                        DefaultSchema = "dbo"
                    }
                }
            }
        });

        var epochProvider = new GovernanceEpochProvider(new FixedEpochStore());
        var unmaskStore = new UnmaskGrantResolver(consentStore, epochProvider, timeProvider: time);
        var typed = new TypedPolicySources(
            new RowPolicyFactory(new PolicyPredicateRenderer()),
            new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(opts, timeProvider: time)),
            ClassificationResolver: resolver);

        var pdp = new AccessDecisionPoint(
            opts,
            new ConsentResolutionService(),
            consentStore,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            typedPolicies: typed,
            writeGrantEvaluator: new WriteGrantEvaluator(consentStore, timeProvider: time),
            timeProvider: time,
            protectionProvider: new StubProtection(protection),
            accessPolicySettings: new StubPolicySettings(TenantA),
            unmaskResolver: unmaskStore);

        var principal = CreatePrincipal(UserSid, GroupSid, extraScope: "Sql.Write");
        var subject = SecurityPrincipalContext.FromPrincipal(principal, TenantA, "Bearer");

        // 1) Without UNMASK: W5 category masks (email, api_key) and DB classifications (db_pii, legacy_col -> Deny) are enforced on write decision.
        var writeDecision = await pdp.DecideAsync(
            new AccessRequest(subject, meta, AccessOperation.Update, AccessTransport.WebSql, Columns: ["amount"]));
        writeDecision.IsAllowed.ShouldBeTrue(string.Join("; ", writeDecision.DeniedReasons));
        writeDecision.GetEffectiveColumnAccess("status", meta).ShouldBe(ColumnAccessLevel.Clear);
        writeDecision.GetEffectiveColumnAccess("email", meta).ShouldBe(ColumnAccessLevel.Mask);
        writeDecision.GetEffectiveColumnAccess("api_key", meta).ShouldBe(ColumnAccessLevel.Mask);
        writeDecision.GetEffectiveColumnAccess("db_pii", meta).ShouldBe(ColumnAccessLevel.Mask);
        writeDecision.GetEffectiveColumnAccess("legacy_col", meta).ShouldBe(ColumnAccessLevel.Deny);
        writeDecision.WriteGrant!.WritableColumns.ShouldNotContain("legacy_col");
        (writeDecision.ColumnMasks as ColumnMaskPolicy)?.HasMask("email").ShouldBeTrue();
        (writeDecision.ColumnMasks as ColumnMaskPolicy)?.HasMask("db_pii").ShouldBeTrue();

        var service = Autheris.Tests.Unit.TestDoubles.GovernedSqlExecutionServiceFactory.Create(
            opts,
            connectionFactory: new DummyWriteConnectionFactory(),
            auditLogRepository: Autheris.Tests.Unit.TestDoubles.NullAuditLogRepository.Instance,
            executionGuard: GovernedTestSupport.Guard,
            consentResolution: new ConsentResolutionService(),
            tableRepository: new SingleTableRepo(meta),
            consentRepository: consentStore,
            sqlEngine: new FastSqlEngine(),
            policyFactory: GovernedTestSupport.PolicyFactory(new SingleTableRepo(meta), opts),
            typedPolicies: typed,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            accessDecisionPoint: pdp);

        // Side-channel probing in WHERE or copying via SET assignment RHS is rejected fail-closed.
        var exEmailWhere = await Should.ThrowAsync<WebSqlPolicyException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE domainB.dbo.orders SET amount = 1 WHERE email = 'ceo@corp.com'", null, "domainB", Transport: SqlTransports.WebSql),
            principal, TenantA));
        exEmailWhere.Code.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FilterOnMaskedColumn);
        await Should.ThrowAsync<System.Security.SecurityException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE domainB.dbo.orders SET status = db_pii WHERE id = 1", null, "domainB", Transport: SqlTransports.WebSql),
            principal, TenantA));
        await Should.ThrowAsync<System.Security.SecurityException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE domainB.dbo.orders SET legacy_col = 'x' WHERE id = 1", null, "domainB", Transport: SqlTransports.WebSql),
            principal, TenantA));

        // 2) Grant group-targeted UNMASK on email, db_pii, api_key -> PII is clear in WHERE, CREDENTIALS stays masked!
        await consentStore.CreateConsentAsync(new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = TenantA,
            TableIdentifier = DomainBOrders,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupSid,
            Effect = ConsentEffect.Allow,
            GrantKind = PermissionGrantKind.Unmask,
            TargetColumns = ["email", "db_pii", "api_key"],
            ValidFrom = FixedNow.AddDays(-1),
            ValidTo = FixedNow.AddDays(5)
        });
        epochProvider.InvalidateTenant(TenantA.Value);

        var unmaskedDecision = await pdp.DecideAsync(
            new AccessRequest(subject, meta, AccessOperation.Update, AccessTransport.WebSql, Columns: ["amount"]));
        unmaskedDecision.GetEffectiveColumnAccess("email", meta).ShouldBe(ColumnAccessLevel.Clear);
        unmaskedDecision.GetEffectiveColumnAccess("db_pii", meta).ShouldBe(ColumnAccessLevel.Clear);
        unmaskedDecision.GetEffectiveColumnAccess("api_key", meta).ShouldBe(ColumnAccessLevel.Mask);

        await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE domainB.dbo.orders SET amount = 2 WHERE email = 'ceo@corp.com'", null, "domainB", Transport: SqlTransports.WebSql),
            principal, TenantA);
        var exKeyWhere = await Should.ThrowAsync<WebSqlPolicyException>(() => service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest("UPDATE domainB.dbo.orders SET amount = 2 WHERE api_key = 'secret'", null, "domainB", Transport: SqlTransports.WebSql),
            principal, TenantA));
        exKeyWhere.Code.ShouldBe(TrinoSqlEngine.Ast.Emit.GovernedSqlErrorCodes.FilterOnMaskedColumn);
    }

    private static ClaimsPrincipal CreatePrincipal(Sid userSid, Sid? groupSid = null, string? extraScope = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, userSid.Value),
            new("sub", userSid.Value),
            new("tenant_id", TenantA.Value)
        };
        if (groupSid.HasValue)
        {
            claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(groupSid.Value.Value));
        }

        if (!string.IsNullOrWhiteSpace(extraScope))
        {
            claims.Add(new Claim("scp", extraScope));
        }

        return PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")));
    }

    private static TableMetadata OrdersMetadata(TableIdentifier id, bool emailPii = false) => new()
    {
        Identifier = id,
        PrimaryKeyColumns = ["id"],
        Table = new Table { Id = Guid.NewGuid(), SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, SourceType = "PostgreSql", IsActive = true },
        Columns =
        [
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "region", DataType = "varchar" },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "email", DataType = "varchar" },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "api_key", DataType = "varchar" },
            new TableColumn { Id = Guid.NewGuid(), ColumnName = "amount", DataType = "int" }
        ],
        ColumnClassifications = emailPii
            ? new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = [new ColumnClassification { TableColumnId = "email", Scope = ClassificationScopes.Global, Code = ClassificationScopes.PiiCode, Source = "manual", AssignedBy = "owner", AssignedAt = FixedNow }]
            }
            : new Dictionary<string, IReadOnlyList<ColumnClassification>>(StringComparer.OrdinalIgnoreCase)
    };

    private static EffectiveProtection PublicProtection() =>
        new(new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), null, ProtectionClass.Public, ProtectionClass.Public, null,
            new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase), CatalogVisibilityMode.Open);

    private static EffectiveProtection ConfidentialProtection() =>
        new(new ContainerRef(ContainerKind.Domain, Guid.NewGuid()), null, ProtectionClass.Confidential, ProtectionClass.Confidential, null,
            new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase), CatalogVisibilityMode.Open);

    private static AccessDecisionPoint CreatePdp(
        ClassificationAccessResolver classResolver,
        IConsentRepository consents,
        TimeProvider time,
        EffectiveProtection? protection = null,
        IUnmaskGrantResolver? unmaskResolver = null,
        IContainerOwnership? ownership = null,
        RowScopeCompiler? rowScopeCompiler = null)
    {
        var opts = Options.Create(new GatewayOptions());
        var typed = new TypedPolicySources(
            new RowPolicyFactory(new PolicyPredicateRenderer()),
            new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(opts, timeProvider: time)),
            ClassificationResolver: classResolver);
        return new AccessDecisionPoint(
            opts,
            new ConsentResolutionService(),
            consents,
            NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            typedPolicies: typed,
            timeProvider: time,
            rowScopeCompiler: rowScopeCompiler,
            ownershipRepository: ownership,
            protectionProvider: protection is not null ? new StubProtection(protection) : null,
            accessPolicySettings: new StubPolicySettings(TenantA),
            unmaskResolver: unmaskResolver);
    }

    private sealed class PatternAwareConsentRepository(IReadOnlyList<Consent> consents) : IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            var set = subjects.ToHashSet();
            return Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c =>
                !c.IsPatternGrant && c.TableIdentifier.Equals(table) && c.IsActive(atTime) &&
                (tenantId is null || c.TenantId == tenantId.Value) &&
                c.GranteeSid is { } sid && set.Contains(sid)).ToList());
        }

        public Task<IReadOnlyList<Consent>> GetActivePatternConsentsAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.IsPatternGrant && c.TenantId == tenantId && c.IsActive(atTime)).ToList());

        public Task<IReadOnlyList<Consent>> GetPatternConsentsWithPendingDeniesAsync(TenantId tenantId, DateTimeOffset atTime, CancellationToken ct = default) =>
            GetActivePatternConsentsAsync(tenantId, atTime, ct);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task<ConsentPage> GetConsentPageAsync(ConsentPageQuery query, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DomainRestrictedOwnership(params string[] authorizedOwners) : IContainerOwnership
    {
        private readonly HashSet<string> _owners = authorizedOwners.ToHashSet(StringComparer.OrdinalIgnoreCase);
        public Task<IReadOnlyList<DataOwner>> GetDataOwnersForTableAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DataOwner>>([]);
        public Task<bool> IsAuthorizedApproverForTableAsync(TableIdentifier table, Sid approverSid, CancellationToken ct = default) =>
            Task.FromResult(_owners.Contains(approverSid.Value));
    }

    private sealed class StubProtection(EffectiveProtection protection) : IProtectionSnapshotProvider
    {
        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default) =>
            ValueTask.FromResult(protection);
    }

    private sealed class StubPolicySettings(TenantId tenant) : IAccessPolicySettingsProvider
    {
        public Task<TenantAccessPolicy> GetEffectiveAsync(TenantId tenantId, CancellationToken ct = default) =>
            Task.FromResult(TenantAccessPolicy.FromDeploymentDefaults(tenant, new AccessPolicyOptions()));
        public Task<AccessPolicyUpdateResult> UpdateAsync(TenantId tenantId, TenantAccessPolicyUpdate update, int expectedVersion, Sid actor, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpMaterializationGuard : IConsentMaterializationGuard
    {
        public ValueTask EnsureMayMaterializeAsync(TenantId tenant, TableIdentifier table, IReadOnlyList<ConsentRowFilter> filters, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FixedEpochStore : IGovernanceEpochStore
    {
        public Task<long> GetEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<long> BumpEpochAsync(TenantId tenant, CancellationToken ct = default) => Task.FromResult(2L);
    }

    private sealed class SingleTableRepo(TableMetadata table) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier id, CancellationToken ct = default) => Task.FromResult(id.Equals(table.Identifier) ? table : null);
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TableMetadata>>([table]);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier id, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class DummyWriteConnectionFactory : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default)
        {
            var conn = new Autheris.Tests.Unit.Governance.RecordingGovernanceDbConnection([]);
            conn.Open();
            return Task.FromResult<DbConnection>(conn);
        }
    }
}
