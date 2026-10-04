namespace Autheris.Tests.Unit.Security;

using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Governance;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Analysis;
using Xunit;

/// <summary>
/// Regression tests for the second remediation round of the repeated security review
/// (RR-L4-05, RR-L4-06, RR-L5-02, RR-L5-03, RR-L2-03, RR-L2-04, RV-04).
/// </summary>
public sealed class RepeatedReviewRound2Tests
{
    private static readonly TenantId Tenant = new("tenant-rr2");

    // ---------------------------------------------------------------- RR-L4-05

    [Fact]
    public void RR_L4_05_RlsFilterWithCommas_IsParsedAsSingleField()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.LoadPolicyFromText(Tenant, "p, S-1-5-21-U1, tenant-rr2, sales.dbo.orders, read, true, allow, region IN ('EU','US')");

        casbin.HasPolicies(Tenant).ShouldBeTrue();
    }

    [Fact]
    public void RR_L4_05_UnknownEffect_IsRejected()
    {
        using var casbin = new CasbinEnforcementService();

        // A naive Split(',') turned "region IN ('EU','US')" into the effect field; unknown effects must fail loudly.
        Should.Throw<FormatException>(() =>
            casbin.LoadPolicyFromText(Tenant, "p, S-1-5-21-U1, tenant-rr2, sales.dbo.orders, read, true, denyy"));
        casbin.HasPolicies(Tenant).ShouldBeFalse();
    }

    [Fact]
    public void RR_L4_05_UnbalancedQuotes_AreRejected_AndLastKnownGoodStays()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.LoadPolicyFromText(Tenant, "p, S-1-5-21-U1, tenant-rr2, sales.dbo.orders, read, true, deny");

        Should.Throw<FormatException>(() =>
            casbin.LoadPolicyFromText(Tenant, "p, S-1-5-21-U1, tenant-rr2, sales.dbo.orders, read, true, allow, region = 'E"));

        casbin.HasPolicies(Tenant).ShouldBeTrue();
    }

    [Fact]
    public void RR_L4_05_EmptyReload_DoesNotDropExistingPolicies()
    {
        using var casbin = new CasbinEnforcementService();
        casbin.LoadPolicyFromText(Tenant, "p, S-1-5-21-U1, tenant-rr2, sales.dbo.orders, read, true, deny");

        Should.Throw<InvalidOperationException>(() => casbin.LoadPolicyFromText(Tenant, "   \n# truncated\n"));

        casbin.HasPolicies(Tenant).ShouldBeTrue();
    }

    [Fact]
    public async Task RR_L4_05_ReloadWithMissingRegisteredFile_KeepsLastKnownGood()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"rr2_casbin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "policy.csv");
            await File.WriteAllTextAsync(file, "p, S-1-5-21-U1, tenant-rr2, sales.dbo.orders, read, true, deny");

            using var casbin = new CasbinEnforcementService();
            casbin.LoadPolicyFromFile(Tenant, file);
            File.Delete(file);

            await Should.ThrowAsync<FileNotFoundException>(() => casbin.ReloadPoliciesAsync(Tenant));
            casbin.HasPolicies(Tenant).ShouldBeTrue();
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- RR-L4-06

    private static (ConsentCacheService Cache, IEpochValidationService Epochs) CreateConsentCache()
    {
        var epochs = Substitute.For<IEpochValidationService>();
        epochs.IsEpochValidAsync(Arg.Any<TableIdentifier>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        var eventBus = Substitute.For<IEventBus>();
        var cache = new ConsentCacheService(new MemoryCache(new MemoryCacheOptions()), epochs, eventBus);
        return (cache, epochs);
    }

    [Fact]
    public async Task RR_L4_06_EpochChangedDuringEvaluation_DecisionIsNotCached()
    {
        var (cache, epochs) = CreateConsentCache();
        using var _ = cache;
        var table = new TableIdentifier("sales", "dbo", "orders");
        var sid = new Sid("S-1-5-21-U1");

        epochs.GetCurrentEpochAsync(table, Arg.Any<CancellationToken>()).Returns(1L);
        var snapshot = await cache.GetEpochSnapshotAsync(table);

        // Consent revoked while the decision was being computed -> epoch bump.
        epochs.GetCurrentEpochAsync(table, Arg.Any<CancellationToken>()).Returns(2L);
        var staleAllow = TableAccessDecision.Allowed(table, new System.Collections.Generic.Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        await cache.SetCachedDecisionAsync(Tenant, sid, table, staleAllow, TimeSpan.FromMinutes(5), "ctx", snapshot);

        (await cache.GetCachedDecisionAsync(Tenant, sid, table, "ctx")).ShouldBeNull();
    }

    [Fact]
    public async Task RR_L4_06_EpochUnchanged_DecisionIsCached()
    {
        var (cache, epochs) = CreateConsentCache();
        using var _ = cache;
        var table = new TableIdentifier("sales", "dbo", "orders");
        var sid = new Sid("S-1-5-21-U1");

        epochs.GetCurrentEpochAsync(table, Arg.Any<CancellationToken>()).Returns(7L);
        var snapshot = await cache.GetEpochSnapshotAsync(table);
        var allow = TableAccessDecision.Allowed(table, new System.Collections.Generic.Dictionary<string, ColumnAccessLevel>(), rowFilterSql: null, hasUnconstrainedColumnAllow: true);
        await cache.SetCachedDecisionAsync(Tenant, sid, table, allow, TimeSpan.FromMinutes(5), "ctx", snapshot);

        (await cache.GetCachedDecisionAsync(Tenant, sid, table, "ctx")).ShouldNotBeNull();
    }

    // ---------------------------------------------------------------- RR-L5-02

    [Fact]
    public void RR_L5_02_QuotedAndUnquotedVariants_AreDistinctTargets()
    {
        var metadata = new FastSqlEngine().Analyze("SELECT * FROM \"Orders\" a JOIN orders b ON a.id = b.id".AsMemory());

        metadata.ReferencedTables.Count.ShouldBe(2);
        metadata.ReferencedTables.Single(t => t.Alias == "a").TableNameQuoted.ShouldBeTrue();
        metadata.ReferencedTables.Single(t => t.Alias == "b").TableNameQuoted.ShouldBeFalse();
    }

    [Theory]
    [InlineData("orders", false, "orders", true)]
    [InlineData("ORDERS", false, "orders", true)]   // unquoted folds to lower case
    [InlineData("Orders", true, "orders", false)]   // quoted "Orders" is a different PG relation
    [InlineData("Orders", true, "Orders", true)]
    [InlineData("orders", false, "Orders", false)]  // unquoted orders never reaches catalogued "Orders"
    public void RR_L5_02_PostgreSqlCatalogMatch_UsesPostgresIdentifierSemantics(string tableName, bool quoted, string catalogName, bool expected)
    {
        var target = new TableAccessTarget(null, "public", tableName, null, $"public.{tableName}", SchemaQuoted: false, TableNameQuoted: quoted);

        GovernedSqlExecutionService.MatchesPostgreSqlCatalogName(target, new TableIdentifier("default", "public", catalogName))
            .ShouldBe(expected);
    }

    // ---------------------------------------------------------------- RR-L5-03

    [Fact]
    public void RR_L5_03_WhereFilterWithSubquery_IsRejectedWhenSubqueriesDisallowed()
    {
        const string oracle = "EXISTS (SELECT 1 FROM hr.salaries s WHERE s.amount > 100000)";

        Should.NotThrow(() => SqlSecurityValidator.ValidatePredicateSql(oracle, "rls"));
        Should.Throw<ArgumentException>(() => SqlSecurityValidator.ValidatePredicateSql(oracle, "where", allowSubqueries: false));
        Should.NotThrow(() => SqlSecurityValidator.ValidatePredicateSql("region = 'EU' AND amount > 10", "where", allowSubqueries: false));
    }

    // ---------------------------------------------------------------- RR-L2-03

    private static (BasicAuthenticationHandler Handler, DefaultHttpContext Context) CreateBasicHandler(GatewayOptions options, string authorization)
    {
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var monitor = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        monitor.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());
        var handler = new BasicAuthenticationHandler(monitor, NullLoggerFactory.Instance, UrlEncoder.Default, Options.Create(options), env);
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.1.2.3");
        context.Request.Headers.Authorization = authorization;
        handler.InitializeAsync(new AuthenticationScheme("Basic", "Basic", typeof(BasicAuthenticationHandler)), context).GetAwaiter().GetResult();
        return (handler, context);
    }

    private static string Basic(string user, string password) =>
        "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));

    [Fact]
    public async Task RR_L2_03_BasicAuth_LocksOutAfterRepeatedFailures()
    {
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    MaxFailedAttempts = 3,
                    FailureWindowSeconds = 300,
                    Users = [new BasicAuthUserConfig { Username = "dave", Password = "devOnlyPassword", Sid = "S-1-5-21-DAVE" }]
                }
            }
        };

        for (var i = 0; i < 3; i++)
        {
            var (bad, _) = CreateBasicHandler(options, Basic("dave", "wrong" + i));
            (await bad.AuthenticateAsync()).Succeeded.ShouldBeFalse();
        }

        // Correct password is now rejected for this (user, IP) pair until the window expires.
        var (locked, _) = CreateBasicHandler(options, Basic("dave", "devOnlyPassword"));
        (await locked.AuthenticateAsync()).Succeeded.ShouldBeFalse();
    }

    [Fact]
    public async Task RR_L2_03_BasicAuth_RejectsTrivialIterationCount()
    {
        var salt = "rr2-salt-1234567"u8.ToArray();
        var hash = System.Security.Cryptography.Rfc2898DeriveBytes.Pbkdf2("pw", salt, 1, System.Security.Cryptography.HashAlgorithmName.SHA256, 32);
        var options = new GatewayOptions
        {
            Authentication = new Autheris.Domain.Options.AuthenticationOptions
            {
                BasicAuth = new BasicAuthOptions
                {
                    Enabled = true,
                    Users = [new BasicAuthUserConfig { Username = "erin", Password = $"$pbkdf2$1${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}" }]
                }
            }
        };

        var (handler, _) = CreateBasicHandler(options, Basic("erin", "pw"));
        (await handler.AuthenticateAsync()).Succeeded.ShouldBeFalse();
    }

    // ---------------------------------------------------------------- RR-L2-04 / RV-04

    [Fact]
    public async Task RR_L2_04_ForgedTransformationMarker_DoesNotSkipNormalization()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim("__EnterpriseTransformed", "1", ClaimValueTypes.String, "https://evil-issuer"),
            new Claim("oid", "user-oid-1"),
            new Claim("roles", "DataOwner")
        ], "Bearer");

        var result = await new EnterpriseClaimsTransformation().TransformAsync(new ClaimsPrincipal(identity));

        result.FindFirst(ClaimTypes.PrimarySid)?.Value.ShouldBe("user-oid-1");
        result.Claims.Where(c => c.Type == "__EnterpriseTransformed").ShouldAllBe(c => c.Issuer == EnterpriseClaimsTransformation.MarkerIssuer);
    }

    [Theory]
    [InlineData("GatewayAdmin")]
    [InlineData("PlatformAdmin")]
    public async Task RV_04_TenantAdminAlias_IsNotExpandedToClusterAdmin(string alias)
    {
        var identity = new ClaimsIdentity([new Claim("oid", "user-oid-2"), new Claim(ClaimTypes.Role, alias)], "Bearer");

        var result = await new EnterpriseClaimsTransformation().TransformAsync(new ClaimsPrincipal(identity));

        result.IsInRole("ClusterAdmin").ShouldBeFalse();
        Autheris.Domain.Security.ClusterAdminPolicy.IsCanonicalClusterAdmin(result).ShouldBeFalse();
    }
}
