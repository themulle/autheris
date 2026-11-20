namespace Autheris.Modules.OpenMetadata.Tests;

using Autheris.Tests.Common;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Audit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Models;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.Tests.TestSupport;
using Autheris.Modules.OpenMetadata;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Round 4 (FIX-O): E-05..E-09 and EX-02 / EX-06 / EX-16 – OpenMetadata reconcile, Art. 9 mapping, SID resolution,
/// Alation client hardening and the database-aware OpenMetadata identity.
/// </summary>
public sealed class OpenMetadataReconcileAndAlationHardeningTests
{
    private static readonly Guid PolicyId = new("6c1f2b0e-3a4d-4e5f-8a9b-0c1d2e3f4a5b");
    private static readonly TableIdentifier OrdersId = new("sales_svc", "dbo", "orders");

    // ------------------------------------------------------------------ helpers

    private static OpenMetadataTable OmTable(
        string service,
        string database,
        string schema,
        string name,
        List<OpenMetadataTag>? tags = null,
        List<OpenMetadataColumn>? columns = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        FullyQualifiedName = $"{service}.{database}.{schema}.{name}",
        Service = new OpenMetadataEntityReference { Name = service },
        Database = new OpenMetadataEntityReference { Name = database },
        DatabaseSchema = new OpenMetadataEntityReference { Name = schema },
        Tags = tags ?? [],
        Columns = columns ?? [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
    };

    private static OpenMetadataTable Orders(List<OpenMetadataTag>? tags = null, List<OpenMetadataColumn>? columns = null) =>
        OmTable("sales_svc", "salesdb", "dbo", "orders", tags, columns);

    private static OpenMetadataRule ViewAllTables() =>
        new() { Name = "ReadTables", Effect = "allow", Resources = ["table"], Operations = ["ViewAll"] };

    private const string OrderTeamSid = "S-1-5-21-ORDER-TEAM";

    /// <summary>The team that carries the allow policy: roles never grant data access, so allow consents are generated for teams and users only.</summary>
    private static OpenMetadataTeam OrderTeam() => new() { Id = Guid.NewGuid(), Name = "OrderTeam", Policies = [PolicyRef()] };

    private static OpenMetadataEntityReference PolicyRef() => new() { Id = PolicyId, Name = "R4Policy" };

    private static Consent SyncConsent(TableIdentifier table, GranteeType type, string grantee, ConsentEffect effect = ConsentEffect.Allow, DateTimeOffset? validTo = null) => new()
    {
        TenantId = Autheris.Tests.Common.TestTenants.Default,
        Id = Guid.NewGuid(),
        TableIdentifier = table,
        GranteeType = type,
        GranteeSid = new Sid(grantee),
        Effect = effect,
        ConsentRequestId = OpenMetadataSyncService.OpenMetadataSyncConsentMarker,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
        ValidTo = validTo ?? DateTimeOffset.UtcNow.AddDays(300)
    };

    private sealed class OmFixture
    {
        public required OpenMetadataSyncService Service { get; init; }
        public required StubOpenMetadataClient Client { get; init; }
        public required RecordingConsentRepository ConsentRepo { get; init; }
        public required InMemoryTableMetadataRepository MetadataRepo { get; init; }
        public IReadOnlyList<Consent> Created => ConsentRepo.CreatedConsents;
        public IReadOnlyList<Guid> Revoked => ConsentRepo.RevokedIds;
        public IReadOnlyList<TableMetadata> Upserted => MetadataRepo.UpsertedTables;
    }

    private static OmFixture CreateOm(
        OpenMetadataOptions omOptions,
        IReadOnlyList<OpenMetadataTable> tables,
        IReadOnlyList<OpenMetadataRule>? rules = null,
        IReadOnlyList<OpenMetadataRole>? roles = null,
        IReadOnlyList<OpenMetadataTeam>? teams = null,
        IReadOnlyList<OpenMetadataUser>? users = null,
        IReadOnlyList<Consent>? existingSyncConsents = null,
        Func<TableIdentifier, TableMetadata?>? existingMetadata = null,
        bool usersFail = false)
    {
        IReadOnlyList<OpenMetadataRule> effectiveRules = rules ?? [ViewAllTables()];
        IReadOnlyList<OpenMetadataRole> effectiveRoles = roles ?? [new OpenMetadataRole { Name = "OrderReader", Policies = [PolicyRef()] }];
        IReadOnlyList<OpenMetadataTeam> effectiveTeams = teams ?? [];
        IReadOnlyList<OpenMetadataUser> effectiveUsers = users ?? [];

        var client = new StubOpenMetadataClient
        {
            Tables = tables,
            Policies = [new OpenMetadataPolicy { Id = PolicyId, Name = "R4Policy", Enabled = true, Rules = effectiveRules.ToList() }],
            Roles = effectiveRoles,
            Teams = effectiveTeams,
            Users = effectiveUsers,
            OnGetUsers = usersFail ? () => Task.FromException<IReadOnlyList<OpenMetadataUser>>(new HttpRequestException("OpenMetadata returned 503")) : null
        };
        var metadataRepo = new DelegatingTableRepo(existingMetadata);
        var consentRepo = new RecordingConsentRepository(existingSyncConsents?.ToArray() ?? []);
        var epochRepo = new InMemoryPolicyEpochRepository();

        var options = Options.Create(new GatewayOptions { OpenMetadata = omOptions });
        return new OmFixture
        {
            Service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance, clusterState: new InMemoryClusterStateProvider()),
            Client = client,
            ConsentRepo = consentRepo,
            MetadataRepo = metadataRepo.Inner
        };
    }

