using Autheris.Application.Sql.Policy;
using Autheris.Domain.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

namespace Autheris.Tests.Unit.Sql.Policy;

/// <summary>WP-A (GAP-013, SEC-PG-03): the date translation is an explicit opt-in of governed WebSQL, never a default of the factory.</summary>
public sealed class CompileRequestFactoryDateTranslationTests
{
    private sealed class Empty : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => false;
        public PolicyPredicate GetPredicate(TableIdentity table) => PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => false;
        public MaskSpec GetMask(TableIdentity table, string column) => throw new InvalidOperationException();
    }

    private static CompileRequest Create(bool translate) =>
        CompileRequestFactory.Create(
            new GovernancePolicy
            {
                RowFilters = new Empty(),
                Masks = new Empty(),
                Catalog = new InMemoryTableCatalog([], "main"),
                Tenant = new TenantBinding("t", "acme", SqlParameterType.String),
                Dml = DmlGuardOptions.Strict
            },
            TargetSqlDialect.Sqlite,
            new WebSqlOptions(),
            isDml: false,
            enforcedMaxRows: 10,
            allowedFunctions: new HashSet<string> { "lower" },
            translateDateFunctions: translate);

    [Trait("Category", "Security")]
    [Fact]
    public void TranslateDateFunctions_IsOffByDefault_AndOnlyWhenRequested()
    {
        // SEC-PG-03: only the governed WebSQL caller opts in.
        Create(translate: false).TranslateTrinoDateFunctions.ShouldBeFalse();
        Create(translate: true).TranslateTrinoDateFunctions.ShouldBeTrue();
    }
}
