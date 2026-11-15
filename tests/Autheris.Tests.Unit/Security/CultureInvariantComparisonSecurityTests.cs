namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using Autheris.Api.Configuration.Validation;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Security;
using Autheris.Domain.Security;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CultureInvariantComparisonSecurityTests
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");

    private static T RunWithCulture<T>(CultureInfo culture, Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = culture;
            return action();
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    private static ClaimsPrincipal CreatePrincipalWithRoles(params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "user-1") };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    [Fact]
    public void Role_check_is_not_affected_by_turkish_i()
    {
        var evaluator = new GatewayRoleEvaluator();
        var identityAdmin = CreatePrincipalWithRoles("identity_admin");
        var turkishUpperRole = CreatePrincipalWithRoles("BİLLİNGADMİN");
        var dotlessRole = CreatePrincipalWithRoles("bıllingadmin");
        var asciiBilling = CreatePrincipalWithRoles("BillingAdmin");

        var (trExact, invExact) = (
            RunWithCulture(TurkishCulture, () => evaluator.HasExactRole(asciiBilling, "BILLINGADMIN")),
            RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasExactRole(asciiBilling, "BILLINGADMIN")));
        trExact.ShouldBeTrue();
        invExact.ShouldBe(trExact);

        var (trTurkishI, invTurkishI) = (
            RunWithCulture(TurkishCulture, () => evaluator.HasExactRole(turkishUpperRole, "billingadmin")),
            RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasExactRole(turkishUpperRole, "billingadmin")));
        trTurkishI.ShouldBeFalse();
        invTurkishI.ShouldBe(trTurkishI);

        var (trDotlessI, invDotlessI) = (
            RunWithCulture(TurkishCulture, () => evaluator.HasExactRole(dotlessRole, "BILLINGADMIN")),
            RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasExactRole(dotlessRole, "BILLINGADMIN")));
        trDotlessI.ShouldBeFalse();
        invDotlessI.ShouldBe(trDotlessI);

        var (trCustomLower, invCustomLower) = (
            RunWithCulture(TurkishCulture, () => evaluator.HasRole(identityAdmin, "IDENTITY_ADMIN")),
            RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasRole(identityAdmin, "IDENTITY_ADMIN")));
        trCustomLower.ShouldBeTrue();
        invCustomLower.ShouldBe(trCustomLower);
    }

    [Fact]
    public void Sid_prefix_check_is_ordinal()
    {
        var candidates = new[]
        {
            "S-1-5-32-544",
            "s-1-5-32-544",
            "S-1-5-21-1000-2000-3000-500",
            "S-1-5-21-1000-2000-3000-1001",
            "S-1-5-32-\u0130",
            "\u00ADS-1-5-32-544"
        };

        foreach (var raw in candidates)
        {
            var sid = new Sid(raw);
            var (trCanonical, invCanonical) = (
                RunWithCulture(TurkishCulture, () => SidFormat.IsCanonical(raw)),
                RunWithCulture(CultureInfo.InvariantCulture, () => SidFormat.IsCanonical(raw)));
            trCanonical.ShouldBe(invCanonical);

            var (trPrivileged, invPrivileged) = (
                RunWithCulture(TurkishCulture, () => SidFormat.IsPrivileged(sid)),
                RunWithCulture(CultureInfo.InvariantCulture, () => SidFormat.IsPrivileged(sid)));
            trPrivileged.ShouldBe(invPrivileged);
        }

        SidFormat.IsCanonical("\u00ADS-1-5-32-544").ShouldBeFalse();
        SidFormat.IsPrivileged(new Sid("\u00ADS-1-5-32-544")).ShouldBeFalse();
    }

    [Fact]
    public void Header_name_match_is_culture_invariant()
    {
        var validator = new AuthenticationModeOptionsValidator(new FakeHostEnvironment());

        var asciiDuplicate = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                Mode = AuthenticationMode.Upstream,
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    UserHeader = "X-Api-Id",
                    EmailHeader = "x-api-id"
                }
            }
        };

        var turkishDotlessPair = new GatewayOptions
        {
            Authentication = new AuthenticationOptions
            {
                Mode = AuthenticationMode.Upstream,
                ForwardAuth = new ForwardAuthOptions
                {
                    Enabled = true,
                    UserHeader = "X-Api-Id",
                    EmailHeader = "x-ap\u0131-id"
                }
            }
        };

        var trDup = RunWithCulture(TurkishCulture, () => validator.Validate(null, asciiDuplicate));
        var invDup = RunWithCulture(CultureInfo.InvariantCulture, () => validator.Validate(null, asciiDuplicate));
        trDup.Failed.ShouldBeTrue();
        invDup.Failed.ShouldBe(trDup.Failed);

        var trDotless = RunWithCulture(TurkishCulture, () => validator.Validate(null, turkishDotlessPair));
        var invDotless = RunWithCulture(CultureInfo.InvariantCulture, () => validator.Validate(null, turkishDotlessPair));
        trDotless.Succeeded.ShouldBeTrue();
        invDotless.Succeeded.ShouldBe(trDotless.Succeeded);
    }

    [Fact]
    public void Sql_keyword_guard_is_culture_invariant()
    {
        var planner = new GovernedTableReadPlanner(cursorEncoder: null);
        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            PrimaryKeyColumns = ["id"],
            Table = new Table { SourceName = "sales", SchemaName = "public", TableName = "orders", IsActive = true },
            Columns =
            [
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "amount", DataType = "int" }
            ]
        };
        var decision = new TableAccessDecision(
            metadata.Identifier,
            IsAllowed: true,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            },
            CombinedRowFilterSql: null,
            DeniedReasons: []);

        foreach (var keywordVariant in new[] { "INSERT", "insert", "\u0131nsert", "\u00ADINSERT" })
        {
            var trRejected = RunWithCulture(TurkishCulture, () =>
            {
                try
                {
                    planner.PlanKeysetStream(new BulkTableStreamRequest(Select: [keywordVariant], Limit: 10), metadata, decision);
                    return false;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            });

            var invRejected = RunWithCulture(CultureInfo.InvariantCulture, () =>
            {
                try
                {
                    planner.PlanKeysetStream(new BulkTableStreamRequest(Select: [keywordVariant], Limit: 10), metadata, decision);
                    return false;
                }
                catch (ArgumentException)
                {
                    return true;
                }
            });

            trRejected.ShouldBeTrue();
            invRejected.ShouldBeTrue();
        }

        var guardrail = new SemanticPromptGuardrail();
        foreach (var prompt in new[] { "drop tables now", "\u00ADdrop tables now", "DROP TABLES" })
        {
            var trBlocked = RunWithCulture(TurkishCulture, () => !guardrail.EvaluatePrompt("sql", prompt).IsAllowed);
            var invBlocked = RunWithCulture(CultureInfo.InvariantCulture, () => !guardrail.EvaluatePrompt("sql", prompt).IsAllowed);
            trBlocked.ShouldBeTrue();
            invBlocked.ShouldBe(trBlocked);
        }

        var sqlValidator = new DefaultSqlSecurityValidator();
        RunWithCulture(TurkishCulture, () =>
        {
            sqlValidator.ValidateOrderBySql("id ASC", "orderBy");
            Should.Throw<ArgumentException>(() => sqlValidator.ValidateOrderBySql("id \u00ADASC", "orderBy"));
            return true;
        });
    }

    [Fact]
    public void Scope_and_tenant_checks_same_in_tr_TR_and_invariant()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("scp", "Sql.Write Catalog.Read"),
            new Claim(ClaimTypes.Role, "tenant-i:DataOwner")
        ], "Bearer"));

        var evaluator = new GatewayRoleEvaluator();

        foreach (var scope in new[] { "sql.write", "SQL.WRITE", "catalog.read", "catalog.r\u0131ad", "ADM\u0130N" })
        {
            var trHas = RunWithCulture(TurkishCulture, () => principal.HasScope(scope));
            var invHas = RunWithCulture(CultureInfo.InvariantCulture, () => principal.HasScope(scope));
            trHas.ShouldBe(invHas);
        }

        foreach (var tenant in new[] { "tenant-i", "TENANT-I", "TENANT-\u0130" })
        {
            var trRole = RunWithCulture(TurkishCulture, () => evaluator.HasRole(principal, "DataOwner", tenant));
            var invRole = RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasRole(principal, "DataOwner", tenant));
            trRole.ShouldBe(invRole);
        }
    }

    [Fact]
    public void Soft_hyphen_does_not_match_role()
    {
        var evaluator = new GatewayRoleEvaluator();
        var obfuscated = CreatePrincipalWithRoles("\u00ADadmin", "Security\u00ADAdmin", "Data\u200BOwner");

        var (trAdmin, invAdmin) = (
            RunWithCulture(TurkishCulture, () => evaluator.HasExactRole(obfuscated, "admin")),
            RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasExactRole(obfuscated, "admin")));
        trAdmin.ShouldBeFalse();
        invAdmin.ShouldBeFalse();

        var (trSecAdmin, invSecAdmin) = (
            RunWithCulture(TurkishCulture, () => evaluator.HasRole(obfuscated, "SecurityAdmin")),
            RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasRole(obfuscated, "SecurityAdmin")));
        trSecAdmin.ShouldBeFalse();
        invSecAdmin.ShouldBeFalse();

        var (trDataOwner, invDataOwner) = (
            RunWithCulture(TurkishCulture, () => evaluator.HasRole(obfuscated, GatewayRole.DataOwner)),
            RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasRole(obfuscated, GatewayRole.DataOwner)));
        trDataOwner.ShouldBeFalse();
        invDataOwner.ShouldBeFalse();
    }

    [Fact]
    public void Changed_sites_keep_allow_decision_case_sensitivity()
    {
        var evaluator = new GatewayRoleEvaluator();
        var mixedTenant = CreatePrincipalWithRoles("Tenant-A:SecurityAdmin");

        // Exact allow semantics preserved identically under tr-TR and InvariantCulture
        var trDeniedDifferentTenant = RunWithCulture(TurkishCulture, () => evaluator.HasRole(mixedTenant, "SecurityAdmin", "Tenant-B"));
        var invDeniedDifferentTenant = RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasRole(mixedTenant, "SecurityAdmin", "Tenant-B"));
        trDeniedDifferentTenant.ShouldBeFalse();
        invDeniedDifferentTenant.ShouldBeFalse();

        var trAllowedSameTenant = RunWithCulture(TurkishCulture, () => evaluator.HasRole(mixedTenant, "SecurityAdmin", "tenant-a"));
        var invAllowedSameTenant = RunWithCulture(CultureInfo.InvariantCulture, () => evaluator.HasRole(mixedTenant, "SecurityAdmin", "tenant-a"));
        trAllowedSameTenant.ShouldBeTrue();
        invAllowedSameTenant.ShouldBeTrue();
    }

    private sealed class FakeHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
