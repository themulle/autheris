namespace Autheris.Tests.Unit.AccessProfiles;

using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class AccessProfileTests
{
    private static readonly TableIdentifier TelemetryTable = new("telemetry", "tem", "gps_position");
    private static readonly TenantId TenantLiebherr = new("liebherr");

    private static TableMetadata CreateTelemetryTableMetadata()
    {
        return new TableMetadata
        {
            Identifier = TelemetryTable,
            Table = new Table
            {
                SourceName = "telemetry",
                SchemaName = "tem",
                TableName = "gps_position",
                SourceType = "PostgreSql",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "crane_id", DataType = "VARCHAR(64)", IsSensitive = false },
                new TableColumn { ColumnName = "latitude", DataType = "DOUBLE PRECISION", IsSensitive = true },
                new TableColumn { ColumnName = "longitude", DataType = "DOUBLE PRECISION", IsSensitive = true },
                new TableColumn { ColumnName = "customer_id", DataType = "VARCHAR(128)", IsSensitive = true },
                new TableColumn { ColumnName = "status", DataType = "VARCHAR(32)", IsSensitive = false }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["latitude"] = new MaskingRule { RuleType = "GEO_JITTER", Mode = "round", Decimals = 2 },
                ["longitude"] = new MaskingRule { RuleType = "GEO_JITTER", Mode = "round", Decimals = 2 },
                ["customer_id"] = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 2 }
            }
        };
    }

    private static TableAccessPolicy CreatePolicy(IAccessProfileRepository profileRepo, IMemoryCache? memoryCache = null)
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var resolutionService = Substitute.For<IConsentResolutionService>();
        var cacheService = Substitute.For<IConsentCacheService>();
        var policyEnforcementService = Substitute.For<IPolicyEnforcementService>();
        var rebacEvaluator = Substitute.For<IRebacEvaluator>();
        var clientIpResolver = Substitute.For<IClientIpResolver>();
        var mandatoryFilters = NullMandatoryRowFilterResolver.Instance;

        var options = new GatewayOptions();

        return new TableAccessPolicy(
            consentRepo,
            resolutionService,
            cacheService,
            policyEnforcementService,
            rebacEvaluator,
            clientIpResolver,
            options,
            mandatoryFilters,
            contractManager: null,
            accessProfileRepository: profileRepo,
            memoryCache: memoryCache);
    }

    [Fact]
    public async Task AccessProfileResolution_David_ReceivesClearAccessLevel()
    {
        // R-52 & R-50: David (Fachbereichsleiter Telemetrie) has Unmasked access profile for tem.*
        var repo = new InMemoryAccessProfileRepository();
        var davidProfile = new AccessProfile
        {
            ProfileId = "prof-david-unmasked",
            Name = "David Telemetry Cleartext Exception",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status != 'DELIVERED'",
            AssignedSubjects = ["david", "S-1-5-21-DAVID"],
            CreatedAt = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(90),
            Justification = "Fachbereichsleitung Telemetrie (PoC-Ausnahmegenehmigung)",
            CreatedBy = "admin@autheris.internal"
        };
        await repo.UpsertProfileAsync(davidProfile);

        var policy = CreatePolicy(repo);
        var meta = CreateTelemetryTableMetadata();

        var query = new TableAccessQuery(
            UserSid: new Sid("S-1-5-21-DAVID"),
            Tenant: TenantLiebherr,
            GroupSids: new HashSet<Sid>(),
            Roles: new HashSet<string> { "TelemetryLead" },
            Metadata: meta);

        var decision = await policy.DecideAsync(query, CancellationToken.None);

        // Assert
        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("status != 'DELIVERED'");

        // David must see all columns as CLEAR (unmasked exception)
        decision.GetEffectiveColumnAccess("latitude", meta).ShouldBe(ColumnAccessLevel.Clear);
        decision.GetEffectiveColumnAccess("longitude", meta).ShouldBe(ColumnAccessLevel.Clear);
        decision.GetEffectiveColumnAccess("customer_id", meta).ShouldBe(ColumnAccessLevel.Clear);
        decision.GetEffectiveColumnAccess("crane_id", meta).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task AccessProfileResolution_Philipp_ReceivesMaskedAccessLevel()
    {
        // R-52: Philipp (Analyst) has Default access profile for tem.* with standard masking
        var repo = new InMemoryAccessProfileRepository();
        var philippProfile = new AccessProfile
        {
            ProfileId = "prof-philipp-default",
            Name = "Philipp Telemetry Masked",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Default,
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status != 'DELIVERED'",
            AssignedSubjects = ["philipp", "S-1-5-21-PHILIPP"],
            CreatedAt = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(90),
            Justification = "Analyst Telemetry (PoC Default Masking)",
            CreatedBy = "admin@autheris.internal"
        };
        await repo.UpsertProfileAsync(philippProfile);

        var policy = CreatePolicy(repo);
        var meta = CreateTelemetryTableMetadata();

        var query = new TableAccessQuery(
            UserSid: new Sid("S-1-5-21-PHILIPP"),
            Tenant: TenantLiebherr,
            GroupSids: new HashSet<Sid>(),
            Roles: new HashSet<string> { "Analyst" },
            Metadata: meta);

        var decision = await policy.DecideAsync(query, CancellationToken.None);

        // Assert
        decision.IsAllowed.ShouldBeTrue();
        decision.CombinedRowFilterSql.ShouldBe("status != 'DELIVERED'");

        // Philipp must see sensitive columns as MASKED
        decision.GetEffectiveColumnAccess("latitude", meta).ShouldBe(ColumnAccessLevel.Mask);
        decision.GetEffectiveColumnAccess("longitude", meta).ShouldBe(ColumnAccessLevel.Mask);
        decision.GetEffectiveColumnAccess("customer_id", meta).ShouldBe(ColumnAccessLevel.Mask);
        decision.GetEffectiveColumnAccess("crane_id", meta).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task AccessProfileResolution_BothReceiveSameRowFilter()
    {
        var repo = new InMemoryAccessProfileRepository();
        var rowFilter = "delivery_status != 'DELIVERED'";

        var davidProfile = new AccessProfile
        {
            ProfileId = "prof-david",
            Name = "David Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            RowFilterPredicate = rowFilter,
            AssignedSubjects = ["david"],
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        };
        var philippProfile = new AccessProfile
        {
            ProfileId = "prof-philipp",
            Name = "Philipp Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Default,
            TargetTables = ["tem.*"],
            RowFilterPredicate = rowFilter,
            AssignedSubjects = ["philipp"],
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        };
        await repo.UpsertProfileAsync(davidProfile);
        await repo.UpsertProfileAsync(philippProfile);

        var policy = CreatePolicy(repo);
        var meta = CreateTelemetryTableMetadata();

        var queryDavid = new TableAccessQuery(new Sid("david"), TenantLiebherr, new HashSet<Sid>(), new HashSet<string>(), meta);
        var queryPhilipp = new TableAccessQuery(new Sid("philipp"), TenantLiebherr, new HashSet<Sid>(), new HashSet<string>(), meta);

        var decisionDavid = await policy.DecideAsync(queryDavid, CancellationToken.None);
        var decisionPhilipp = await policy.DecideAsync(queryPhilipp, CancellationToken.None);

        decisionDavid.CombinedRowFilterSql.ShouldBe(rowFilter);
        decisionPhilipp.CombinedRowFilterSql.ShouldBe(rowFilter);
        decisionDavid.CombinedRowFilterSql.ShouldBe(decisionPhilipp.CombinedRowFilterSql);
    }

    [Fact]
    public async Task AccessProfileResolution_Strict_ReceivesDenyOnSensitiveColumns()
    {
        var repo = new InMemoryAccessProfileRepository();
        var strictProfile = new AccessProfile
        {
            ProfileId = "prof-strict",
            Name = "Strict Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Strict,
            TargetTables = ["tem.*"],
            RowFilterPredicate = null,
            AssignedSubjects = ["restricted-user"],
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        };
        await repo.UpsertProfileAsync(strictProfile);

        var policy = CreatePolicy(repo);
        var meta = CreateTelemetryTableMetadata();

        var query = new TableAccessQuery(new Sid("restricted-user"), TenantLiebherr, new HashSet<Sid>(), new HashSet<string>(), meta);
        var decision = await policy.DecideAsync(query, CancellationToken.None);

        decision.IsAllowed.ShouldBeTrue();
        decision.GetEffectiveColumnAccess("latitude", meta).ShouldBe(ColumnAccessLevel.Deny);
        decision.GetEffectiveColumnAccess("longitude", meta).ShouldBe(ColumnAccessLevel.Deny);
        decision.GetEffectiveColumnAccess("crane_id", meta).ShouldBe(ColumnAccessLevel.Clear);
    }

    [Fact]
    public async Task AccessProfileResolution_ExpiredProfile_IsNotApplied()
    {
        TableAccessPolicy.ClearCache();
        var repo = new InMemoryAccessProfileRepository();
        var expiredProfile = new AccessProfile
        {
            ProfileId = "prof-expired",
            Name = "Expired David Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status != 'DELIVERED'",
            AssignedSubjects = ["david-expired"],
            ValidTo = DateTimeOffset.UtcNow.AddMinutes(-5) // Expired 5 minutes ago
        };
        await repo.UpsertProfileAsync(expiredProfile);

        var policy = CreatePolicy(repo);
        var meta = CreateTelemetryTableMetadata();

        var query = new TableAccessQuery(new Sid("david-expired"), TenantLiebherr, new HashSet<Sid>(), new HashSet<string>(), meta);
        var decision = await policy.DecideAsync(query, CancellationToken.None);

        // When no active profile matches, it falls through to consent / default denial
        decision.IsAllowed.ShouldBeFalse();
    }

    [Fact]
    public async Task AccessProfileResolution_CacheInvalidation_RefreshesProfile()
    {
        TableAccessPolicy.ClearCache();
        var repo = new InMemoryAccessProfileRepository();
        var subject = "david-cache-test";
        var profile = new AccessProfile
        {
            ProfileId = "prof-cache-test",
            Name = "Cache Test Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status = 'ORIGINAL'",
            AssignedSubjects = [subject],
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        await repo.UpsertProfileAsync(profile);

        var policy = CreatePolicy(repo);
        var meta = CreateTelemetryTableMetadata();
        var query = new TableAccessQuery(new Sid(subject), TenantLiebherr, new HashSet<Sid>(), new HashSet<string>(), meta);

        var decision1 = await policy.DecideAsync(query, CancellationToken.None);
        decision1.CombinedRowFilterSql.ShouldBe("status = 'ORIGINAL'");

        // Update profile in repo to new filter (new object instance so old cached reference isn't mutated in-place)
        var updatedProfile = new AccessProfile
        {
            ProfileId = "prof-cache-test",
            Name = "Cache Test Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            RowFilterPredicate = "status = 'UPDATED'",
            AssignedSubjects = [subject],
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        await repo.UpsertProfileAsync(updatedProfile);

        // Without invalidation, cache still returns original
        var decisionCached = await policy.DecideAsync(query, CancellationToken.None);
        decisionCached.CombinedRowFilterSql.ShouldBe("status = 'ORIGINAL'");

        // Invalidate cache
        TableAccessPolicy.InvalidateCache(TenantLiebherr, subject);

        // Now policy returns updated filter
        var decisionUpdated = await policy.DecideAsync(query, CancellationToken.None);
        decisionUpdated.CombinedRowFilterSql.ShouldBe("status = 'UPDATED'");
    }

    [Theory]
    [InlineData("tem.*", "tem", "gps_position", true)]
    [InlineData("tem.*", "tem", "crane", true)]
    [InlineData("tem.*", "md", "crane", false)]
    [InlineData("*.*", "any_schema", "any_table", true)]
    [InlineData("tem.gps_position", "tem", "gps_position", true)]
    [InlineData("tem.gps_position", "tem", "other_table", false)]
    [InlineData("telemetry.tem.gps_position", "tem", "gps_position", true)]
    public void AccessProfile_MatchesPattern_EvaluatesCorrectly(string pattern, string schema, string table, bool expectedMatch)
    {
        var id = new TableIdentifier("telemetry", schema, table);
        AccessProfile.MatchesPattern(pattern, id).ShouldBe(expectedMatch);
    }

    [Fact]
    public async Task InMemoryAccessProfileRepository_LifecycleWorks()
    {
        var repo = new InMemoryAccessProfileRepository();
        var profile = new AccessProfile
        {
            ProfileId = "test-prof-1",
            Name = "Test Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            AssignedSubjects = ["alice", "bob"],
            ValidTo = DateTimeOffset.UtcNow.AddDays(10)
        };

        await repo.UpsertProfileAsync(profile);

        var retrieved = await repo.GetProfileAsync(TenantLiebherr, "test-prof-1");
        retrieved.ShouldNotBeNull();
        retrieved.Name.ShouldBe("Test Profile");
        retrieved.AssignedSubjects.ShouldContain("alice");
        retrieved.AssignedSubjects.ShouldContain("bob");

        var aliceProfiles = await repo.GetProfilesForSubjectAsync(TenantLiebherr, "alice");
        aliceProfiles.Count.ShouldBe(1);
        aliceProfiles[0].ProfileId.ShouldBe("test-prof-1");

        var all = await repo.GetAllProfilesAsync(TenantLiebherr);
        all.Count.ShouldBe(1);

        var deleted = await repo.DeleteProfileAsync(TenantLiebherr, "test-prof-1");
        deleted.ShouldBeTrue();

        var afterDelete = await repo.GetProfileAsync(TenantLiebherr, "test-prof-1");
        afterDelete.ShouldBeNull();

        var aliceAfter = await repo.GetProfilesForSubjectAsync(TenantLiebherr, "alice");
        aliceAfter.ShouldBeEmpty();
    }

    [Fact]
    public async Task SqliteGovernanceRepository_AccessProfiles_PersistenceAndSubjectLookup()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                ConnectionString = $"Data Source=governance_test_{Guid.NewGuid():N};Mode=Memory;Cache=Shared",
                Provider = "Sqlite"
            }
        });

        var epochService = Substitute.For<IEpochValidationService>();
        using var sqliteRepo = new SqliteGovernanceRepository(
            epochService,
            options,
            logger: NullLogger<SqliteGovernanceRepository>.Instance);

        var profile = new AccessProfile
        {
            ProfileId = "prof-david-sqlite",
            Name = "David Sqlite Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*", "conf.*"],
            RowFilterPredicate = "delivery_status != 'DELIVERED'",
            AssignedSubjects = ["david", "S-1-5-21-DAVID-SQLITE"],
            CreatedAt = DateTimeOffset.UtcNow,
            ValidTo = DateTimeOffset.UtcNow.AddDays(90),
            Justification = "Valid Justification for Enterprise PoC Telemetry",
            CreatedBy = "admin@autheris.internal"
        };

        // Upsert
        await sqliteRepo.UpsertProfileAsync(profile);

        // Get by ID
        var fetched = await sqliteRepo.GetProfileAsync(TenantLiebherr, "prof-david-sqlite");
        fetched.ShouldNotBeNull();
        fetched.Name.ShouldBe("David Sqlite Profile");
        fetched.MaskingMode.ShouldBe(MaskingPolicyMode.Unmasked);
        fetched.TargetTables.ShouldContain("tem.*");
        fetched.TargetTables.ShouldContain("conf.*");
        fetched.RowFilterPredicate.ShouldBe("delivery_status != 'DELIVERED'");
        fetched.AssignedSubjects.ShouldContain("david");
        fetched.AssignedSubjects.ShouldContain("S-1-5-21-DAVID-SQLITE");

        // Lookup by Subject
        var davidProfiles = await sqliteRepo.GetProfilesForSubjectAsync(TenantLiebherr, "david");
        davidProfiles.Count.ShouldBe(1);
        davidProfiles[0].ProfileId.ShouldBe("prof-david-sqlite");

        // Lookup all
        var allProfiles = await sqliteRepo.GetAllProfilesAsync(TenantLiebherr);
        allProfiles.Count.ShouldBe(1);

        // Delete
        var deleted = await sqliteRepo.DeleteProfileAsync(TenantLiebherr, "prof-david-sqlite");
        deleted.ShouldBeTrue();

        var afterDelete = await sqliteRepo.GetProfileAsync(TenantLiebherr, "prof-david-sqlite");
        afterDelete.ShouldBeNull();

        var davidAfter = await sqliteRepo.GetProfilesForSubjectAsync(TenantLiebherr, "david");
        davidAfter.ShouldBeEmpty();
    }

    private static HttpContext CreateEndpointContext(
        string tenant = "liebherr",
        string callerSid = "S-1-5-21-ADMIN",
        string callerName = "admin@autheris.internal",
        IAuditLogRepository? auditRepo = null,
        IDistributedClusterStateProvider? clusterState = null,
        ITableMetadataRepository? metaRepo = null,
        IMemoryCache? memoryCache = null,
        params string[] roles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (auditRepo != null) services.AddSingleton(auditRepo);
        if (clusterState != null) services.AddSingleton(clusterState);
        if (metaRepo != null) services.AddSingleton(metaRepo);
        if (memoryCache != null) services.AddSingleton(memoryCache);

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            Response = { Body = new MemoryStream() }
        };

        var effectiveRoles = roles.Length > 0 ? roles : ["GovernanceAdmin"];
        context.Items[SecurityPrincipalContext.ItemKey] = new SecurityPrincipalContext
        {
            UserSid = new Sid(callerSid),
            TenantId = new TenantId(tenant),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(effectiveRoles.Where(r => r is not ("ClusterAdmin" or "GovernanceAdmin"))),
            ClusterRoles = new HashSet<string>(effectiveRoles.Where(r => r is "ClusterAdmin" or "GovernanceAdmin")),
            AuthenticationScheme = "Test"
        };

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, callerSid),
            new(ClaimTypes.Name, callerName),
            new("tenant", tenant)
        };
        foreach (var role in effectiveRoles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
        return context;
    }

    private static async Task<(int StatusCode, string Body)> ExecuteResultAsync(IResult result, HttpContext context)
    {
        await result.ExecuteAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        return (context.Response.StatusCode, body);
    }

    [Fact]
    public async Task BulkConsent_SelfGrant_FailsWithSoDViolation()
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext(callerSid: "david", callerName: "david");
        var req = new BulkConsentRequest
        {
            Subject = "david", // SoD violation: self-grant!
            MaskingMode = "Unmasked",
            Tables = ["tem.*"],
            Justification = "Trying to grant myself cleartext exception without 4-eyes approval",
            ValidDays = 30
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("Segregation of Duties (SoD) violation");
    }

    [Fact]
    public async Task BulkConsent_ShortJustification_FailsValidation()
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext(callerSid: "admin", callerName: "admin");
        var req = new BulkConsentRequest
        {
            Subject = "david",
            MaskingMode = "Unmasked",
            Tables = ["tem.*"],
            Justification = "Too short", // < 15 characters
            ValidDays = 30
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("Justification is required and must be at least 15 characters long");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(181)]
    [InlineData(365)]
    public async Task BulkConsent_InvalidValidDays_FailsValidation(int days)
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext(callerSid: "admin", callerName: "admin");
        var req = new BulkConsentRequest
        {
            Subject = "david",
            MaskingMode = "Unmasked",
            Tables = ["tem.*"],
            Justification = "Valid enterprise justification for telemetry exception",
            ValidDays = days
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("validDays must be between 1 and 180");
    }

    [Theory]
    [InlineData("*.*")]
    [InlineData("*")]
    public async Task BulkConsent_WildcardTableForUnmasked_FailsValidation(string wildcardPattern)
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext(callerSid: "admin", callerName: "admin");
        var req = new BulkConsentRequest
        {
            Subject = "david",
            MaskingMode = "Unmasked",
            Tables = [wildcardPattern], // Forbidden wildcard for unmasked
            Justification = "Valid enterprise justification for telemetry exception",
            ValidDays = 30
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("Overbroad wildcard '*.*' is prohibited for Unmasked access profiles");
    }

    [Theory]
    [InlineData("status = 'ACTIVE'; DROP TABLE users;")]
    [InlineData("status = 'ACTIVE' -- comment")]
    [InlineData("status = 'ACTIVE' /* block comment */")]
    public async Task BulkConsent_ProhibitedSqlConstructs_FailsValidation(string maliciousFilter)
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext(callerSid: "admin", callerName: "admin");
        var req = new BulkConsentRequest
        {
            Subject = "david",
            MaskingMode = "Default",
            Tables = ["tem.*"],
            RowFilter = maliciousFilter,
            Justification = "Valid enterprise justification for telemetry exception",
            ValidDays = 30
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("Row filter predicate contains prohibited SQL constructs");
    }

    [Fact]
    public async Task BulkConsent_InvalidSqlSyntax_FailsValidation()
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext(callerSid: "admin", callerName: "admin");
        var req = new BulkConsentRequest
        {
            Subject = "david",
            MaskingMode = "Default",
            Tables = ["tem.*"],
            RowFilter = "INVALID SQL >>> <<< SYNTAX",
            Justification = "Valid enterprise justification for telemetry exception",
            ValidDays = 30
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status400BadRequest);
        body.ShouldContain("Invalid row filter syntax");
    }

    [Fact]
    public async Task BulkConsent_ValidUnmaskedRequest_CreatesProfileAndLogsAudit()
    {
        var repo = new InMemoryAccessProfileRepository();
        var auditRepo = Substitute.For<IAuditLogRepository>();
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([CreateTelemetryTableMetadata()]));

        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var context = CreateEndpointContext(
            callerSid: "admin@autheris.internal",
            callerName: "Admin User",
            auditRepo: auditRepo,
            clusterState: clusterState,
            metaRepo: metaRepo,
            memoryCache: memoryCache);

        var req = new BulkConsentRequest
        {
            Subject = "david",
            MaskingMode = "Unmasked",
            Tables = ["tem.*"],
            RowFilter = "status != 'DELIVERED'",
            Justification = "Enterprise PoC Telemetry Head cleartext exception approved",
            ValidDays = 90
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status201Created);
        body.ShouldContain("prof-david-unmasked-");

        // Verify stored in repository
        var profiles = await repo.GetProfilesForSubjectAsync(TenantLiebherr, "david");
        profiles.Count.ShouldBe(1);
        var stored = profiles[0];
        stored.MaskingMode.ShouldBe(MaskingPolicyMode.Unmasked);
        stored.TargetTables.ShouldContain("tem.*");
        stored.RowFilterPredicate.ShouldBe("status != 'DELIVERED'");
        stored.Justification.ShouldBe(req.Justification);

        // Verify audit log recorded Tier-A CONSENT_GRANTED
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "CONSENT_GRANTED" &&
                e.Decision == "ALLOW" &&
                e.TenantId == TenantLiebherr),
            Arg.Any<CancellationToken>());

        // Verify cluster state epoch incremented
        await clusterState.Received(1).IncrementAsync(
            $"profile_epoch:{TenantLiebherr.Value}",
            1,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BulkConsent_ValidDefaultRequest_CreatesProfile()
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext();
        var req = new BulkConsentRequest
        {
            Subject = "philipp",
            MaskingMode = "Default",
            Tables = ["tem.*"],
            RowFilter = "status != 'DELIVERED'",
            Justification = "Analyst Telemetry masking profile approved by governance",
            ValidDays = 60
        };

        var result = await GovernanceEndpoints.CreateBulkConsentAsync(req, context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status201Created);

        var profiles = await repo.GetProfilesForSubjectAsync(TenantLiebherr, "philipp");
        profiles.Count.ShouldBe(1);
        profiles[0].MaskingMode.ShouldBe(MaskingPolicyMode.Default);
    }

    [Fact]
    public async Task GetProfiles_ReturnsListAndDetail()
    {
        var repo = new InMemoryAccessProfileRepository();
        var profile = new AccessProfile
        {
            ProfileId = "prof-test-123",
            Name = "Test Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Default,
            TargetTables = ["tem.*"],
            AssignedSubjects = ["user1"],
            ValidTo = DateTimeOffset.UtcNow.AddDays(10)
        };
        await repo.UpsertProfileAsync(profile);

        var context = CreateEndpointContext();

        // GET all
        var listResult = await GovernanceEndpoints.GetProfilesAsync(context, repo, CancellationToken.None);
        var (listStatus, listBody) = await ExecuteResultAsync(listResult, context);
        listStatus.ShouldBe(StatusCodes.Status200OK);
        listBody.ShouldContain("prof-test-123");

        // GET by ID (found)
        var detailContext = CreateEndpointContext();
        var detailResult = await GovernanceEndpoints.GetProfileByIdAsync("prof-test-123", detailContext, repo, CancellationToken.None);
        var (detailStatus, detailBody) = await ExecuteResultAsync(detailResult, detailContext);
        detailStatus.ShouldBe(StatusCodes.Status200OK);
        detailBody.ShouldContain("Test Profile");

        // GET by ID (not found)
        var notFoundContext = CreateEndpointContext();
        var notFoundResult = await GovernanceEndpoints.GetProfileByIdAsync("nonexistent", notFoundContext, repo, CancellationToken.None);
        var (notFoundStatus, _) = await ExecuteResultAsync(notFoundResult, notFoundContext);
        notFoundStatus.ShouldBe(StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task DeleteProfile_Existing_RevokesProfileAndLogsAudit()
    {
        var repo = new InMemoryAccessProfileRepository();
        var profile = new AccessProfile
        {
            ProfileId = "prof-to-revoke",
            Name = "Revoke Target Profile",
            TenantId = TenantLiebherr,
            MaskingMode = MaskingPolicyMode.Unmasked,
            TargetTables = ["tem.*"],
            AssignedSubjects = ["david-revoke"],
            ValidTo = DateTimeOffset.UtcNow.AddDays(30)
        };
        await repo.UpsertProfileAsync(profile);

        var auditRepo = Substitute.For<IAuditLogRepository>();
        var clusterState = Substitute.For<IDistributedClusterStateProvider>();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var context = CreateEndpointContext(
            callerSid: "admin@autheris.internal",
            auditRepo: auditRepo,
            clusterState: clusterState,
            memoryCache: memoryCache);

        var result = await GovernanceEndpoints.DeleteProfileAsync("prof-to-revoke", context, repo, CancellationToken.None);
        var (status, body) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status200OK);
        body.ShouldContain("successfully revoked");

        // Verify deleted from repo
        var fetched = await repo.GetProfileAsync(TenantLiebherr, "prof-to-revoke");
        fetched.ShouldBeNull();

        // Verify audit log recorded Tier-A CONSENT_REVOKED
        await auditRepo.Received(1).RecordAuditEventAsync(
            Arg.Is<AuditLogEntry>(e =>
                e.EventType == "CONSENT_REVOKED" &&
                e.Decision == "DENY" &&
                e.TenantId == TenantLiebherr),
            Arg.Any<CancellationToken>());

        // Verify epoch increment
        await clusterState.Received(1).IncrementAsync(
            $"profile_epoch:{TenantLiebherr.Value}",
            1,
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteProfile_NotFound_Returns404()
    {
        var repo = new InMemoryAccessProfileRepository();
        var context = CreateEndpointContext();

        var result = await GovernanceEndpoints.DeleteProfileAsync("nonexistent", context, repo, CancellationToken.None);
        var (status, _) = await ExecuteResultAsync(result, context);

        status.ShouldBe(StatusCodes.Status404NotFound);
    }
}
