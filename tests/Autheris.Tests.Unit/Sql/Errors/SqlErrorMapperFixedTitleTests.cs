namespace Autheris.Tests.Unit.Sql.Errors;

using System;
using System.Linq;
using Autheris.Application.Sql.Errors;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using Xunit;

/// <summary>
/// NL-SC-30 (O-5b part-1 review finding 5): the Title of every mapped SQL error is the fixed message of its code. It takes no input,
/// so a dialect, exception text or value can never reach the problem body that <c>SqlErrorResponse</c> writes.
/// </summary>
[Trait("Category", "Security")]
public sealed class SqlErrorMapperFixedTitleTests
{
    private const string Canary = "CANARY-7f3a-secret-value";

    [Fact]
    public void Every_Governed_Code_Maps_To_Its_Fixed_Title_Without_Exception_Text()
    {
        var codes = typeof(GovernedSqlErrorCodes).GetFields()
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        codes.ShouldNotBeEmpty();

        foreach (var code in codes)
        {
            foreach (var dialect in Enum.GetValues<TargetSqlDialect>())
            {
                var ex = new GovernedSqlException(code, dialect);
                if (!SqlCompilerErrorMapper.TryMap(ex, out var mapping))
                {
                    continue;
                }

                mapping.Title.ShouldBe(GovernedSqlErrorCodes.FixedMessage(mapping.Code), $"code {code}, dialect {dialect}");
            }
        }
    }

    [Theory]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(TimeoutException))]
    [InlineData(typeof(ArgumentException))]
    [InlineData(typeof(NotSupportedException))]
    public void A_Mapped_Title_Never_Carries_The_Exception_Message(Type exceptionType)
    {
        var ex = (Exception)Activator.CreateInstance(exceptionType, Canary)!;
        if (SqlCompilerErrorMapper.TryMap(ex, out var mapping))
        {
            mapping.Title.ShouldNotContain(Canary);
        }

        SqlCompilerErrorMapper.FixedMessageOrDefault(ex, "fallback").ShouldNotContain(Canary);
    }
}
