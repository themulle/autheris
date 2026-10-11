namespace Autheris.Tests.SqlCompilerGate;

using Autheris.Tests.SqlCompilerGate.Classification;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Shouldly;

/// <summary>The directional classification of G4 (plan 16.5 M-1), on synthetic outcomes: no database needed.</summary>
public sealed class ClassifierTests
{
    private static readonly PolicyOracle TenantOracle = new(PolicyScenario.Tenant);
    private static readonly PolicyOracle MaskOracle = new(PolicyScenario.TenantConsentMask);

    private static EngineOutcome.Rows Rows(params object?[][] rows) => new(rows);

    private static DifferenceClass Class(EngineOutcome legacy, EngineOutcome ast, PolicyOracle? oracle = null) =>
        Classifier.Classify(legacy, ast, oracle ?? TenantOracle).Class;

    private static string ClearEmail => GateModel.EmailOf("orders", GateModel.TenantA, 1);

    private static RowChange Change(char sign, string tenant, long id) =>
        new(sign, "users", [id, tenant, GateModel.MarkerOf("users", tenant, id), "open", GateModel.EmailOf("users", tenant, id)]);

    [Fact]
    public void Classifier_FlagsAstLooserRows_MaskWrite()
    {
        // rows: the AST path returns a row (a tenant-B id) that legacy did not
        Class(Rows([1L], [2L]), Rows([1L], [2L], [1001L])).ShouldBe(DifferenceClass.AstLooserRows);

        // rows: no tenant-B marker, but a row legacy did not return
        Class(Rows([1L]), Rows([1L], [3L])).ShouldBe(DifferenceClass.AstLooserRows);

        // mask: a clear value of a masked column where legacy returned the mask
        Class(Rows([1L, "***"]), Rows([1L, ClearEmail]), MaskOracle).ShouldBe(DifferenceClass.AstLooserMask);

        // write: the AST path changed a row that legacy did not
        var legacyWrite = new EngineOutcome.Written([Change('-', "A", 1), Change('+', "A", 1)]);
        var astWrite = new EngineOutcome.Written([Change('-', "A", 1), Change('+', "A", 1), Change('-', "A", 2), Change('+', "A", 2)]);
        Class(legacyWrite, astWrite).ShouldBe(DifferenceClass.AstLooserWrite);
    }

    [Fact]
    public void Classifier_FlagsAstLooserWrite_WhenAForeignRowChanges()
    {
        var legacyWrite = new EngineOutcome.Written([Change('-', "A", 1)]);
        var astWrite = new EngineOutcome.Written([Change('-', "A", 1), Change('-', "B", 1001)]);

        var result = Classifier.Classify(legacyWrite, astWrite, TenantOracle);

        result.Class.ShouldBe(DifferenceClass.AstLooserWrite);
        result.AstViolations.ShouldContain(v => v.Kind == OracleViolationKind.InvisibleRowChanged);
    }

    [Fact]
    public void Classifier_FlagsAstLooserAccepts_OnlyWhenTheOracleFlagsTheResult()
    {
        var rejected = new EngineOutcome.Rejected(RejectionStage.Compile, "SecurityException", true);

        Class(rejected, Rows([1001L])).ShouldBe(DifferenceClass.AstLooserAccepts);
        Class(rejected, Rows([1L, "***"]), MaskOracle).ShouldBe(DifferenceClass.AstAccepts);
        Class(rejected, Rows([1L, ClearEmail]), MaskOracle).ShouldBe(DifferenceClass.AstLooserAccepts);
    }

    [Fact]
    public void Classifier_AstRejectingWhatLegacyAccepts_IsStricterOrError()
    {
        var governed = new EngineOutcome.Rejected(RejectionStage.Compile, "SqlCompileNotSupportedException", true);
        var unexpected = new EngineOutcome.Rejected(RejectionStage.Compile, "NullReferenceException", false);
        var database = new EngineOutcome.Rejected(RejectionStage.Execute, "NpgsqlException", false);

        Class(Rows([1L]), governed).ShouldBe(DifferenceClass.AstStricter);
        Class(Rows([1L]), unexpected).ShouldBe(DifferenceClass.AstError);
        Class(Rows([1L]), database).ShouldBe(DifferenceClass.AstError);
        Class(governed, database).ShouldBe(DifferenceClass.BothRejected);
    }

    [Fact]
    public void Classifier_FewerAstRows_AreStricter_NotLooser()
    {
        Class(Rows([1L], [2L]), Rows([1L])).ShouldBe(DifferenceClass.AstStricter);
        Class(new EngineOutcome.Written([Change('-', "A", 1), Change('-', "A", 2)]), new EngineOutcome.Written([Change('-', "A", 1)]))
            .ShouldBe(DifferenceClass.AstStricter);
    }

    [Fact]
    public void Classifier_Identical_And_EquivalentAfterTypedNormalization()
    {
        Class(Rows([1L, 2.50m]), Rows([1L, 2.50m])).ShouldBe(DifferenceClass.Identical);
        // numeric scale and integer width are normalized
        Class(Rows([1L, 2.5m]), Rows([1, 2.50m])).ShouldBe(DifferenceClass.EquivalentResult);
        // the result is a multiset: row order does not matter
        Class(Rows([1L], [2L]), Rows([2L], [1L])).ShouldBe(DifferenceClass.Identical);
        // nothing else is equivalent: trailing spaces of a VARCHAR value are a difference
        Class(Rows(["a"]), Rows(["a "])).ShouldBe(DifferenceClass.AstLooserRows);
    }

    [Theory]
    [InlineData(DifferenceClass.AstLooserRows)]
    [InlineData(DifferenceClass.AstLooserMask)]
    [InlineData(DifferenceClass.AstLooserWrite)]
    [InlineData(DifferenceClass.AstLooserAccepts)]
    public void EveryLooserClass_IsFlaggedAsLooser_AndNoOtherClassIs(DifferenceClass cls)
    {
        cls.IsLooser().ShouldBeTrue();
        cls.ToWireName().ShouldStartWith("ast-looser-");
        Enum.GetValues<DifferenceClass>().Where(c => c.IsLooser()).Count().ShouldBe(4);
    }
}
