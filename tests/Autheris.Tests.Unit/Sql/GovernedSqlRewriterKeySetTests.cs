namespace Autheris.Tests.Unit.Sql;

using System.Security;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Policy.RowFilterKeySets;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Sql.Policy;
using Autheris.Tests.Unit.VirtualFilters.Federation;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #107 WP-C: the full governed pipeline (stage 3 decision, assembly with the key-set stage, real compiler) with fakes for the key load.
/// Conditions: SEC-RF107-15 (retry once, only after a substituted site, kind only), -17, -18, -19, AC-8 (byte identical without opt-in).
/// </summary>
[Trait("Category", "Security")]
public sealed class GovernedSqlRewriterKeySetTests : IDisposable
{
    private const string Tenant = "tenant_a";
    private const string ShortSql = "SELECT id FROM public.orders";
    private static readonly TableIdentifier OrdersId = new("default", "public", "orders");
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private readonly List<KeySetPipeline> _fixtures = [];

    private KeySetPipeline Create(bool optIn = true, int keyCount = 3, int maxKeys = 2000, bool wireStage = true)
    {
        var p = KeySetPipeline.Create(optIn, keyCount, maxKeys, wireStage);
        _fixtures.Add(p);
        return p;
    }

    public void Dispose()
    {
        foreach (var f in _fixtures) f.Dispose();
    }

    private static void FailWith(KeySetPipeline f, Func<int, Exception?> failure) => f.Failure = failure;

