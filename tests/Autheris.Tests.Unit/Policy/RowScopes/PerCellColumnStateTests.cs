using System.Collections.Immutable;
using System.Globalization;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Streaming.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Ast.Security;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Parsing;
using Xunit;

namespace Autheris.Tests.Unit.Policy.RowScopes;

[Trait("Category", "Security")]
public sealed class PerCellColumnStateTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-100-200");
    private static readonly TableIdentifier OrdersId = new("prod", "dbo", "orders");
    private static readonly TableIdentity OrdersIdentity = new("dbo", "orders");
    private static readonly TableIdentifier AclId = new("prod", "conf", "acl");
    private static readonly TableIdentity AclIdentity = new("conf", "acl");

    [Fact]
    public async Task Single_signature_compiles_without_case()
    {
        var g1 = MakeConsent(("region", "EU"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Deny));
        var g2 = MakeConsent(("tier", "VIP"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Deny));

        var decision = await EvaluateDecisionAsync([g1, g2]);
        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Clear);
        decision.PerCellColumns.ShouldBeNull();

        var sql = (await CompileAsync(decision, TargetSqlDialect.PostgreSql, "SELECT id, email FROM dbo.orders")).Sql;
        sql.ShouldNotContain("CASE WHEN", Case.Insensitive);
        sql.ShouldNotContain("__f_0", Case.Sensitive);

        // Single grant-level RowScope also collapses without CASE WHEN or RowDependent.
        var filter = LiveFilter("region_acl");
        var scopeGrant = MakeScopeConsent("region_acl", "EU", ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Mask), ("ssn", ColumnAccessLevel.Deny));
        var sqlServerMeta = OrdersMetadata(TargetSqlDialect.SqlServer);
        var scopeDecision = await EvaluateDecisionAsync([scopeGrant], new VirtualFilterSnapshot(1, [filter], []), metadata: sqlServerMeta);
        scopeDecision.IsAllowed.ShouldBeTrue();
        scopeDecision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Mask);
        scopeDecision.PerCellColumns.ShouldBeNull();

        var scopeSql = (await CompileAsync(scopeDecision, TargetSqlDialect.SqlServer, "SELECT id, email FROM dbo.orders")).Sql;
        scopeSql.ShouldNotContain("CASE WHEN", Case.Insensitive);
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Oracle)]
    [InlineData(TargetSqlDialect.Databricks)]
    [InlineData(TargetSqlDialect.DuckDb)]
    public async Task Clear_only_in_rows_of_clear_grant(TargetSqlDialect dialect)
    {
        var euClear = MakeConsent(("region", "EU"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Mask));
        var vipMask = MakeConsent(("tier", "VIP"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Mask), ("ssn", ColumnAccessLevel.Deny));

        var evalDialect = dialect is TargetSqlDialect.DuckDb or TargetSqlDialect.Ansi ? TargetSqlDialect.PostgreSql : dialect;
        var metadata = OrdersMetadata(evalDialect);
        var decision = await EvaluateDecisionAsync([euClear, vipMask], metadata: metadata);
        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.RowDependent);
        decision.GetEffectiveColumnAccess("email", metadata).ShouldBe(ColumnAccessLevel.Mask);
        decision.GetColumnAccess("ssn").ShouldBe(ColumnAccessLevel.RowDependent);
        decision.PerCellColumns.ShouldNotBeNull();

        var compiled = await CompileAsync(decision, dialect, "SELECT id, email, ssn FROM dbo.orders");
        compiled.Sql.ShouldContain("CASE WHEN", Case.Insensitive);
        compiled.Sql.ShouldContain("ELSE NULL END", Case.Insensitive);

        var explanation = await ExplainDecisionAsync([euClear, vipMask], metadata);
        explanation.Allowed.ShouldBeTrue();
        var emailState = explanation.Columns.Single(c => c.ColumnName == "email");
        emailState.Level.ShouldBe(ColumnAccessLevel.RowDependent);
        emailState.MaskType.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Clear_only_in_rows_of_clear_grant_streaming_enforcer()
    {
        var euClear = MakeConsent(("region", "EU"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Mask));
        var vipMask = MakeConsent(("tier", "VIP"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Mask), ("ssn", ColumnAccessLevel.Deny));

        var enforcer = CreateStreamEnforcer([euClear, vipMask]);

        // Row matching EU clear grant -> email Clear, ssn Mask.
        var euRow = await enforcer.EvaluateAndMaskAsync(MakeEvent("e1", 1, "EU", "BASIC", "alice@x.test", "111-22"), Subscriber());
        euRow.IsAllowed.ShouldBeTrue();
        euRow.MaskedPayload!["email"].ShouldBe("alice@x.test");
        euRow.MaskedPayload["ssn"].ShouldNotBe("111-22");

        // Row matching only VIP mask grant -> email Mask, ssn Deny (stripped).
        var vipRow = await enforcer.EvaluateAndMaskAsync(MakeEvent("e2", 2, "US", "VIP", "bob@x.test", "333-44"), Subscriber());
        vipRow.IsAllowed.ShouldBeTrue();
        vipRow.MaskedPayload!["email"].ShouldNotBe("bob@x.test");
        vipRow.MaskedPayload.ContainsKey("ssn").ShouldBeFalse();

        // Row matching BOTH grants -> strongest per-cell privilege in matching signatures (Clear beats Mask).
        var bothRow = await enforcer.EvaluateAndMaskAsync(MakeEvent("e3", 3, "EU", "VIP", "carol@x.test", "555-66"), Subscriber());
        bothRow.IsAllowed.ShouldBeTrue();
        bothRow.MaskedPayload!["email"].ShouldBe("carol@x.test");
        bothRow.MaskedPayload["ssn"].ShouldNotBe("555-66");

        // Row matching NEITHER grant -> dropped completely.
        var outRow = await enforcer.EvaluateAndMaskAsync(MakeEvent("e4", 4, "US", "BASIC", "dave@x.test", "777-88"), Subscriber());
        outRow.IsAllowed.ShouldBeFalse();
        outRow.MaskedPayload.ShouldBeNull();
    }

    [Fact]
    public async Task Column_outside_covering_grants_is_null()
    {
        var euWithEmail = MakeConsent(("region", "EU"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Deny));
        var usNoEmail = MakeConsent(("region", "US"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Deny), ("ssn", ColumnAccessLevel.Deny));

        var decision = await EvaluateDecisionAsync([euWithEmail, usNoEmail]);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.RowDependent);

        // Only Clear + ELSE NULL branch should appear for email (never a Mask branch when no signature masks email).
        var compiled = await CompileAsync(decision, TargetSqlDialect.PostgreSql, "SELECT id, email FROM dbo.orders");
        var upper = compiled.Sql.ToUpperInvariant();
        upper.ShouldContain("CASE WHEN");
        upper.ShouldContain("ELSE NULL END");
        upper.ShouldNotContain("'***'");

        var enforcer = CreateStreamEnforcer([euWithEmail, usNoEmail]);
        var eu = await enforcer.EvaluateAndMaskAsync(MakeEvent("e1", 1, "EU", "BASIC", "alice@x.test", "111"), Subscriber());
        eu.IsAllowed.ShouldBeTrue();
        eu.MaskedPayload!["email"].ShouldBe("alice@x.test");

        var us = await enforcer.EvaluateAndMaskAsync(MakeEvent("e2", 2, "US", "BASIC", "bob@x.test", "222"), Subscriber());
        us.IsAllowed.ShouldBeTrue();
        us.MaskedPayload!.ContainsKey("email").ShouldBeFalse();
    }

    [Fact]
    public async Task Flag_join_never_duplicates_rows()
    {
        var aclA = LiveFilter("acl_clear");
        var aclB = LiveFilter("acl_mask");
        var snapshot = new VirtualFilterSnapshot(1, [aclA, aclB], []);

        var grantA = MakeScopeConsent("acl_clear", "EU", ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Deny));
        var grantB = MakeScopeConsent("acl_mask", "US", ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Mask), ("ssn", ColumnAccessLevel.Deny));

        var decision = await EvaluateDecisionAsync([grantA, grantB], snapshot);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.RowDependent);

        var compiled = await CompileAsync(decision, TargetSqlDialect.PostgreSql, "SELECT id, email FROM dbo.orders", dialectSnapshot: snapshot);
        compiled.Sql.ShouldContain("LEFT OUTER JOIN", Case.Insensitive);
        compiled.Sql.ShouldContain("SELECT DISTINCT", Case.Insensitive);
        compiled.Sql.ShouldContain("\"__f_0\"", Case.Sensitive);
        compiled.Sql.ShouldContain("\"__f_1\"", Case.Sensitive);

        var policy = await CreatePolicyAsync(decision, TargetSqlDialect.PostgreSql);
        var perCell = policy.PerCellColumns!.GetPolicy(OrdersIdentity);
        perCell.ShouldNotBeNull();
        perCell.Signatures.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData("SELECT id FROM dbo.orders WHERE email = 'alice@x.test'")]
    [InlineData("SELECT email, COUNT(*) FROM dbo.orders GROUP BY email")]
    [InlineData("SELECT id, email FROM dbo.orders ORDER BY email")]
    [InlineData("SELECT COUNT(email) FROM dbo.orders")]
    [InlineData("SELECT region, COUNT(*) FROM dbo.orders GROUP BY region HAVING MAX(email) = 'x'")]
    [InlineData("SELECT a.id FROM dbo.orders a JOIN dbo.orders b ON a.email = b.email")]
    public async Task Masked_in_any_signature_counts_as_masked_for_predicates_and_aggregates(string unsafeSql)
    {
        var euClear = MakeConsent(("region", "EU"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Deny));
        var vipMask = MakeConsent(("tier", "VIP"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Mask), ("ssn", ColumnAccessLevel.Deny));

        var decision = await EvaluateDecisionAsync([euClear, vipMask]);
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.RowDependent);

        await Should.ThrowAsync<SecurityException>(() => CompileAsync(decision, TargetSqlDialect.PostgreSql, unsafeSql));
    }

    [Fact]
    public async Task Access_profile_narrowing_demotes_clear_signature_and_separates_template_cache()
    {
        var euClear = MakeConsent(("region", "EU"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Deny));
        var vipMask = MakeConsent(("tier", "VIP"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Mask), ("ssn", ColumnAccessLevel.Deny));

        var unmaskedProfile = ProfileWithMode(MaskingPolicyMode.Unmasked);
        var unmaskedDecision = await EvaluateDecisionAsync([euClear, vipMask], profiles: [unmaskedProfile]);
        unmaskedDecision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.RowDependent);

        // Strict AccessProfile denies every sensitive column across ALL signatures, collapsing RowDependent -> Deny!
        var strictProfile = ProfileWithMode(MaskingPolicyMode.Strict);
        var strictDecision = await EvaluateDecisionAsync([euClear, vipMask], profiles: [strictProfile]);
        strictDecision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.Deny);
        strictDecision.PerCellColumns.ShouldBeNull();

        // Different PerCellTablePolicy column rules produce different fingerprints (no template cache poisoning).
        var euClearAlt = MakeConsent(("region", "EU"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Mask), ("ssn", ColumnAccessLevel.Deny));
        var vipClearAlt = MakeConsent(("tier", "VIP"), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", ColumnAccessLevel.Clear), ("ssn", ColumnAccessLevel.Deny));
        var altDecision = await EvaluateDecisionAsync([euClearAlt, vipClearAlt]);

        var engine = new FastSqlEngine();
        var compiledA = await CompileWithEngineAsync(engine, unmaskedDecision, TargetSqlDialect.PostgreSql, "SELECT id, email FROM dbo.orders");
        var compiledB = await CompileWithEngineAsync(engine, altDecision, TargetSqlDialect.PostgreSql, "SELECT id, email FROM dbo.orders");
        compiledA.Sql.ShouldNotBe(compiledB.Sql);
    }

    [Fact]
    public async Task Randomized_no_clear_value_outside_clear_scope()
    {
        var rng = new Random(480048);
        var regions = new[] { "EU", "US", "APAC" };
        var tiers = new[] { "VIP", "STD" };
        var levels = new[] { ColumnAccessLevel.Clear, ColumnAccessLevel.Mask, ColumnAccessLevel.Deny };
        var engine = new FastSqlEngine();

        for (var i = 0; i < 1000; i++)
        {
            var grantCount = rng.Next(1, 5);
            var consents = new List<Consent>(grantCount);
            var sigSpecs = new List<(string? Col, string? Val, ColumnAccessLevel Email, ColumnAccessLevel Ssn)>(grantCount);

            for (var g = 0; g < grantCount; g++)
            {
                var unfiltered = rng.Next(5) == 0;
                var emailLvl = levels[rng.Next(levels.Length)];
                var ssnLvl = levels[rng.Next(levels.Length)];
                if (unfiltered)
                {
                    consents.Add(MakeUnfilteredConsent(("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", emailLvl), ("ssn", ssnLvl)));
                    sigSpecs.Add((null, null, emailLvl, ssnLvl));
                }
                else
                {
                    var useRegion = rng.Next(2) == 0;
                    var col = useRegion ? "region" : "tier";
                    var val = useRegion ? regions[rng.Next(regions.Length)] : tiers[rng.Next(tiers.Length)];
                    consents.Add(MakeConsent((col, val), ("id", ColumnAccessLevel.Clear), ("region", ColumnAccessLevel.Clear), ("tier", ColumnAccessLevel.Clear), ("email", emailLvl), ("ssn", ssnLvl)));
                    sigSpecs.Add((col, val, emailLvl, ssnLvl));
                }
            }

            var enforcer = CreateStreamEnforcer(consents);
            var testRegion = regions[rng.Next(regions.Length)];
            var testTier = tiers[rng.Next(tiers.Length)];
            var clearEmail = $"user{i}@example.test";
            var clearSsn = $"999-{i.ToString(CultureInfo.InvariantCulture)}";

            var result = await enforcer.EvaluateAndMaskAsync(
                MakeEvent($"evt-{i}", i, testRegion, testTier, clearEmail, clearSsn),
                Subscriber());

            var matchingSigs = sigSpecs.Where(s =>
                s.Col is null ||
                (s.Col == "region" && s.Val == testRegion) ||
                (s.Col == "tier" && s.Val == testTier)).ToList();

            if (matchingSigs.Count == 0)
            {
                result.IsAllowed.ShouldBeFalse();
                result.MaskedPayload.ShouldBeNull();
            }
            else
            {
                result.IsAllowed.ShouldBeTrue($"iter={i} reason={result.FilterReason} grants={grantCount} sigs={string.Join('|', sigSpecs.Select(s => $"{s.Col}:{s.Val}:{s.Email}:{s.Ssn}"))} row=({testRegion},{testTier})");
                result.MaskedPayload.ShouldNotBeNull();

                var expectedEmail = matchingSigs.Max(s => s.Email);
                var expectedSsn = matchingSigs.Max(s => s.Ssn);

                if (expectedEmail == ColumnAccessLevel.Clear)
                    result.MaskedPayload["email"].ShouldBe(clearEmail);
                else if (expectedEmail == ColumnAccessLevel.Mask)
                    result.MaskedPayload["email"].ShouldNotBe(clearEmail);
                else
                    result.MaskedPayload.ContainsKey("email").ShouldBeFalse();

                if (expectedSsn == ColumnAccessLevel.Clear)
                    result.MaskedPayload["ssn"].ShouldBe(clearSsn);
                else if (expectedSsn == ColumnAccessLevel.Mask)
                    result.MaskedPayload["ssn"].ShouldNotBe(clearSsn);
                else
                    result.MaskedPayload.ContainsKey("ssn").ShouldBeFalse();
            }

            // Sample SQL compilation every 10th randomized configuration to also validate SQL CASE emission end-to-end.
            if (i % 10 == 0)
            {
                var decision = await EvaluateDecisionAsync(consents);
                if (decision.IsAllowed)
                {
                    var compiled = await CompileWithEngineAsync(engine, decision, TargetSqlDialect.PostgreSql, "SELECT id, email, ssn FROM dbo.orders");
                    if (decision.GetColumnAccess("email") == ColumnAccessLevel.RowDependent || decision.GetColumnAccess("ssn") == ColumnAccessLevel.RowDependent)
                    {
                        compiled.Sql.ShouldContain("CASE WHEN", Case.Insensitive);
                        await Should.ThrowAsync<SecurityException>(() => CompileWithEngineAsync(engine, decision, TargetSqlDialect.PostgreSql, "SELECT COUNT(email) FROM dbo.orders WHERE email = 'x'"));
                    }
                }
            }
        }
    }

    private static ClaimsPrincipal Subscriber() => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", Tenant.Value),
        new Claim(ClaimTypes.PrimarySid, UserSid.Value)
    ]));

    private static CdcEvent MakeEvent(string id, int rowId, string region, string tier, string email, string ssn) =>
        new(
            EventId: id,
            Table: OrdersId,
            Operation: CdcOperation.Insert,
            TenantId: Tenant.Value,
            Before: null,
            After: new Dictionary<string, object?>
            {
                ["id"] = rowId,
                ["tenant_id"] = Tenant.Value,
                ["region"] = region,
                ["tier"] = tier,
                ["email"] = email,
                ["ssn"] = ssn
            },
            Timestamp: DateTimeOffset.UtcNow);

    private static TableMetadata OrdersMetadata(TargetSqlDialect dialect = TargetSqlDialect.PostgreSql) => new()
    { ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email", "ssn"),
        Identifier = OrdersId,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "prod", SchemaName = "dbo", TableName = "orders", SourceType = SourceTypeFor(dialect), IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "INTEGER" },
            new TableColumn { ColumnName = "tenant_id", DataType = "VARCHAR(64)" },
            new TableColumn { ColumnName = "region", DataType = "VARCHAR(32)" },
            new TableColumn { ColumnName = "tier", DataType = "VARCHAR(32)" },
            new TableColumn { ColumnName = "email", DataType = "VARCHAR(128)" },
            new TableColumn { ColumnName = "ssn", DataType = "VARCHAR(64)" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new() { RuleType = "MASK_EMAIL" },
            ["ssn"] = new() { RuleType = "REDACT", Replacement = "[REDACTED]" }
        }
    };

    private static TableMetadata AclMetadata(TargetSqlDialect dialect = TargetSqlDialect.PostgreSql) => new()
    {
        Identifier = AclId,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "prod", SchemaName = "conf", TableName = "acl", SourceType = SourceTypeFor(dialect), IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "INTEGER" },
            new TableColumn { ColumnName = "tenant_id", DataType = "VARCHAR(64)" },
            new TableColumn { ColumnName = "tag", DataType = "VARCHAR(32)" }
        ]
    };

    private static string SourceTypeFor(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => "SqlServer",
        TargetSqlDialect.Oracle => "Oracle",
        TargetSqlDialect.Databricks => "Databricks",
        _ => "PostgreSQL"
    };

    private static Consent MakeConsent(string region, params (string Col, ColumnAccessLevel Lvl)[] cols) =>
        MakeConsent(("region", region), cols);

    private static Consent MakeConsent((string Col, string Val) filter, params (string Col, ColumnAccessLevel Lvl)[] cols) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = [new ConsentRowFilter { ColumnName = filter.Col, Operator = "EQ", ValueType = "string", ValueJson = JsonSerializer.Serialize(filter.Val) }],
        ColumnRules = cols.Select(c => new ConsentColumnRule { ColumnName = c.Col, AccessLevel = c.Lvl }).ToList()
    };

    private static Consent MakeUnfilteredConsent(params (string Col, ColumnAccessLevel Lvl)[] cols) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        ColumnRules = cols.Select(c => new ConsentColumnRule { ColumnName = c.Col, AccessLevel = c.Lvl }).ToList()
    };

    private static Consent MakeScopeConsent(string policyName, string tagVal, params (string Col, ColumnAccessLevel Lvl)[] cols) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowScope = [new GrantRowScopeEntry(policyName, new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { ["tag"] = JsonSerializer.SerializeToElement(tagVal) })],
        ColumnRules = cols.Select(c => new ConsentColumnRule { ColumnName = c.Col, AccessLevel = c.Lvl }).ToList()
    };

    private static VirtualFilter LiveFilter(string name) => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = "prod",
        EvaluationMode = RowPolicyEvaluationMode.Live,
        KeyColumns = ["acl.id"],
        Parameters = [new RowPolicyParameter("tag", RowPolicyParameterType.String)],
        Structured = new StructuredFilterDefinition
        {
            From = AclId,
            FromAlias = "acl",
            Where = [new FilterCondition("acl.tag", FilterConditionOperator.Eq, ":tag")]
        }
    };

    private static AccessProfile ProfileWithMode(MaskingPolicyMode mode) => new()
    {
        ProfileId = $"prof-{mode}",
        Name = mode.ToString(),
        TenantId = Tenant,
        MaskingMode = mode,
        TargetTables = ["*.*"]
    };

    private static GatewayOptions DefaultOptions() => new()
    {
        DataMasking = new DataMaskingOptions()
    };

    private static TableAccessPolicy CreateTableAccessPolicy(
        IReadOnlyList<Consent> consents,
        VirtualFilterSnapshot? snapshot = null,
        IReadOnlyList<AccessProfile>? profiles = null)
    {
        var consentRepo = CreateConsentRepo(consents);
        var opts = DefaultOptions();
        var compiler = CreateCompiler(snapshot);

        return new TableAccessPolicy(
            consentRepo,
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            opts,
            NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()),
            maskPolicyFactory: new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(Options.Create(opts))),
            accessProfileRepository: profiles is null ? null : new FakeAccessProfileRepository(profiles),
            rowScopeCompiler: compiler);
    }

    private static RowScopeCompiler CreateCompiler(VirtualFilterSnapshot? snapshot = null) =>
        new(
            new FixedSnapshotProvider(snapshot ?? VirtualFilterSnapshot.Empty),
            new StructuredFilterSqlBuilder(),
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer(), new PolicyExpressionParser(FastSqlEngine.Default)));

    private static SecurityPrincipalContext TestSubject() => new()
    {
        IsAuthenticated = true,
        AuthenticationScheme = "Test",
        UserSid = UserSid,
        ResolvedTenant = Tenant,
        GroupResolutionStatus = Autheris.Domain.Identity.GroupResolutionStatus.Complete,
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        Claims = []
    };

    private static async Task<TableAccessDecision> EvaluateDecisionAsync(
        IReadOnlyList<Consent> consents,
        VirtualFilterSnapshot? snapshot = null,
        IReadOnlyList<AccessProfile>? profiles = null,
        TableMetadata? metadata = null)
    {
        var policy = CreateTableAccessPolicy(consents, snapshot, profiles);
        var query = new TableAccessQuery(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(), metadata ?? OrdersMetadata(), Claims: []);
        return await policy.DecideAsync(query, CancellationToken.None);
    }

    private static async Task<AccessExplanation> ExplainDecisionAsync(IReadOnlyList<Consent> consents, TableMetadata? metadata = null)
    {
        var opts = Options.Create(DefaultOptions());
        var typedPolicies = new TypedPolicySources(
            new RowPolicyFactory(new PolicyPredicateRenderer()),
            new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(opts)));
        var pdp = new AccessDecisionPoint(
            opts,
            resolutionService: new ConsentResolutionService(),
            consentRepository: CreateConsentRepo(consents),
            cacheService: NullConsentCacheService.Instance,
            mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
            typedPolicies: typedPolicies,
            rowScopeCompiler: CreateCompiler());
        return await pdp.ExplainAsync(new AccessRequest(TestSubject(), metadata ?? OrdersMetadata(), AccessOperation.Read, AccessTransport.WebSql));
    }

    private static FakeConsentRepository CreateConsentRepo(IReadOnlyList<Consent> consents)
    {
        var repo = new FakeConsentRepository();
        foreach (var c in consents) repo.CreateConsentAsync(c).GetAwaiter().GetResult();
        return repo;
    }

    private static StreamRlsPolicyEnforcer CreateStreamEnforcer(IReadOnlyList<Consent> consents)
    {
        var policy = CreateTableAccessPolicy(consents);
        var opts = Options.Create(DefaultOptions());
        return new StreamRlsPolicyEnforcer(
            new PolicyBackedPdp(policy),
            new FakeTableMetadataRepository([OrdersMetadata(), AclMetadata()]),
            new ColumnMaskingProvider(opts),
            new AlwaysValidEpochService(),
            NullLogger<StreamRlsPolicyEnforcer>.Instance,
            CreateConsentRepo(consents),
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            options: opts);
    }

    private static async Task<GovernancePolicy> CreatePolicyAsync(TableAccessDecision decision, TargetSqlDialect dialect)
    {
        var ordersMeta = OrdersMetadata(dialect);
        var aclMeta = AclMetadata(dialect);
        var catalog = new InMemoryTableCatalog(
            [
                ToCatalogEntry(ordersMeta, OrdersIdentity),
                ToCatalogEntry(aclMeta, AclIdentity)
            ],
            "dbo");
        var metaRepo = new FakeTableMetadataRepository([ordersMeta, aclMeta]);
        var factory = new GovernancePolicyFactory(new FixedCatalogProvider(catalog), metaRepo, Options.Create(DefaultOptions()));
        var policy = await factory.CreateAsync(new GovernedPolicyInput(
            "prod",
            Tenant,
            new Dictionary<TableIdentity, TableAccessDecision> { [OrdersIdentity] = decision },
            new HashSet<TableIdentity>(),
            GovernedSubqueryStrategy.Correlated)
        {
            Metadata = new Dictionary<TableIdentity, TableMetadata>
            {
                [OrdersIdentity] = ordersMeta,
                [AclIdentity] = aclMeta
            }
        }, CancellationToken.None);

        return dialect is TargetSqlDialect.DuckDb or TargetSqlDialect.Ansi
            ? policy with { RowFilters = new DialectPassThroughRowFilterProvider(policy.RowFilters) }
            : policy;
    }

    private static Task<CompiledSql> CompileAsync(TableAccessDecision decision, TargetSqlDialect dialect, string sql, VirtualFilterSnapshot? dialectSnapshot = null)
        => CompileWithEngineAsync(new FastSqlEngine(), decision, dialect, sql, dialectSnapshot);

    private static async Task<CompiledSql> CompileWithEngineAsync(FastSqlEngine engine, TableAccessDecision decision, TargetSqlDialect dialect, string sql, VirtualFilterSnapshot? _ = null)
    {
        var policy = await CreatePolicyAsync(decision, dialect);
        var request = CompileRequestFactory.Create(policy, dialect, new WebSqlOptions(), isDml: false, enforcedMaxRows: 100, allowedFunctions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return engine.Compile(sql.AsMemory(), request, CancellationToken.None);
    }

    private static TableCatalogEntry ToCatalogEntry(TableMetadata meta, TableIdentity id) => new(
        id,
        meta.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(),
        meta.TenantColumnName,
        1L);

    private sealed class FixedSnapshotProvider(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class FixedCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct) =>
            ValueTask.FromResult(catalog);
    }

    private sealed class PolicyBackedPdp(TableAccessPolicy policy) : IAccessDecisionPoint
    {
        public async ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default)
        {
            var query = new TableAccessQuery(
                request.Subject.UserSid,
                request.Subject.TenantId,
                request.Subject.GroupSids,
                request.Subject.TenantRoles,
                request.Object,
                Claims: request.Subject.Claims ?? [],
                RequestedColumns: request.Columns,
                SupplementalConsents: request.SupplementalConsents);
            return await policy.DecideAsync(query, ct).ConfigureAwait(false);
        }

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class DialectPassThroughRowFilterProvider(IPolicyPredicateProvider inner) : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => inner.ShouldApplyPolicy(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => inner.GetPredicate(table);
    }

    private sealed class FakeAccessProfileRepository(IReadOnlyList<AccessProfile> profiles) : IAccessProfileRepository
    {
        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectAsync(TenantId tenantId, string subject, CancellationToken ct = default) => Task.FromResult(profiles);
        public Task<IReadOnlyList<AccessProfile>> GetAllProfilesAsync(TenantId tenantId, CancellationToken ct = default) => Task.FromResult(profiles);
        public Task<AccessProfile?> GetProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) => Task.FromResult(profiles.FirstOrDefault(p => p.ProfileId == profileId));
        public Task UpsertProfileAsync(AccessProfile profile, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> DeleteProfileAsync(TenantId tenantId, string profileId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<AccessProfile>> GetProfilesForSubjectsAsync(TenantId tenantId, IReadOnlyCollection<string> users, IReadOnlyCollection<string> groups, IReadOnlyCollection<string> roles, CancellationToken ct = default) => Task.FromResult(profiles);
        public Task<AccessProfileAssignment> AssignProfileAsync(TenantId tenantId, string profileId, AccessProfileAssignment assignment, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccessProfileAssignment> ApproveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, string approvedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccessProfileAssignment> RenewAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, DateTimeOffset newExpiresAt, string approvedBy, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> RemoveAssignmentAsync(TenantId tenantId, string profileId, AccessProfileSubjectType subjectType, string subject, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<RevokedRoleConsent>> GetRevokedRoleConsentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RevokedRoleConsent>>([]);
        public Task<IReadOnlyList<DeactivatedProfileAssignment>> GetDeactivatedAssignmentsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DeactivatedProfileAssignment>>([]);
    }

    private sealed class FakeTableMetadataRepository(IReadOnlyList<TableMetadata> tables) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult(tables.FirstOrDefault(t => t.Identifier.Equals(table)));
        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => Task.FromResult(tables);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => Task.FromResult(metadata);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null, CancellationToken ct = default) => Task.FromResult<TableKeyDefinitionChange?>(null);
        public Task FlushPendingVersionChangesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class AlwaysValidEpochService : IEpochValidationService
    {
        public Task<bool> IsEpochValidAsync(TableIdentifier table, long expectedEpoch, CancellationToken ct = default) => Task.FromResult(true);
        public Task InvalidateEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.CompletedTask;
        public Task<long> GetCurrentEpochAsync(TableIdentifier table, CancellationToken ct = default) => Task.FromResult(1L);
        public Task<IReadOnlyDictionary<TableIdentifier, long>> GetCurrentEpochsAsync(IEnumerable<TableIdentifier> tables, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<TableIdentifier, long>>(tables.Distinct().ToDictionary(t => t, _ => 1L));
    }
}
