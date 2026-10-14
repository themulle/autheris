using System.Collections.Immutable;
using Autheris.Application.Sql.Policy;
using Autheris.Domain.Options;
using Shouldly;
using System.Security;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>WP-X0l: <see cref="CompileRequestFactory"/> replaces the mutable <c>RlsOptions</c> bag of <c>GovernedSqlRewriter</c> (lines 671-704).</summary>
public sealed class CompileRequestFactoryTests
{
    private static GovernancePolicy Policy(DmlGuardOptions? dml = null) => new()
    {
        RowFilters = new Empty(),
        Masks = new Empty(),
        Catalog = new InMemoryTableCatalog([], "main"),
        Tenant = new TenantBinding("t", "acme", SqlParameterType.String),
        Dml = dml ?? DmlGuardOptions.Strict
    };

    private sealed class Empty : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private static CompileRequest Create(
        TargetSqlDialect dialect = TargetSqlDialect.PostgreSql, bool isDml = false, long maxRows = 100, IReadOnlySet<string>? functions = null, GovernancePolicy? policy = null) =>
        CompileRequestFactory.Create(policy ?? Policy(), dialect, new WebSqlOptions(), isDml, maxRows, functions ?? new HashSet<string> { "lower" });

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql)]
    [InlineData(TargetSqlDialect.SqlServer)]
    [InlineData(TargetSqlDialect.Sqlite)]
    [InlineData(TargetSqlDialect.Oracle)]
    public void Create_MapsTargetDialect(TargetSqlDialect dialect) => Create(dialect).TargetDialect.ShouldBe(dialect);

    [Fact]
    public void Create_MapsEnforcedMaxRows() => Create(maxRows: 1001).EnforcedMaxRows.ShouldBe(1001);

    [Fact]
    public void Create_MapsEnforceCatalogProjection() => Create().EnforceCatalogProjection.ShouldBeTrue();

    [Fact]
    public void Create_MapsEnforceReadOnlyQueries_ToReadOnlyStatements() => Create(isDml: false).Statements.ShouldBe(StatementPermissions.ReadOnly);

    [Fact]
    public void Create_Dml_PermitsTheDmlStatementClasses() =>
        Create(isDml: true).Statements.ShouldBe(
            StatementPermissions.Insert | StatementPermissions.Update | StatementPermissions.Delete | StatementPermissions.Merge);

    [Fact]
    public void Create_MapsAllowedFunctions_AsAClosedCopy()
    {
        var functions = new HashSet<string> { "lower" };

        var request = Create(functions: functions);
        functions.Add("load_extension");

        request.AllowedFunctions.ShouldBe(new[] { "lower" });
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, false)]
    [InlineData(TargetSqlDialect.SqlServer, true)]
    [InlineData(TargetSqlDialect.Sqlite, true)]
    public void Create_MapsTokenGuards_PerDialect(TargetSqlDialect dialect, bool rejectBracketLexerDifferentials)
    {
        var guards = Create(dialect).TokenGuards;

        guards.RejectComments.ShouldBeTrue();
        guards.RejectBackslashInStrings.ShouldBeTrue();
        guards.RejectEscapedStringLiterals.ShouldBeTrue();
        guards.RejectNonAsciiIdentifiers.ShouldBeTrue();
        guards.RejectDotsInQuotedIdentifiers.ShouldBeTrue();
        guards.RejectTimeTravelQueries.ShouldBeTrue();
        guards.RejectDollarQuoting.ShouldBeTrue();
        guards.RejectBracketLexerDifferentials.ShouldBe(rejectBracketLexerDifferentials);
    }

    [Fact]
    public void Create_Databricks_RejectsVariableSubstitution() =>
        CompileLimits.Normalize(Create(TargetSqlDialect.Databricks)).TokenGuards.RejectVariableSubstitutionSequences.ShouldBeTrue();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DmlGuards_AlwaysStrict(bool isDml)
    {
        var strict = Policy();

        var request = Create(isDml: isDml, policy: strict);

        request.Policy.ShouldBeSameAs(strict);
        request.Policy.Dml.ShouldBe(DmlGuardOptions.Strict);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelaxedDmlGuards_AreAConfigurationError_NotSilentlyRepaired(bool isDml)
    {
        var relaxed = DmlGuardOptions.Strict with { RejectUnfilteredDml = false, RejectConsentFilteredInsert = false };

        // CR-ADG-38: the guards are not a per-request choice. The factory never assigns them (an architecture scan forbids it in
        // production code); a policy that carries another value is rejected exactly like the compiler rejects it.
        Should.Throw<SqlCompileConfigurationException>(() => Create(isDml: isDml, policy: Policy(relaxed)));
    }

    [Fact]
    public void Create_KeepsThePolicyProviders()
    {
        var policy = Policy();

        var request = Create(policy: policy);

        request.Policy.RowFilters.ShouldBeSameAs(policy.RowFilters);
        request.Policy.Masks.ShouldBeSameAs(policy.Masks);
        request.Policy.Catalog.ShouldBeSameAs(policy.Catalog);
        request.Policy.Tenant.ShouldBe(policy.Tenant);
    }

    [Fact]
    public void Create_NullArguments_Throw()
    {
        Should.Throw<ArgumentNullException>(() => CompileRequestFactory.Create(null!, TargetSqlDialect.Sqlite, new WebSqlOptions(), false, 1, new HashSet<string>()));
        Should.Throw<ArgumentNullException>(() => CompileRequestFactory.Create(Policy(), TargetSqlDialect.Sqlite, null!, false, 1, new HashSet<string>()));
        Should.Throw<ArgumentNullException>(() => CompileRequestFactory.Create(Policy(), TargetSqlDialect.Sqlite, new WebSqlOptions(), false, 1, null!));
    }

    [Fact]
    public void Create_NegativeMaxRows_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() => Create(maxRows: -1));
}
