namespace Autheris.Tests.Unit.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class PdpPreconditionAndExplainTests
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TableIdentifier TableId = new("default", "dbo", "orders");
    private static readonly Sid UserSid = new("S-1-5-21-1-2-3-1001");
    private static readonly Sid GroupSid = new("S-1-5-21-1-2-3-2001");
    private static readonly TenantId Tenant = new("tenant-a");

    private static TableMetadata ActiveTable(bool isActive = true) => new()
    { ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email", "secret_note"),
        Identifier = TableId,
        Table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = "default",
            SchemaName = "dbo",
            TableName = "orders",
            IsActive = isActive
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "email", DataType = "varchar" },
            new TableColumn { ColumnName = "secret_note", DataType = "varchar" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new MaskingRule { RuleType = "MASK_EMAIL" }
        }
    };

    private static SecurityPrincipalContext Subject(
        bool isAuthenticated = true,
        string userSid = "S-1-5-21-1-2-3-1001",
        TenantId? tenant = null,
        bool tenantClaimInvalid = false,
        GroupResolutionStatus groupStatus = GroupResolutionStatus.Complete,
        ClaimsPrincipal? principal = null) => new()
    {
        IsAuthenticated = isAuthenticated,
        UserSid = new Sid(userSid),
        ResolvedTenant = tenant,
        TenantClaimInvalid = tenantClaimInvalid,
        GroupSids = groupStatus == GroupResolutionStatus.Complete ? new HashSet<Sid>([GroupSid]) : new HashSet<Sid>(),
        GroupResolutionStatus = groupStatus,
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = isAuthenticated ? "Bearer" : "Anonymous",
        Principal = principal
    };

    [Fact]
    public async Task FromPrincipal_without_pipeline_marker_is_degraded()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", UserSid.Value),
            new Claim("tenant_id", Tenant.Value),
            new Claim("groups", GroupSid.Value)
        ], "Bearer"));

        var ctx = SecurityPrincipalContext.FromPrincipal(principal);

        ctx.GroupResolutionStatus.ShouldBe(GroupResolutionStatus.Degraded);

        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true }
        }));

        var decision = await pdp.DecideAsync(new AccessRequest(ctx, ActiveTable(), AccessOperation.Read, AccessTransport.Rest));

        decision.IsAllowed.ShouldBeFalse();
        decision.Reasons.ShouldContain(r => r.Step == DecisionStep.Precondition && r.Code == "GROUPS_DEGRADED");
    }

    [Fact]
    public async Task Principal_less_degraded_subject_denied_with_GROUPS_DEGRADED()
    {
        var subject = Subject(tenant: Tenant, groupStatus: GroupResolutionStatus.Degraded, principal: null);
        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()));

        var decision = await pdp.DecideAsync(new AccessRequest(subject, ActiveTable(), AccessOperation.Read, AccessTransport.Rest));

        decision.IsAllowed.ShouldBeFalse();
        decision.Reasons.ShouldContain(r => r.Step == DecisionStep.Precondition && r.Code == "GROUPS_DEGRADED");
    }

    [Fact]
    public async Task Preconditions_evaluated_identity_tenant_groups_before_table_state()
    {
        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()));
        var inactive = ActiveTable(isActive: false);

        var anon = await pdp.DecideAsync(new AccessRequest(
            Subject(isAuthenticated: false, userSid: "", tenant: null, groupStatus: GroupResolutionStatus.Complete),
            inactive,
            AccessOperation.Insert,
            AccessTransport.Rest));
        anon.IsAllowed.ShouldBeFalse();
        anon.Reasons.Select(r => r.Code).ShouldBe(["IDENTITY_INCOMPLETE"]);

        var noTenant = await pdp.DecideAsync(new AccessRequest(
            Subject(isAuthenticated: true, tenant: null, groupStatus: GroupResolutionStatus.Degraded),
            inactive,
            AccessOperation.Insert,
            AccessTransport.Rest));
        noTenant.IsAllowed.ShouldBeFalse();
        noTenant.Reasons.Select(r => r.Code).ShouldBe(["TENANT_UNRESOLVED"]);

        var degraded = await pdp.DecideAsync(new AccessRequest(
            Subject(isAuthenticated: true, tenant: Tenant, groupStatus: GroupResolutionStatus.Degraded),
            inactive,
            AccessOperation.Insert,
            AccessTransport.Rest));
        degraded.IsAllowed.ShouldBeFalse();
        degraded.Reasons.Select(r => r.Code).ShouldBe(["GROUPS_DEGRADED"]);

        var tableInactive = await pdp.DecideAsync(new AccessRequest(
            Subject(isAuthenticated: true, tenant: Tenant, groupStatus: GroupResolutionStatus.Complete),
            inactive,
            AccessOperation.Insert,
            AccessTransport.Rest));
        tableInactive.IsAllowed.ShouldBeFalse();
        tableInactive.Reasons.Count.ShouldBe(1);
        tableInactive.Reasons[0].Step.ShouldBe(DecisionStep.Precondition);
        tableInactive.Reasons[0].Code.ShouldBe("TABLE_INACTIVE");
        tableInactive.Reasons[0].SourceId.ShouldBeNull();

        var writeUnwired = await pdp.DecideAsync(new AccessRequest(
            Subject(isAuthenticated: true, tenant: Tenant, groupStatus: GroupResolutionStatus.Complete),
            ActiveTable(isActive: true),
            AccessOperation.Insert,
            AccessTransport.Rest));
        writeUnwired.IsAllowed.ShouldBeFalse();
        writeUnwired.Reasons.Select(r => r.Code).ShouldBe(["WRITE_TRANSPORT_NOT_WIRED"]);
    }

    [Fact]
    public async Task Consent_bypass_still_denies_degraded_groups()
    {
        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions
        {
            Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = true }
        }));

        var degraded = Subject(isAuthenticated: true, tenant: Tenant, groupStatus: GroupResolutionStatus.Degraded);
        var denied = await pdp.DecideAsync(new AccessRequest(degraded, ActiveTable(), AccessOperation.Read, AccessTransport.Rest));
        denied.IsAllowed.ShouldBeFalse();
        denied.Reasons.ShouldContain(r => r.Step == DecisionStep.Precondition && r.Code == "GROUPS_DEGRADED");

        var complete = Subject(isAuthenticated: true, tenant: Tenant, groupStatus: GroupResolutionStatus.Complete);
        var allowed = await pdp.DecideAsync(new AccessRequest(complete, ActiveTable(), AccessOperation.Read, AccessTransport.Rest));
        allowed.IsAllowed.ShouldBeTrue();
    }

    [Fact]
    public async Task Self_explain_never_lists_catalog_hidden_columns()
    {
        var fullMeta = ActiveTable();
        var visibleMeta = new TableMetadata
        {
            Identifier = TableId,
            Table = fullMeta.Table,
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "email", DataType = "varchar" }
            ],
            ColumnMaskingRules = fullMeta.ColumnMaskingRules
        }.WithPii("email");

        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = fullMeta.Table.Id,
            TableIdentifier = TableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask },
                new ConsentColumnRule { ColumnName = "secret_note", AccessLevel = ColumnAccessLevel.Clear }
            ]
        };

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([consent]));

        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()), new ConsentResolutionService(), consentRepo);
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(TableId, Arg.Any<CancellationToken>()).Returns(fullMeta);
        var visibility = Substitute.For<ICatalogVisibilityService>();
        visibility.FindVisibleAsync(Arg.Any<CatalogCaller>(), TableId, Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleRow(visibleMeta, CatalogOutputShape.Full));
        var owners = Substitute.For<IDataOwnershipRepository>();
        var audit = Substitute.For<IAuditWriter>();

        var service = new AccessExplanationService(pdp, tableRepo, visibility, owners, consentRepo, new DegradedExplainSubjectResolver(), audit);
        var caller = Subject(tenant: Tenant, groupStatus: GroupResolutionStatus.Complete);

        var result = await service.ExplainAsync(caller, new ExplainRequest(new ExplainSubjectRef(UserSid.Value, null), TableId.ToString(), AccessOperation.Read));

        result.Outcome.ShouldBe(ExplainOutcome.Ok);
        result.Explanation.ShouldNotBeNull();
        result.Explanation.Columns.Select(c => c.ColumnName).ShouldBe(["id", "email"], ignoreOrder: true);
        result.Explanation.Columns.ShouldNotContain(c => string.Equals(c.ColumnName, "secret_note", StringComparison.OrdinalIgnoreCase));
        var emailCol = result.Explanation.Columns.Single(c => c.ColumnName == "email");
        emailCol.Level.ShouldBe(ColumnAccessLevel.Mask);
        emailCol.MaskType.ShouldBe("MASK_EMAIL");
    }

    [Fact]
    public async Task Self_explain_redacts_governance_object_ids()
    {
        var fullMeta = ActiveTable();
        var grantId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var denyId = Guid.NewGuid();

        var allowConsent = new Consent
        {
            Id = grantId,
            TableId = fullMeta.Table.Id,
            TableIdentifier = TableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask }
            ],
            RowFilters =
            [
                new ConsentRowFilter { Id = filterId, ColumnName = "id", Operator = "EQ", ValueJson = "\"42\"" }
            ]
        };

        var denyConsent = new Consent
        {
            Id = denyId,
            TableId = fullMeta.Table.Id,
            TableIdentifier = TableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Deny }
            ]
        };

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetActiveConsentsForSubjectsAsync(default!, default, default, default, default)
            .ReturnsForAnyArgs(Task.FromResult<IReadOnlyList<Consent>>([allowConsent, denyConsent]));

        var pdp = new AccessDecisionPoint(Options.Create(new GatewayOptions()), new ConsentResolutionService(), consentRepo);
        var tableRepo = Substitute.For<ITableMetadataRepository>();
        tableRepo.GetTableMetadataAsync(TableId, Arg.Any<CancellationToken>()).Returns(fullMeta);
        var visibility = Substitute.For<ICatalogVisibilityService>();
        visibility.FindVisibleAsync(Arg.Any<CatalogCaller>(), TableId, Arg.Any<CancellationToken>())
            .Returns(new CatalogVisibleRow(fullMeta, CatalogOutputShape.Full));
        var owners = Substitute.For<IDataOwnershipRepository>();
        var audit = Substitute.For<IAuditWriter>();

        var service = new AccessExplanationService(pdp, tableRepo, visibility, owners, consentRepo, new DegradedExplainSubjectResolver(), audit);
        var caller = Subject(tenant: Tenant, groupStatus: GroupResolutionStatus.Complete);

        var result = await service.ExplainAsync(caller, new ExplainRequest(new ExplainSubjectRef(UserSid.Value, null), TableId.ToString(), AccessOperation.Read));

        result.Outcome.ShouldBe(ExplainOutcome.Ok);
        result.Explanation.ShouldNotBeNull();

        var json = JsonSerializer.Serialize(result.Explanation, WebJsonOptions);
        using var doc = JsonDocument.Parse(json);
        var trace = doc.RootElement.GetProperty("trace").EnumerateArray().ToList();
        trace.ShouldNotBeEmpty();

        var redactedCodes = new HashSet<string>(["GRANT_DENY", "COLUMN_DENY", "ROW_DENY", "ROW_FILTER", "GRANT_EXPIRED"], StringComparer.Ordinal);
        foreach (var entry in trace)
        {
            var code = entry.GetProperty("code").GetString()!;
            if (redactedCodes.Contains(code))
            {
                if (entry.TryGetProperty("sourceId", out var sourceId))
                {
                    sourceId.ValueKind.ShouldBe(JsonValueKind.Null);
                }
            }
        }

        trace.ShouldContain(e => e.GetProperty("code").GetString() == "COLUMN_DENY");
        trace.ShouldContain(e => e.GetProperty("code").GetString() == "ROW_FILTER");
    }

    [Fact]
    public void ConsentResolutionService_populates_deterministic_grants_and_reasons_without_predicate_values()
    {
        var sut = new ConsentResolutionService();
        const string SecretLiteral = "secret-filter-value-4711";

        var allow1 = new Consent
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            TableIdentifier = TableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask },
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }
            ],
            RowFilters =
            [
                new ConsentRowFilter { Id = Guid.NewGuid(), ColumnName = "region", Operator = "EQ", ValueJson = $"\"{SecretLiteral}\"" }
            ]
        };

        var allow2 = new Consent
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            ConsentRequestId = Guid.NewGuid(),
            TableIdentifier = TableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Group,
            GranteeSid = GroupSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(5),
            ColumnRules =
            [
                new ConsentColumnRule { ColumnName = "email", AccessLevel = ColumnAccessLevel.Mask },
                new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear }
            ],
            RowFilters =
            [
                new ConsentRowFilter { Id = Guid.NewGuid(), ColumnName = "region", Operator = "EQ", ValueJson = $"\"{SecretLiteral}\"" }
            ]
        };

        var decisionA = sut.ResolveAccess(UserSid, new HashSet<Sid>([GroupSid]), new HashSet<string>(), TableId, [allow1, allow2]);
        var decisionB = sut.ResolveAccess(UserSid, new HashSet<Sid>([GroupSid]), new HashSet<string>(), TableId, [allow2, allow1]);

        decisionA.IsAllowed.ShouldBeTrue();
        decisionA.Grants.Count.ShouldBe(2);
        decisionA.Grants.Select(g => g.GrantId).ShouldBe(decisionB.Grants.Select(g => g.GrantId));
        decisionA.Reasons.ShouldBe(decisionB.Reasons);
        decisionA.Reasons.ShouldContain(r => r.Code == "GRANT_ALLOW");
        decisionA.Reasons.ShouldContain(r => r.Code == "ROW_FILTER");
        decisionA.Reasons.ShouldContain(r => r.Code == "COLUMN_MASK" && r.Column == "email");

        var serialized = JsonSerializer.Serialize(new { decisionA.Reasons, decisionA.Grants });
        serialized.ShouldNotContain(SecretLiteral);

        var noGrant = sut.ResolveAccess(UserSid, new HashSet<Sid>(), new HashSet<string>(), TableId, []);
        noGrant.IsAllowed.ShouldBeFalse();
        noGrant.Reasons.ShouldContain(r => r.Step == DecisionStep.DefaultDeny && r.Code == "NO_GRANT");

        var hardDeny = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = TableId,
            TenantId = Tenant,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        var denied = sut.ResolveAccess(UserSid, new HashSet<Sid>(), new HashSet<string>(), TableId, [allow1, hardDeny]);
        denied.IsAllowed.ShouldBeFalse();
        denied.Reasons.ShouldContain(r => r.Step == DecisionStep.ExplicitDeny && r.Code == "GRANT_DENY");
    }
}
