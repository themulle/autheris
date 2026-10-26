namespace Autheris.Tests.Unit.Olap;

using System;
using System.Linq;
using System.Reflection;
using Autheris.Application.Olap;
using Shouldly;
using Xunit;

/// <summary>ARCH-MNT-08-FU-01: the rejection carries a closed reason, never caller text (WAVE3-OLAP-SC-02, SC-03).</summary>
public sealed class OlapSqlRejectionReasonTests
{
    public static TheoryData<OlapSqlRejectionReason> AllReasons => [.. Enum.GetValues<OlapSqlRejectionReason>()];

    [Theory]
    [MemberData(nameof(AllReasons))]
    [Trait("Category", "Security")]
    public void WAVE3_OLAP_SC_02_T_01_EveryReason_HasCodeAndFixedMessage(OlapSqlRejectionReason reason)
    {
        var ex = new OlapSqlRejectedException(reason);

        ex.Reason.ShouldBe(reason);
        ex.Code.ShouldStartWith("OLAP_");
        ex.Message.ShouldNotBeNullOrWhiteSpace();
        ex.Message.ShouldBe(new OlapSqlRejectedException(reason).Message);
        new OlapSqlRejectedException(reason, new InvalidOperationException("secret-marker-inner")).Message.ShouldBe(ex.Message);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WAVE3_OLAP_SC_03_T_02_Exception_HasNoStringMessageConstructor()
    {
        var ctors = typeof(OlapSqlRejectedException).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        ctors.ShouldNotBeEmpty();
        ctors.SelectMany(c => c.GetParameters()).ShouldNotContain(p => p.ParameterType == typeof(string));
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WAVE3_OLAP_SC_03_CodeConstants_KeepTheirValues()
    {
        OlapSqlRejectedException.Invalid.ShouldBe("OLAP_SQL_INVALID");
        OlapSqlRejectedException.NotAnalyzable.ShouldBe("OLAP_SQL_NOT_ANALYZABLE");
        OlapSqlRejectedException.ConstructNotAllowed.ShouldBe("OLAP_SQL_CONSTRUCT_NOT_ALLOWED");
        OlapSqlRejectedException.ColumnPolicy.ShouldBe("OLAP_COLUMN_POLICY");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WAVE3_OLAP_SC_02_UnknownReason_FailsClosedToInvalidStatement()
    {
        var ex = new OlapSqlRejectedException((OlapSqlRejectionReason)999);

        ex.Reason.ShouldBe(OlapSqlRejectionReason.InvalidStatement);
        ex.Code.ShouldBe(OlapSqlRejectedException.Invalid);
        ex.HttpStatus.ShouldBe(400);
        ex.Message.ShouldBe(new OlapSqlRejectedException(OlapSqlRejectionReason.InvalidStatement).Message);
    }

    [Theory]
    [MemberData(nameof(AllReasons))]
    [Trait("Category", "Security")]
    public void WAVE3_OLAP_SC_02_T_06_OnlyColumnPolicy_Maps403(OlapSqlRejectionReason reason)
    {
        var ex = new OlapSqlRejectedException(reason);

        ex.HttpStatus.ShouldBe(reason == OlapSqlRejectionReason.ColumnPolicy ? 403 : 400);
        (ex.Code == OlapSqlRejectedException.ColumnPolicy).ShouldBe(reason == OlapSqlRejectionReason.ColumnPolicy);
    }
}