    private static ClaimsPrincipal User() =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.PrimarySid, "S-1-5-21-KS"), new Claim("tenant_id", Tenant)], "Test"));

    private static Task<Autheris.Application.Sql.Internal.GovernedRewrite> Rewrite(KeySetPipeline f, string sql = ShortSql, CancellationToken ct = default) =>
        f.Rewriter.RewriteCoreAsync(sql, User(), new TenantId(Tenant), dataSourceName: null, dmlContext: null, ct);

    private static SqlLimitExceededException Limit(SqlLimitKind kind, long requested = 987_654_321, long maximum = 123_456_789) =>
        new(kind, TargetSqlDialect.PostgreSql, requested, maximum);

    // ---- end to end and AC-8 ----

    [Fact]
    public async Task KeySetCache_EndToEnd_UsesTheInListWithBinds()
    {
        var f = Create();

        var rewrite = await Rewrite(f);

        rewrite.Compiled.Sql.ShouldContain(" IN (");
        rewrite.Compiled.Sql.ShouldNotContain("EXISTS");
        rewrite.Compiled.Parameters.Count(p => p.SourceName?.StartsWith("__rls_ks0_", StringComparison.Ordinal) == true).ShouldBe(4);
        var outcome = rewrite.KeySets.ShouldNotBeNull().Single();
        outcome.Outcome.ShouldBe(KeySetOutcomeKind.Miss);
        f.Loader.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task WithoutOptIn_TheStatementIsByteIdenticalToTheStageFreeAssembly_AC8()
    {
        var withoutStage = Create(optIn: false, wireStage: false);
        var withStageOff = Create(optIn: false);

        var baseline = await Rewrite(withoutStage);
        var current = await Rewrite(withStageOff);

        current.Compiled.Sql.ShouldBe(baseline.Compiled.Sql);
        current.Compiled.Parameters.Select(p => (p.Name, p.Value)).ShouldBe(baseline.Compiled.Parameters.Select(p => (p.Name, p.Value)));
        current.KeySets.ShouldBeEmpty();
        withStageOff.Loader.Calls.ShouldBe(0);
        withStageOff.Epochs.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task ShortStatement_2000Keys_CompilesWithExists_D_WPC_3()
    {
        var f = Create(keyCount: 2000);

        var rewrite = await Rewrite(f);   // 27 characters: a 2,048 item list can never fit the 4,096 character budget

        rewrite.Compiled.Sql.ShouldContain("EXISTS");
        rewrite.KeySets.ShouldNotBeNull().Single().Reason.ShouldBe(KeySetFallbackReason.ExpansionBudget);
        f.Compiles.Count.ShouldBe(1);
    }

    // ---- retry (SEC-RF107-15, -19, D-WPC-4) ----

    private async Task<string> ExistsSql() => (await Rewrite(Create(optIn: false))).Compiled.Sql;

    [Fact]
    public async Task BindLimit_RetriesOnceWithExists_SEC_RF107_15()
    {
        var f = Create();
        FailWith(f, n => n == 1 ? Limit(SqlLimitKind.BindParameters) : null);

        var rewrite = await Rewrite(f);

        f.Compiles.Count.ShouldBe(2);
        rewrite.Compiled.Sql.ShouldBe(await ExistsSql());
        rewrite.KeySets.ShouldNotBeNull().Single().Reason.ShouldBe(KeySetFallbackReason.BindLimitRetry);
        rewrite.KeySets!.Single().Outcome.ShouldBe(KeySetOutcomeKind.Fallback);
    }

    [Fact]
    public async Task ExpansionLimit_RetriesOnceWithExists_AndReportsExpansionLimitRetry_D_WPC_4()
    {
        var f = Create();
        FailWith(f, n => n == 1 ? Limit(SqlLimitKind.PolicyExpansionFactor) : null);

        var rewrite = await Rewrite(f);

        f.Compiles.Count.ShouldBe(2);
        rewrite.Compiled.Sql.ShouldBe(await ExistsSql());
        rewrite.KeySets.ShouldNotBeNull().Single().Reason.ShouldBe(KeySetFallbackReason.ExpansionLimitRetry);
    }

    [Fact]
    public async Task RetryOnlyWhenSiteSubstituted_D_WPC_4()
    {
        var f = Create(optIn: false);
        FailWith(f, _ => Limit(SqlLimitKind.BindParameters));

        await Should.ThrowAsync<SqlLimitExceededException>(() => Rewrite(f));

        f.Compiles.Count.ShouldBe(1);   // the EXISTS form would fail identically
    }

    [Fact]
    public async Task SecondLimitException_Propagates_AndIsNotRetriedAgain_SEC_RF107_19()
    {
        var f = Create();
        FailWith(f, _ => Limit(SqlLimitKind.PolicyExpansionFactor));

        var ex = await Should.ThrowAsync<SqlLimitExceededException>(() => Rewrite(f));

        ex.Kind.ShouldBe(SqlLimitKind.PolicyExpansionFactor);
        f.Compiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task OtherLimitKinds_DoNotRetry_D_WPC_4()
    {
        var f = Create();
        FailWith(f, _ => Limit(SqlLimitKind.EmittedSqlLength));

        await Should.ThrowAsync<SqlLimitExceededException>(() => Rewrite(f));

        f.Compiles.Count.ShouldBe(1);
    }

    [Fact]
    public async Task OtherCompilerErrors_DoNotRetry_SEC_RF107_15()
    {
        var f = Create();
        FailWith(f, _ => new SecurityException("coverage"));

        await Should.ThrowAsync<SecurityException>(() => Rewrite(f));

        f.Compiles.Count.ShouldBe(1);
    }

    [Fact]
    public async Task RetryStartsFromUnsubstitutedPredicate_SEC_RF107_15()
    {
        var f = Create();
        FailWith(f, n => n == 1 ? Limit(SqlLimitKind.BindParameters) : null);

        await Rewrite(f);

        var table = new TrinoSqlEngine.Governance.TableIdentity("public", "orders");
        var first = f.Compiles[0].Request.Policy.RowFilters.GetPredicate(table);
        var second = f.Compiles[1].Request.Policy.RowFilters.GetPredicate(table);
        // the retried assembly started from the original decision: its predicate still carries the registered EXISTS site and no key binds
        CorrelatedKeySetSubstitution.FindSites(first).ShouldBeEmpty();
        CorrelatedKeySetSubstitution.FindSites(second).Count.ShouldBe(1);
        second.Parameters.Keys.ShouldNotContain(k => k.StartsWith("__rls_ks", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retry_UsesSameToken_NoKeyLoad_SEC_RF107_19()
    {
        var f = Create();
        FailWith(f, n => n == 1 ? Limit(SqlLimitKind.BindParameters) : null);
        using var cts = new CancellationTokenSource();

        await Rewrite(f, ct: cts.Token);

        f.Compiles[0].Token.ShouldBe(cts.Token);
        f.Compiles[1].Token.ShouldBe(cts.Token);
        f.Loader.Calls.ShouldBe(1);   // the retry loads no keys
        f.Epochs.Calls.ShouldBe(1);   // and reads no epochs
    }

    [Fact]
    public async Task FirstExceptionValues_NotInLogsOrAudit_SEC_RF107_15()
    {
        var f = Create();
        FailWith(f, n => n == 1 ? Limit(SqlLimitKind.PolicyExpansionFactor, requested: 987_654_321, maximum: 123_456_789) : null);

        var rewrite = await Rewrite(f);

        string audit = System.Text.Json.JsonSerializer.Serialize(rewrite.KeySets);
        audit.ShouldNotContain("987654321");
        audit.ShouldNotContain("123456789");
        f.Logs.ShouldAllBe(l => !l.Message.Contains("987654321") && !l.Message.Contains("123456789"));
    }

    [Fact]
    public async Task UnknownStatementType_StageNotInvoked_SEC_RF107_17()
    {
        // A statement the stage does not classify as SELECT never reads epochs, the cache or the loader (DML is refused by WebSQL policy here
        // before the assembly; the factory-level twins cover the assembly itself).
        var f = Create();
        await Should.ThrowAsync<WebSqlPolicyException>(() => f.Rewriter.RewriteCoreAsync("UPDATE public.orders SET id = 1", User(), new TenantId(Tenant), null, null, default));

        f.Loader.Calls.ShouldBe(0);
        f.Epochs.Calls.ShouldBe(0);
    }
}