    private static OpenMetadataOptions Om(
        bool autoCreate = true,
        string serviceFilter = "",
        Dictionary<string, string>? teamMap = null,
        Dictionary<string, string>? userMap = null,
        Dictionary<string, string>? serviceDatabaseMap = null,
        bool activateNewTables = true,
        string webhookSecret = "") => new()
    {
        DefaultTenantId = "default-tenant",
        Enabled = true,
        AutoCreateConsents = autoCreate,
        ServiceFilter = serviceFilter,
        SyncIntervalMinutes = 30,
        WebhookSecret = webhookSecret,
        RoleToGatewayRoleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OrderReader"] = "OrderReaderRole" },
        TeamToGroupSidMap = teamMap ?? new Dictionary<string, string> { ["name:OrderTeam"] = OrderTeamSid },
        UserToUserSidMap = userMap ?? new Dictionary<string, string>(),
        ServiceDatabaseToDomainMap = serviceDatabaseMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ActivateNewTables = activateNewTables
    };

    // ------------------------------------------------------------------ E-05 / EX-02

    [Fact]
    public async Task E05_Reconcile_LoadsAllMarkerConsents_RevokesDeletedGranteesAndFilteredTables()
    {
        var rawOmRole = SyncConsent(OrdersId, GranteeType.Group, "RawOmRoleName");               // legacy consent on a raw OM role name
        var deletedUser = SyncConsent(new TableIdentifier("sales_svc", "dbo", "dropped_table"), GranteeType.User, "S-1-5-21-DELETED");
        var legacyRoleAllow = SyncConsent(OrdersId, GranteeType.Group, "OrderReaderRole");           // an Allow from an earlier sync is no longer desired
        var stillGranted = SyncConsent(OrdersId, GranteeType.Group, OrderTeamSid, validTo: DateTimeOffset.UtcNow.AddMinutes(10));

        var f = CreateOm(Om(), [Orders()], teams: [OrderTeam()], existingSyncConsents: [rawOmRole, deletedUser, legacyRoleAllow, stillGranted]);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        f.Revoked.ShouldBe(new[] { rawOmRole.Id, deletedUser.Id, legacyRoleAllow.Id, stillGranted.Id }, ignoreOrder: true);
        f.Created.Count.ShouldBe(1); // the still granted consent is refreshed via fail-closed revoke + recreate, not duplicated
        f.ConsentRepo.RequestedConsentRequestIds.ShouldBe([OpenMetadataSyncService.OpenMetadataSyncConsentMarker]);
        f.ConsentRepo.UserRevokeCalls.ShouldBe(0);
    }

