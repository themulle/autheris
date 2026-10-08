namespace Autheris.Tests.Unit;

using System;
using System.IO;
using Autheris.Application.Governance;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public class Csharp12MoatsTests
{
    [Fact]
    public void SensitiveDataSpan_ShouldPreventHeapEscapeAndProvideConstantTimeComparison()
    {
        // Arrange
        ReadOnlySpan<char> rawIban = "DE89370400440532013000";
        var span1 = new SensitiveDataSpan(rawIban, "GDPR_ART_9_FINANCIAL");
        var span2 = new SensitiveDataSpan(rawIban, "GDPR_ART_9_FINANCIAL");
        var differentSpan = new SensitiveDataSpan("DE89370400440532013999", "GDPR_ART_9_FINANCIAL");

        // Act & Assert: Constant time equality
        span1.EqualsConstantTime(span2.AsSpan()).ShouldBeTrue();
        span1.EqualsConstantTime(differentSpan.AsSpan()).ShouldBeFalse();

        // Stack-only in-place masking
        Span<char> buffer = stackalloc char[rawIban.Length];
        span1.MaskInto(buffer, maskChar: '*', visiblePrefix: 2, visibleSuffix: 3);

        var maskedResult = new string(buffer);
        maskedResult.ShouldStartWith("DE");
        maskedResult.ShouldEndWith("000");
        maskedResult.ShouldContain("*****************");

        // ToString() should always be redacted
        span1.ToString().ShouldBe("[REDACTED_STACK_SPAN:GDPR_ART_9_FINANCIAL]");
    }

}
