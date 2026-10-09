namespace Autheris.Extensions.Tests;

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
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.DataCatalog;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// Round 4 (FIX-O): E-05..E-09 and EX-02 / EX-06 / EX-16 – OpenMetadata reconcile, Art. 9 mapping, SID resolution,
/// Alation client hardening and the database-aware OpenMetadata identity.
/// </summary>
public sealed class Round4OpenMetadataTests
{
    private static readonly Guid PolicyId = new("6c1f2b0e-3a4d-4e5f-8a9b-0c1d2e3f4a5b");
    private static readonly TableIdentifier OrdersId = new("sales_svc", "dbo", "orders");

    // ------------------------------------------------------------------ helpers

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }

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

    private static OpenMetadataEntityReference PolicyRef() => new() { Id = PolicyId, Name = "R4Policy" };

    private static Consent SyncConsent(TableIdentifier table, GranteeType type, string grantee, ConsentEffect effect = ConsentEffect.Allow) => new()
    {
        Id = Guid.NewGuid(),
        TableIdentifier = table,
        GranteeType = type,
        RoleName = type == GranteeType.Role ? grantee : null,
        GranteeSid = type == GranteeType.Role ? (Sid?)null : new Sid(grantee),
        Effect = effect,
        ConsentRequestId = OpenMetadataSyncService.OpenMetadataSyncConsentMarker,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-30),
        ValidTo = DateTimeOffset.UtcNow.AddDays(300)
    };

    private sealed class OmFixture
    {
        public required OpenMetadataSyncService Service { get; init; }
        public required IOpenMetadataClient Client { get; init; }
        public required IConsentRepository ConsentRepo { get; init; }
        public List<Consent> Created { get; } = [];
        public List<Guid> Revoked { get; } = [];
        public List<TableMetadata> Upserted { get; } = [];
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
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        IReadOnlyList<OpenMetadataRule> effectiveRules = rules ?? [ViewAllTables()];
        IReadOnlyList<OpenMetadataRole> effectiveRoles = roles ?? [new OpenMetadataRole { Name = "OrderReader", Policies = [PolicyRef()] }];
        IReadOnlyList<OpenMetadataTeam> effectiveTeams = teams ?? [];
        IReadOnlyList<OpenMetadataUser> effectiveUsers = users ?? [];
        IReadOnlyList<Consent> effectiveExisting = existingSyncConsents ?? [];

        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(tables));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
                new OpenMetadataPolicy { Id = PolicyId, Name = "R4Policy", Enabled = true, Rules = effectiveRules.ToList() }
            ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(effectiveRoles));
        client.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(effectiveTeams));
        if (usersFail)
        {
            client.GetUsersAsync(Arg.Any<CancellationToken>())
                .Returns<Task<IReadOnlyList<OpenMetadataUser>>>(_ => throw new HttpRequestException("OpenMetadata returned 503"));
        }
        else
        {
            client.GetUsersAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(effectiveUsers));
        }

        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(existingMetadata?.Invoke(ci.Arg<TableIdentifier>())));

        consentRepo.GetActiveConsentsByConsentRequestIdAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(effectiveExisting));

        var options = Options.Create(new GatewayOptions { OpenMetadata = omOptions });
        var fixture = new OmFixture
        {
            Service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance),
            Client = client,
            ConsentRepo = consentRepo
        };

        metadataRepo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var m = ci.Arg<TableMetadata>();
                fixture.Upserted.Add(m);
                return Task.FromResult(m);
            });
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var c = ci.Arg<Consent>();
                fixture.Created.Add(c);
                return Task.FromResult(c);
            });
        consentRepo.RevokeSystemConsentAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                fixture.Revoked.Add(ci.ArgAt<Guid>(0));
                return Task.FromResult(true);
            });

        return fixture;
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
        Enabled = true,
        AutoCreateConsents = autoCreate,
        ServiceFilter = serviceFilter,
        SyncIntervalMinutes = 30,
        WebhookSecret = webhookSecret,
        RoleToGatewayRoleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["OrderReader"] = "OrderReaderRole" },
        TeamToGroupSidMap = teamMap ?? new Dictionary<string, string>(),
        UserToUserSidMap = userMap ?? new Dictionary<string, string>(),
        ServiceDatabaseToDomainMap = serviceDatabaseMap ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        ActivateNewTables = activateNewTables
    };

    // ------------------------------------------------------------------ E-05 / EX-02

    [Fact]
    public async Task E05_Reconcile_LoadsAllMarkerConsents_RevokesDeletedGranteesAndFilteredTables()
    {
        var rawOmRole = SyncConsent(OrdersId, GranteeType.Role, "RawOmRoleName");               // legacy consent on a raw OM role name
        var deletedUser = SyncConsent(new TableIdentifier("sales_svc", "dbo", "dropped_table"), GranteeType.User, "S-1-5-21-DELETED");
        var stillGranted = SyncConsent(OrdersId, GranteeType.Role, "OrderReaderRole");

        var f = CreateOm(Om(), [Orders()], existingSyncConsents: [rawOmRole, deletedUser, stillGranted]);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        f.Revoked.ShouldBe(new[] { rawOmRole.Id, deletedUser.Id }, ignoreOrder: true);
        f.Created.ShouldBeEmpty(); // the still granted consent is refreshed, not duplicated
        await f.ConsentRepo.Received(1).GetActiveConsentsByConsentRequestIdAsync(
            OpenMetadataSyncService.OpenMetadataSyncConsentMarker, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await f.ConsentRepo.DidNotReceive().RevokeConsentAsync(Arg.Any<Guid>(), Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task E05_Migration_AutoCreateDisabled_RevokesExistingAllowMarkerConsents_KeepsDeny()
    {
        var formerAutoGrant = SyncConsent(OrdersId, GranteeType.Role, "OrderReaderRole");
        var syncedDeny = SyncConsent(OrdersId, GranteeType.Role, "OrderReaderRole", ConsentEffect.Deny);

        var f = CreateOm(
            Om(autoCreate: false),
            [Orders()],
            rules: [ViewAllTables(), new OpenMetadataRule { Name = "DenyOrders", Effect = "deny", Resources = ["orders"], Operations = ["ViewAll"] }],
            existingSyncConsents: [formerAutoGrant, syncedDeny]);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeTrue();
        f.Revoked.ShouldHaveSingleItem().ShouldBe(formerAutoGrant.Id);
        f.Created.ShouldBeEmpty();
        // The deny stays (long-lived, still far from expiry -> no refresh needed).
        await f.ConsentRepo.DidNotReceive().ExtendConsentExpiryAsync(syncedDeny.Id, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EX02_SyncedDeny_KeepsLongValidity_SoAFailingSyncCannotLetItExpire()
    {
        var f = CreateOm(
            Om(autoCreate: false),
            [Orders()],
            rules: [new OpenMetadataRule { Name = "DenyOrders", Effect = "deny", Resources = ["orders"], Operations = ["ViewAll"] }]);

        await f.Service.SyncPermissionsAsync();

        var deny = f.Created.ShouldHaveSingleItem();
        deny.Effect.ShouldBe(ConsentEffect.Deny);
        deny.ValidTo.ShouldBeGreaterThan(DateTimeOffset.UtcNow.AddDays(300));
    }

    [Fact]
    public async Task E05_FourEyesTable_ExistingAutoGrantIsRevoked()
    {
        var existing = SyncConsent(OrdersId, GranteeType.Role, "OrderReaderRole");
        var f = CreateOm(Om(), [Orders(tags: [new OpenMetadataTag { TagFQN = "Governance.FourEyes" }])], existingSyncConsents: [existing]);

        await f.Service.SyncPermissionsAsync();

        f.Revoked.ShouldHaveSingleItem().ShouldBe(existing.Id);
        f.Created.ShouldBeEmpty();
    }

    [Fact]
    public async Task EX02_PrincipalFetchFailure_ReportsFailure_AndNeitherCreatesNorRevokes()
    {
        var stale = SyncConsent(OrdersId, GranteeType.Role, "RawOmRoleName");
        var f = CreateOm(Om(), [Orders()], existingSyncConsents: [stale], usersFail: true);

        var result = await f.Service.SyncPermissionsAsync();

        result.Success.ShouldBeFalse();
        result.Warnings.ShouldContain(w => w.Contains("policy/role/team", StringComparison.Ordinal));
        f.Revoked.ShouldBeEmpty();
        f.Created.ShouldBeEmpty();
        await f.ConsentRepo.DidNotReceive().GetActiveConsentsByConsentRequestIdAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await f.ConsentRepo.DidNotReceive().ExtendConsentExpiryAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        f.Upserted.Count.ShouldBe(1); // table metadata sync is independent of the consent block
    }

    [Fact]
    public async Task EX02_NewSyncConsent_IsValidForTwoSyncIntervalsOnly()
    {
        var f = CreateOm(Om(), [Orders()]);
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
        var existing = SyncConsent(OrdersId, GranteeType.Role, "OrderReaderRole");
        var f = CreateOm(Om(), [Orders()], existingSyncConsents: [existing]);

        await f.Service.SyncPermissionsAsync();

        var limit = DateTimeOffset.UtcNow.AddMinutes(61);
        await f.ConsentRepo.Received(1).ExtendConsentExpiryAsync(
            existing.Id, Arg.Is<DateTimeOffset>(d => d <= limit), Arg.Any<CancellationToken>());
        f.Revoked.ShouldBeEmpty();
        f.Created.ShouldBeEmpty();
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
        meta.Columns.Single(c => c.ColumnName == "diagnosis").IsSensitive.ShouldBeTrue();
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
        meta.Columns.Single().IsSensitive.ShouldBeTrue();
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
        {
            Identifier = OrdersId,
            Table = new Table { Sensitivity = sensitivity, RequiresFourEyes = fourEyes },
            Columns = [new TableColumn { ColumnName = "c", IsSensitive = sensitiveColumn }]
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
            Om(teamMap: new Dictionary<string, string> { [mappedTeamId.ToString()] = "S-1-5-21-TEAM-A" }),
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
            Om(teamMap: new Dictionary<string, string> { [mappedTeamId.ToString()] = "S-1-5-21-TEAM-A" }),
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
        f.Created.ShouldContain(c => c.GranteeType == GranteeType.Role && c.RoleName == "OrderReaderRole");
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

    private static string SignedTableEvent(string fqn, string secret, out string signature)
    {
        var payload = $"{{\"id\":\"{Guid.NewGuid()}\",\"timestamp\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()},\"eventType\":\"entityUpdated\",\"entityType\":\"table\",\"entityFullyQualifiedName\":\"{fqn}\"}}";
        signature = "sha256=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(payload)));
        return payload;
    }

    [Fact]
    public async Task EX06_Webhook_TableOutsideServiceFilter_IsIgnored()
    {
        const string secret = "r4-om-webhook-secret";
        var foreign = OmTable("other_svc", "db", "dbo", "secrets");
        var f = CreateOm(Om(serviceFilter: "sales_svc", webhookSecret: secret), []);
        f.Client.GetTableByFqnAsync(foreign.FullyQualifiedName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(foreign));

        var payload = SignedTableEvent(foreign.FullyQualifiedName, secret, out var signature);
        var ok = await f.Service.HandleWebhookEventAsync(payload, signature);

        ok.ShouldBeTrue();
        f.Upserted.ShouldBeEmpty();
    }

    [Fact]
    public async Task E09_Webhook_NewTable_IsCreatedInactive()
    {
        const string secret = "r4-om-webhook-secret-2";
        var orders = Orders();
        var f = CreateOm(Om(serviceFilter: "sales_svc", webhookSecret: secret, activateNewTables: false), []);
        f.Client.GetTableByFqnAsync(orders.FullyQualifiedName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(orders));

        var payload = SignedTableEvent(orders.FullyQualifiedName, secret, out var signature);
        var ok = await f.Service.HandleWebhookEventAsync(payload, signature);

        ok.ShouldBeTrue();
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
        f.Client.GetTableByFqnAsync(orders.FullyQualifiedName, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OpenMetadataTable?>(orders));

        var payload = SignedTableEvent(orders.FullyQualifiedName, secret, out var signature);
        (await f.Service.HandleWebhookEventAsync(payload, signature)).ShouldBeTrue();

        f.Upserted.ShouldHaveSingleItem().Table.SourceName.ShouldBe("sales_readonly_conn");
    }

    // ------------------------------------------------------------------ E-09 / EX-06 (OpenMetadata catalog provider)

    private static OpenMetadataCatalogAdapter CreateAdapter(OpenMetadataOptions omOptions, IReadOnlyList<OpenMetadataTable> tables)
    {
        var client = Substitute.For<IOpenMetadataClient>();
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(tables));
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
    [InlineData(DataCatalogProviderType.Collibra, false, false)]
    [InlineData(DataCatalogProviderType.Collibra, true, true)]
    public async Task E09_CatalogSync_NewOpenMetadataTables_AreInactiveByDefault(DataCatalogProviderType provider, bool activateNewTables, bool expectedActive)
    {
        var client = Substitute.For<IDataCatalogClient>();
        client.ProviderType.Returns(provider);
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CatalogTableAsset>>([new CatalogTableAsset { Identifier = OrdersId }]));
        var factory = Substitute.For<IDataCatalogClientFactory>();
        factory.GetActiveClient().Returns(client);

        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<TableMetadata?>(null));
        TableMetadata? upserted = null;
        repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                upserted = ci.Arg<TableMetadata>();
                return Task.FromResult(upserted);
            });

        var sut = new DataCatalogSyncService(
            factory,
            repo,
            Substitute.For<IEpochValidationService>(),
            Options.Create(new GatewayOptions
            {
                Catalog = new DataCatalogOptions { Provider = provider, ActivateNewTables = activateNewTables },
                OpenMetadata = new OpenMetadataOptions { ActivateNewTables = activateNewTables }
            }),
            NullLogger<DataCatalogSyncService>.Instance);

        await sut.SyncCatalogAsync();

        upserted.ShouldNotBeNull();
        upserted.Table.IsActive.ShouldBe(expectedActive);
    }

    // ------------------------------------------------------------------ E-08 (Alation) / EX-16

    private static IHostEnvironment Env(string name)
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns(name);
        return env;
    }

    private static AlationCatalogClient CreateAlation(
        StubHandler handler,
        IKeyVaultSecretProvider? secretProvider,
        IHostEnvironment? environment,
        ILogger<AlationCatalogClient>? logger = null,
        bool allowPlaintextInDevelopment = false)
    {
        var options = Options.Create(new GatewayOptions
        {
            Catalog = new DataCatalogOptions
            {
                Provider = DataCatalogProviderType.Alation,
                Alation = new AlationOptions
                {
                    BaseUrl = "https://alation.corp.example",
                    ApiToken = "alation-token-ref",
                    AllowPlaintextApiTokenInDevelopment = allowPlaintextInDevelopment,
                    DataSourceToDomainMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["7"] = "clinic" }
                }
            }
        });

        return new AlationCatalogClient(new HttpClient(handler), options, logger ?? NullLogger<AlationCatalogClient>.Instance, secretProvider, environment);
    }

    private static IKeyVaultSecretProvider ResolvingProvider()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes("alation-token-ref").Returns(Encoding.UTF8.GetBytes("resolved-alation-token"));
        return provider;
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task E08_Alation_SecretLookupFailure_InDevelopment_FailsClosed_AndLogsNoExceptionMessage()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("Secret 'raw-alation-token-XYZ' not found"));
        var logger = new RecordingLogger<AlationCatalogClient>();

        var client = CreateAlation(handler, provider, Env(Environments.Development), logger);

        await Should.ThrowAsync<SecurityException>(() => client.GetTablesAsync());
        handler.Requests.ShouldBeEmpty();
        logger.Entries.ShouldNotBeEmpty();
        logger.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("raw-alation-token-XYZ"));
    }

    [Fact]
    public async Task E08_Alation_EmptySecret_FailsClosed()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(Array.Empty<byte>());

        var client = CreateAlation(handler, provider, Env(Environments.Production));

        await Should.ThrowAsync<SecurityException>(() => client.GetTablesAsync());
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task E08_Alation_MissingSecretProvider_FailsClosed()
    {
        var handler = new StubHandler(_ => Json("[]"));
        var client = CreateAlation(handler, secretProvider: null, environment: null);

        await Should.ThrowAsync<SecurityException>(() => client.GetTablesAsync());
        handler.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task E08_Alation_DevelopmentPlaceholder_RequiresExplicitPlaintextOptIn()
    {
        // DefaultEnvironmentSecretProvider returns the reference itself in Development – that is not a resolved secret.
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes("alation-token-ref").Returns(Encoding.UTF8.GetBytes("alation-token-ref"));

        var blockedHandler = new StubHandler(_ => Json("[]"));
        var blocked = CreateAlation(blockedHandler, provider, Env(Environments.Development));
        await Should.ThrowAsync<SecurityException>(() => blocked.GetTablesAsync());
        blockedHandler.Requests.ShouldBeEmpty();

        var allowedHandler = new StubHandler(_ => Json("[]"));
        var allowed = CreateAlation(allowedHandler, provider, Env(Environments.Development), allowPlaintextInDevelopment: true);
        await allowed.GetTablesAsync();
        allowedHandler.Requests.ShouldHaveSingleItem().Headers.GetValues("TOKEN").Single().ShouldBe("alation-token-ref");

        var prodHandler = new StubHandler(_ => Json("[]"));
        var prod = CreateAlation(prodHandler, provider, Env(Environments.Production), allowPlaintextInDevelopment: true);
        await Should.ThrowAsync<SecurityException>(() => prod.GetTablesAsync());
    }

    [Fact]
    public async Task E08_Alation_PagesUntilShortPage()
    {
        static string Page(int count, int offset) =>
            "[" + string.Join(",", Enumerable.Range(offset, count).Select(i => $"{{\"id\":{i},\"name\":\"t{i}\",\"schema_name\":\"s\",\"ds_id\":7}}")) + "]";

        var handler = new StubHandler(req =>
            req.RequestUri!.Query.Contains($"skip={AlationCatalogClient.PageSize}", StringComparison.Ordinal)
                ? Json(Page(1, AlationCatalogClient.PageSize))
                : Json(Page(AlationCatalogClient.PageSize, 0)));

        var client = CreateAlation(handler, ResolvingProvider(), Env(Environments.Production));
        var tables = await client.GetTablesAsync();

        handler.Requests.Count.ShouldBe(2);
        handler.Requests[0].RequestUri!.Query.ShouldContain("skip=0");
        handler.Requests[1].RequestUri!.Query.ShouldContain($"skip={AlationCatalogClient.PageSize}");
        tables.Count.ShouldBe(AlationCatalogClient.PageSize + 1);
        handler.Requests[0].Headers.GetValues("TOKEN").Single().ShouldBe("resolved-alation-token");
    }

    [Fact]
    public async Task E08_Alation_UnmappedAndMalformedDataSources_AreDiscarded()
    {
        var handler = new StubHandler(_ => Json(
            """
            [
              {"id":1,"name":"patients","schema_name":"clinical","ds_id":7},
              {"id":2,"name":"ghost","schema_name":"clinical","ds_id":8},
              {"id":3,"name":"inject","schema_name":"clinical","ds_id":"7; drop"},
              {"id":4,"name":"nods","schema_name":"clinical"}
            ]
            """));

        var client = CreateAlation(handler, ResolvingProvider(), Env(Environments.Production));
        var tables = await client.GetTablesAsync();

        tables.ShouldHaveSingleItem().Identifier.ShouldBe(new TableIdentifier("clinic", "clinical", "patients"));
    }

    [Fact]
    public void EX16_OpenMetadataClient_SecretLookupFailure_LogsNoExceptionMessage()
    {
        var provider = Substitute.For<IKeyVaultSecretProvider>();
        provider.GetSecretBytes(Arg.Any<string>()).Returns(_ => throw new InvalidOperationException("Secret 'raw-om-token-ABC' could not be resolved"));
        var logger = new RecordingLogger<OpenMetadataClient>();
        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions { ServerUrl = "https://om.corp.example/api/v1", AuthToken = "raw-om-token-ABC" }
        });

        _ = new OpenMetadataClient(new HttpClient(new StubHandler(_ => Json("{}"))), options, logger, provider, Env(Environments.Development));

        logger.Entries.ShouldNotBeEmpty();
        logger.Entries.ShouldAllBe(e => e.Exception == null && !e.Message.Contains("raw-om-token-ABC"));
    }
}