    [Fact]
    public async Task E05_Migration_AutoCreateDisabled_RevokesExistingAllowMarkerConsents_KeepsDeny()
    {
        var formerAutoGrant = SyncConsent(OrdersId, GranteeType.Group, OrderTeamSid);
        var syncedDeny = SyncConsent(OrdersId, GranteeType.Group, OrderTeamSid, ConsentEffect.Deny);

        var f = CreateOm(
            Om(autoCreate: false),
            [Orders()],
            teams: [OrderTeam()],
            rules: [ViewAllTables(), new OpenMetadataRule { Name = "DenyOrders", Effect = "deny", Resources = ["orders"], Operations = ["ViewAll"] }],
            existingSyncConsents: [formerAutoGrant, syncedDeny]);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        f.Revoked.ShouldHaveSingleItem().ShouldBe(formerAutoGrant.Id);
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task EX02_SyncedDeny_KeepsLongValidity_SoAFailingSyncCannotLetItExpire()
    {
        var f = CreateOm(
            Om(autoCreate: false),
            [Orders()],
            teams: [OrderTeam()],
            rules: [new OpenMetadataRule { Name = "DenyOrders", Effect = "deny", Resources = ["orders"], Operations = ["ViewAll"] }]);

        await f.Service.SyncPermissionsAsync();

        var deny = f.Created.ShouldHaveSingleItem();
        deny.Effect.ShouldBe(ConsentEffect.Deny);
        deny.ValidTo.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(300));
    }

    [Fact]
    public async Task E05_FourEyesTable_ExistingAutoGrantIsRevoked()
    {
        var existing = SyncConsent(OrdersId, GranteeType.Group, "OrderReaderRole");
        var f = CreateOm(Om(), [Orders(tags: [new OpenMetadataTag { TagFQN = "Governance.FourEyes" }])], existingSyncConsents: [existing]);

        await f.Service.SyncPermissionsAsync();

        f.Revoked.ShouldHaveSingleItem().ShouldBe(existing.Id);
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task EX02_PrincipalFetchFailure_ReportsFailure_AndNeitherCreatesNorRevokes()
    {
        var stale = SyncConsent(OrdersId, GranteeType.Group, "RawOmRoleName");
        var f = CreateOm(Om(), [Orders()], existingSyncConsents: [stale], usersFail: true);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Contains("policy/role/team", StringComparison.Ordinal));
        f.Revoked.ShouldBeEmpty();
        f.Created.ShouldBeEmpty();
        f.ConsentRepo.RequestedConsentRequestIds.ShouldBeEmpty();
        f.Upserted.Count.ShouldBe(1); // table metadata sync is independent of the consent block
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task RoleAllowPolicy_NeverCreatesAConsent_RolesDoNotGrantDataAccess()
    {
        var f = CreateOm(Om(), [Orders()]);   // the default OpenMetadata role carries an allow rule

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Security_DbtAndOpenMetadata_RoleGrantee_SkippedNeverMapped_OpenMetadata()
    {
        // An allowlisted role carrying a Deny policy used to become a role grantee; now the entry is skipped, reported and never written.
        var f = CreateOm(
            Om(autoCreate: true),
            [Orders()],
            rules: [new OpenMetadataRule { Name = "DenyOrders", Effect = "deny", Resources = ["orders"], Operations = ["ViewAll"] }]);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        f.Created.ShouldBeEmpty("a role entry is never mapped to a group or user");
        result.Warnings.ShouldContain(w => w.Contains("role grantees are not supported", StringComparison.Ordinal));
        result.Warnings.ShouldNotContain(w => w.Contains("OrderReader", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EX02_NewSyncConsent_IsValidForTwoSyncIntervalsOnly()
    {
        var f = CreateOm(Om(), [Orders()], teams: [OrderTeam()]);
        var before = DateTimeOffset.UtcNow;

        await f.Service.SyncPermissionsAsync();

        var created = f.Created.ShouldHaveSingleItem();
        created.ConsentRequestId.ShouldBe(OpenMetadataSyncService.OpenMetadataSyncConsentMarker);
        created.ValidTo.ShouldBeGreaterThanOrEqualTo(before.AddMinutes(59));
        created.ValidTo.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(61));
    }

    [Fact]
    public async Task EX02_ExistingLongLivedSyncConsent_IsShortenedToRefreshWindow()
    {
        var existing = SyncConsent(OrdersId, GranteeType.Group, OrderTeamSid);
        var f = CreateOm(Om(), [Orders()], teams: [OrderTeam()], existingSyncConsents: [existing]);

        await f.Service.SyncPermissionsAsync();

        // Fail-closed refresh revokes the existing consent (audited) and recreates a fresh grant with the refresh window.
        f.Revoked.ShouldHaveSingleItem().ShouldBe(existing.Id);
        var created = f.Created.ShouldHaveSingleItem();
        created.ConsentRequestId.ShouldBe(OpenMetadataSyncService.OpenMetadataSyncConsentMarker);
        created.GranteeSid!.Value.Value.ShouldBe(OrderTeamSid);
        created.ValidTo.ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(61));
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Sync_ExpiredSyncConsent_IsRecreated_NeverExtended()
    {
        // Expired consents are never returned by GetActiveConsentsByConsentRequestIdAsync, so they fall to the create loop.
        var f = CreateOm(Om(), [Orders()], teams: [OrderTeam()], existingSyncConsents: []);

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldHaveSingleItem().ConsentRequestId.ShouldBe(OpenMetadataSyncService.OpenMetadataSyncConsentMarker);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Sync_ConcurrentlyRevokedConsent_IsNotRecreated()
    {
        var existing = SyncConsent(OrdersId, GranteeType.Group, OrderTeamSid, validTo: DateTimeOffset.UtcNow.AddMinutes(5));
        var f = CreateOm(Om(), [Orders()], teams: [OrderTeam()], existingSyncConsents: [existing]);
        f.ConsentRepo.OnRevokeSystemConsent = id => id != existing.Id;

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PERS_04_Sync_RevokeReturnedFalse_DoesNotCreate()
    {
        var existing = SyncConsent(OrdersId, GranteeType.Group, OrderTeamSid);
        var f = CreateOm(Om(), [Orders()], teams: [OrderTeam()], existingSyncConsents: [existing]);
        f.ConsentRepo.OnRevokeSystemConsent = id => id != existing.Id;

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task EX02_ShortSyncConsent_IsRefreshedViaRevokeAndCreate()
    {
        var existing = SyncConsent(OrdersId, GranteeType.Group, OrderTeamSid, validTo: DateTimeOffset.UtcNow.AddMinutes(5));
        var f = CreateOm(Om(), [Orders()], teams: [OrderTeam()], existingSyncConsents: [existing]);

        await f.Service.SyncPermissionsAsync();

        f.Revoked.ShouldHaveSingleItem().ShouldBe(existing.Id);
        var created = f.Created.ShouldHaveSingleItem();
        created.ConsentRequestId.ShouldBe(OpenMetadataSyncService.OpenMetadataSyncConsentMarker);
    }

    // ------------------------------------------------------------------ E-06

    [Fact]
    public async Task E06_Art9ColumnTag_MarksTableHighFourEyes_AndBlocksAutoGrant()
    {
        var f = CreateOm(Om(), [Orders(columns:
        [
            new OpenMetadataColumn { Name = "id", DataType = "INT" },
            new OpenMetadataColumn { Name = "diagnosis", DataType = "VARCHAR", Tags = [new OpenMetadataTag { TagFQN = "GDPR.Art9" }] }
        ])]);

        await f.Service.SyncPermissionsAsync();

        var meta = f.Upserted.ShouldHaveSingleItem();
        meta.Table.Sensitivity.ShouldBe("HIGH");
        meta.Table.RequiresFourEyes.ShouldBeTrue();
        ClassificationMetadata.IsColumnSensitive(meta, "diagnosis").ShouldBeTrue();
        meta.ColumnMaskingRules["diagnosis"].RuleType.ShouldBe("REDACT");
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task E06_HierarchicalArt9TableTag_IsRecognized()
    {
        var f = CreateOm(Om(), [Orders(tags: [new OpenMetadataTag { TagFQN = "Classification.HealthData" }])]);

        await f.Service.SyncPermissionsAsync();

        var meta = f.Upserted.ShouldHaveSingleItem();
        meta.Table.Sensitivity.ShouldBe("HIGH");
        meta.Table.RequiresFourEyes.ShouldBeTrue();
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task E06_PiiColumnFromCatalogPiiTags_IsSensitive_AndBlocksAutoGrant()
    {
        var f = CreateOm(Om(), [Orders(columns:
        [
            new OpenMetadataColumn { Name = "phone", DataType = "VARCHAR", Tags = [new OpenMetadataTag { TagFQN = "Contact.Phone" }] }
        ])]);

        await f.Service.SyncPermissionsAsync();

        var meta = f.Upserted.ShouldHaveSingleItem();
        ClassificationMetadata.IsColumnSensitive(meta, meta.Columns.Single().ColumnName).ShouldBeTrue();
        meta.ColumnMaskingRules.ShouldContainKey("phone");
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task E06_RestrictedExistingTable_IsNeverAutoGranted()
    {
        var existing = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "sales_svc", SchemaName = "dbo", TableName = "orders", Sensitivity = "RESTRICTED" },
            Columns = [new TableColumn { ColumnName = "id", DataType = "INT" }]
        };
        var f = CreateOm(Om(), [Orders()], existingMetadata: id => id.Equals(OrdersId) ? existing : null);

        await f.Service.SyncPermissionsAsync();

        f.Upserted.ShouldHaveSingleItem().Table.Sensitivity.ShouldBe("RESTRICTED");
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public void E06_IsExcludedFromAutoGrant_OnlyPlainTablesAreGrantable()
    {
        static TableMetadata Meta(string sensitivity, bool fourEyes = false, bool sensitiveColumn = false) => new()
        { ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor(sensitiveColumn ? new[] { "c" } : Array.Empty<string>()),
            Identifier = OrdersId,
            Table = new Table { Sensitivity = sensitivity, RequiresFourEyes = fourEyes },
            Columns = [new TableColumn { ColumnName = "c" }]
        };

        OpenMetadataSyncService.IsExcludedFromAutoGrant(Meta("NORMAL")).ShouldBeFalse();
        OpenMetadataSyncService.IsExcludedFromAutoGrant(Meta("HIGH")).ShouldBeTrue();
        OpenMetadataSyncService.IsExcludedFromAutoGrant(Meta("RESTRICTED")).ShouldBeTrue();
        OpenMetadataSyncService.IsExcludedFromAutoGrant(Meta("ART9")).ShouldBeTrue();
        OpenMetadataSyncService.IsExcludedFromAutoGrant(Meta("NORMAL", fourEyes: true)).ShouldBeTrue();
        OpenMetadataSyncService.IsExcludedFromAutoGrant(Meta("NORMAL", sensitiveColumn: true)).ShouldBeTrue();
    }

    // ------------------------------------------------------------------ E-07

    [Fact]
    public async Task E07_TeamNamedLikeMappedTeamId_DoesNotInheritSid()
    {
        var mappedTeamId = Guid.NewGuid();
        var f = CreateOm(
            Om(teamMap: new Dictionary<string, string> { ["id:" + mappedTeamId] = "S-1-5-21-TEAM-A" }),
            [Orders()],
            roles: [],
            teams:
            [
                new OpenMetadataTeam
                {
                    Id = Guid.NewGuid(),
                    Name = mappedTeamId.ToString(),
                    FullyQualifiedName = mappedTeamId.ToString(),
                    Policies = [PolicyRef()]
                }
            ]);

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldNotContain(c => c.GranteeType == GranteeType.Group);
    }

    [Fact]
    public async Task E07_TeamResolvedById_StillWorks()
    {
        var mappedTeamId = Guid.NewGuid();
        var f = CreateOm(
            Om(teamMap: new Dictionary<string, string> { ["id:" + mappedTeamId] = "S-1-5-21-TEAM-A" }),
            [Orders()],
            roles: [],
            teams: [new OpenMetadataTeam { Id = mappedTeamId, Name = "renamed-team", Policies = [PolicyRef()] }]);

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldContain(c => c.GranteeType == GranteeType.Group && c.GranteeSid!.Value.Value == "S-1-5-21-TEAM-A");
    }

    [Fact]
    public async Task E07_EmailDifferingOnlyInCase_AcrossOmUsers_IsAmbiguous_AndNotMapped()
    {
        var reader = new OpenMetadataEntityReference { Name = "OrderReader" };
        var f = CreateOm(
            Om(userMap: new Dictionary<string, string> { ["alice@corp.local"] = "S-1-5-21-ALICE" }),
            [Orders()],
            users:
            [
                new OpenMetadataUser { Id = Guid.NewGuid(), Name = "alice", Email = "alice@corp.local", Roles = [reader] },
                new OpenMetadataUser { Id = Guid.NewGuid(), Name = "mallory", Email = "ALICE@CORP.LOCAL", Roles = [reader] }
            ]);

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldNotContain(c => c.GranteeType == GranteeType.User);
    }

    [Fact]
    public async Task E07_EmailNormalization_IsCaseInsensitiveOnBothSides()
    {
        var f = CreateOm(
            Om(userMap: new Dictionary<string, string> { ["Alice@Corp.Local"] = "S-1-5-21-ALICE" }),
            [Orders()],
            users: [new OpenMetadataUser { Id = Guid.NewGuid(), Name = "alice", Email = "alice@corp.local", Roles = [new OpenMetadataEntityReference { Name = "OrderReader" }] }]);

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldContain(c => c.GranteeType == GranteeType.User && c.GranteeSid!.Value.Value == "S-1-5-21-ALICE");
    }

    [Fact]
    public async Task E07_UserName_IsNeverUsedForSidResolution()
    {
        var f = CreateOm(
            Om(userMap: new Dictionary<string, string> { ["admin"] = "S-1-5-21-ADMIN" }),
            [Orders()],
            users: [new OpenMetadataUser { Id = Guid.NewGuid(), Name = "admin", Email = "attacker@evil.local", Roles = [new OpenMetadataEntityReference { Name = "OrderReader" }] }]);

        await f.Service.SyncPermissionsAsync();

        f.Created.ShouldNotContain(c => c.GranteeType == GranteeType.User);
    }

    [Fact]
    public async Task E07_TeamAndUserGrantees_ResolvedFromSeparateKeyMaps()
    {
        var bobId = Guid.NewGuid();
        var f = CreateOm(
            Om(
                teamMap: new Dictionary<string, string> { ["name:FinanceTeam"] = "S-1-5-21-FINANCE" },
                userMap: new Dictionary<string, string> { [$"id:{bobId}"] = "S-1-5-21-BOB" }),
            [Orders()],
            teams: [new OpenMetadataTeam { Id = Guid.NewGuid(), Name = "FinanceTeam", Policies = [PolicyRef()] }],
            users: [new OpenMetadataUser { Id = bobId, Name = "bob", Email = "bob@corp.local", Roles = [new OpenMetadataEntityReference { Name = "OrderReader" }] }]);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        f.Created.ShouldContain(c => c.GranteeType == GranteeType.Group && c.GranteeSid!.Value.Value == "S-1-5-21-FINANCE");
        f.Created.ShouldContain(c => c.GranteeType == GranteeType.User && c.GranteeSid!.Value.Value == "S-1-5-21-BOB");
    }

    // ------------------------------------------------------------------ E-09 / EX-06 (OpenMetadata sync, webhook)

    [Fact]
    public async Task E09_Sync_ServiceFilter_IsAppliedPerTable()
    {
        var f = CreateOm(Om(serviceFilter: "sales_svc"), [Orders(), OmTable("other_svc", "db", "dbo", "secrets")]);

        var result = await f.Service.SyncPermissionsAsync();

        f.Upserted.ShouldHaveSingleItem().Identifier.ShouldBe(OrdersId);
        result.Warnings.ShouldContain(w => w.Contains("ServiceFilter", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EX06_Sync_SameSchemaTableInDifferentDatabases_IsRejected_WithoutDomainMap()
    {
        var f = CreateOm(Om(), [OmTable("svc", "prod", "public", "users"), OmTable("svc", "sandbox", "public", "users")]);

        var result = await f.Service.SyncPermissionsAsync();

        f.Upserted.ShouldBeEmpty();
        result.Warnings.ShouldContain(w => w.Contains("different databases", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EX06_Sync_ServiceDatabaseMap_SeparatesDatabases_AndRejectsUnmapped()
    {
        var f = CreateOm(
            Om(serviceDatabaseMap: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["svc.prod"] = "svc_prod" }),
            [OmTable("svc", "prod", "public", "users"), OmTable("svc", "sandbox", "public", "users")]);

        await f.Service.SyncPermissionsAsync();

        f.Upserted.ShouldHaveSingleItem().Identifier.ShouldBe(new TableIdentifier("svc_prod", "public", "users"));
    }

    [Fact]
    public async Task E09_Sync_NewTablesAreInactive_ExistingKeepState_OptInActivates()
    {
        var inactive = CreateOm(Om(activateNewTables: false), [Orders()]);
        await inactive.Service.SyncPermissionsAsync();
        inactive.Upserted.ShouldHaveSingleItem().Table.IsActive.ShouldBeFalse();

        var existing = new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table { SourceName = "sales_svc", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "INT" }]
        };
        var known = CreateOm(Om(), [Orders()], existingMetadata: _ => existing);
        await known.Service.SyncPermissionsAsync();
        known.Upserted.ShouldHaveSingleItem().Table.IsActive.ShouldBeTrue();

        var optIn = CreateOm(Om(activateNewTables: true), [Orders()]);
        await optIn.Service.SyncPermissionsAsync();
        optIn.Upserted.ShouldHaveSingleItem().Table.IsActive.ShouldBeTrue();
    }

    private static string TableEvent(string fqn) =>
        $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{fqn}\"}}";

    [Fact]
    public async Task EX06_Webhook_TableOutsideServiceFilter_IsIgnored()
    {
        const string secret = "r4-om-webhook-secret";
        var foreign = OmTable("other_svc", "db", "dbo", "secrets");
        var f = CreateOm(Om(serviceFilter: "sales_svc", webhookSecret: secret), []);
        f.Client.Tables = [foreign];

        var payload = TableEvent(foreign.FullyQualifiedName);
        var outcome = await f.Service.HandleVerifiedAsync(payload);

        outcome.ShouldBe(OpenMetadataWebhookOutcome.Processed);
        f.Upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task E09_Webhook_NewTable_IsCreatedInactive()
    {
        const string secret = "r4-om-webhook-secret-2";
        var orders = Orders();
        var f = CreateOm(Om(serviceFilter: "sales_svc", webhookSecret: secret, activateNewTables: false), []);
        f.Client.Tables = [orders];

        var payload = TableEvent(orders.FullyQualifiedName);
        var outcome = await f.Service.HandleVerifiedAsync(payload);

        outcome.ShouldBe(OpenMetadataWebhookOutcome.Processed);
        var meta = f.Upserted.ShouldHaveSingleItem();
        meta.Identifier.ShouldBe(OrdersId);
        meta.Table.IsActive.ShouldBeFalse();
    }

    private static TableMetadata OrdersBoundTo(string sourceName) => new()
    {
        Identifier = OrdersId,
        Table = new Table { SourceName = sourceName, SchemaName = "dbo", TableName = "orders", IsActive = true },
        Columns = [new TableColumn { ColumnName = "id", DataType = "INT" }]
    };

    [Fact]
    public async Task EXT2_Sync_KeepsThePersistedSourceName()
    {
        var f = CreateOm(Om(), [Orders()], existingMetadata: _ => OrdersBoundTo("sales_readonly_conn"));

        await f.Service.SyncPermissionsAsync();

        f.Upserted.ShouldHaveSingleItem().Table.SourceName.ShouldBe("sales_readonly_conn");
    }

    [Fact]
    public async Task EXT2_Webhook_KeepsThePersistedSourceName()
    {
        const string secret = "r4-om-webhook-secret-ext2";
        var orders = Orders();
        var f = CreateOm(Om(serviceFilter: "sales_svc", webhookSecret: secret), [], existingMetadata: _ => OrdersBoundTo("sales_readonly_conn"));
        f.Client.Tables = [orders];

        var payload = TableEvent(orders.FullyQualifiedName);
        (await f.Service.HandleVerifiedAsync(payload)).ShouldBe(OpenMetadataWebhookOutcome.Processed);

        f.Upserted.ShouldHaveSingleItem().Table.SourceName.ShouldBe("sales_readonly_conn");
    }

    // ------------------------------------------------------------------ E-09 / EX-06 (OpenMetadata catalog provider)

    private static OpenMetadataCatalogAdapter CreateAdapter(OpenMetadataOptions omOptions, IReadOnlyList<OpenMetadataTable> tables)
    {
        var client = new StubOpenMetadataClient { Tables = tables };
        return new OpenMetadataCatalogAdapter(client, NullLogger<OpenMetadataCatalogAdapter>.Instance,
            Options.Create(new GatewayOptions { OpenMetadata = omOptions }));
    }

    [Fact]
    public async Task E09_CatalogAdapter_AppliesServiceFilter()
    {
        var adapter = CreateAdapter(Om(serviceFilter: "sales_svc"), [Orders(), OmTable("other_svc", "db", "dbo", "secrets")]);

        var tables = await adapter.GetTablesAsync();

        tables.ShouldHaveSingleItem().Identifier.ShouldBe(OrdersId);
    }

    [Fact]
    public async Task EX06_CatalogAdapter_RejectsDatabaseCollisions_AndHonoursDomainMap()
    {
        var collisions = CreateAdapter(Om(), [OmTable("svc", "prod", "public", "users"), OmTable("svc", "sandbox", "public", "users"), Orders()]);
        (await collisions.GetTablesAsync()).ShouldHaveSingleItem().Identifier.ShouldBe(OrdersId);

        var mapped = CreateAdapter(
            Om(serviceDatabaseMap: new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["svc.prod"] = "svc_prod", ["svc.sandbox"] = "svc_sandbox" }),
            [OmTable("svc", "prod", "public", "users"), OmTable("svc", "sandbox", "public", "users"), Orders()]);
        var ids = (await mapped.GetTablesAsync()).Select(t => t.Identifier).ToList();
        ids.Count.ShouldBe(2);
        ids.ShouldContain(new TableIdentifier("svc_prod", "public", "users"));
        ids.ShouldContain(new TableIdentifier("svc_sandbox", "public", "users"));
    }

    [Theory]
    [InlineData(DataCatalogProviderType.OpenMetadata, false, false)]
    [InlineData(DataCatalogProviderType.OpenMetadata, true, true)]
    public async Task E09_CatalogSync_NewOpenMetadataTables_AreInactiveByDefault(DataCatalogProviderType provider, bool activateNewTables, bool expectedActive)
    {
        var client = new StubDataCatalogClient(provider) { Tables = [new CatalogTableAsset { Identifier = OrdersId }] };
        var repo = new InMemoryTableMetadataRepository();

        var sut = new DataCatalogSyncService(
            client,
            repo,
            new FakeEpochValidationService(),
            Options.Create(new GatewayOptions
            {
                Catalog = new DataCatalogOptions { Provider = provider, ActivateNewTables = activateNewTables },
                OpenMetadata = new OpenMetadataOptions { DefaultTenantId = "default-tenant", ActivateNewTables = activateNewTables }
            }),
            new InMemoryAuditWriter(), NullLogger<DataCatalogSyncService>.Instance);

        await sut.SyncCatalogAsync();

        var upserted = repo.UpsertedTables.ShouldHaveSingleItem();
        upserted.Table.IsActive.ShouldBe(expectedActive);
    }

    // ------------------------------------------------------------------ EX-16

    private static IHostEnvironment Env(string name)
    {
        IHostEnvironment env = new TestHostEnvironment(name);
        return env;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public void EX16_OpenMetadataClient_SecretLookupFailure_LogsNoExceptionMessage()
    {
        var provider = new InMemoryKeyVaultSecretProvider(_ => new InvalidOperationException("Secret 'raw-om-token-ABC' could not be resolved"));
        var logger = new CapturingLogger<OpenMetadataClient>();
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { DefaultTenantId = "default-tenant", ServerUrl = "https://om.corp.example/api/v1", AuthToken = "raw-om-token-ABC" }
        });

        _ = new OpenMetadataClient(new HttpClient(new StubHttpMessageHandler(_ => Json("{}"))), options, logger, provider, Env(Environments.Development));

        logger.Entries.ShouldNotBeEmpty();
        logger.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("raw-om-token-ABC"));
    }

    private sealed class DelegatingTableRepo(Func<TableIdentifier, TableMetadata?>? existing) : ITableMetadataRepository
    {
        public InMemoryTableMetadataRepository Inner { get; } = new();
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(existing?.Invoke(table) ?? Inner.Peek(table));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Inner.GetAllTablesAsync(ct);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
            => Inner.UpsertTableMetadataAsync(metadata, ct);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default)
            => Inner.ReplaceTableKeyDefinitionAsync(table, primaryKey, indexes, primaryKeyDirections, ct);
    }
}
