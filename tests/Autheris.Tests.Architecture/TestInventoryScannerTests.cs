namespace Autheris.Tests.Architecture;

using System;
using System.Collections.Generic;
using Shouldly;
using Xunit;

public sealed class TestInventoryScannerTests
{
    [Fact]
    public void Theory_WithSemicolonInInlineData_IsFound()
    {
        const string source = """
            namespace Autheris.Tests.Unit.Sample;
            using Xunit;

            public sealed class SemicolonTheoryTests
            {
                [Theory]
                [Trait("Category", "Security")]
                [InlineData("a;b")]
                [InlineData("DROP TABLE x; --")]
                public void Rejects_semicolon_payload(string payload) { }
            }
            """;

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(source, allTests, securityTests);

        const string expected = "Autheris.Tests.Unit.Sample.SemicolonTheoryTests.Rejects_semicolon_payload";
        allTests.ShouldContain(expected);
        securityTests.ShouldContain(expected);
    }

    [Fact]
    public void Theory_WithBracesInInlineData_IsFound()
    {
        const string source = """
            namespace Autheris.Tests.Unit.Sample;
            using Xunit;

            public sealed class BracesTheoryTests
            {
                [Theory]
                [Trait("Category", "Security")]
                [InlineData("{ orders { id } }")]
                [InlineData("}")]
                [InlineData("{")]
                public void Rejects_braces_payload(string query) { }
            }
            """;

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(source, allTests, securityTests);

        const string expected = "Autheris.Tests.Unit.Sample.BracesTheoryTests.Rejects_braces_payload";
        allTests.ShouldContain(expected);
        securityTests.ShouldContain(expected);
    }

    [Fact]
    public void SecurityTrait_WithSemicolonInPrecedingComment_IsFound()
    {
        const string source = """
            namespace Autheris.Tests.Unit.Sample;
            using Xunit;

            [Trait("Category", "Security")]
            /// <summary>Ensures doc comments with ; and } do not hide class-level security traits.</summary>
            public sealed class CommentClassSecurityTests
            {
                [Fact]
                public void Class_security_trait_survives_doc_comment() { }
            }

            public sealed class CommentMethodSecurityTests
            {
                [Fact]
                [Trait("Category", "Security")]
                // Semicolon; and braces { } in single-line comment must not cut off attributes
                /* Block comment with ; and { } */
                public void Method_security_trait_survives_comments() { }
            }
            """;

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(source, allTests, securityTests);

        const string classExpected = "Autheris.Tests.Unit.Sample.CommentClassSecurityTests.Class_security_trait_survives_doc_comment";
        const string methodExpected = "Autheris.Tests.Unit.Sample.CommentMethodSecurityTests.Method_security_trait_survives_comments";

        allTests.ShouldContain(classExpected);
        securityTests.ShouldContain(classExpected);
        allTests.ShouldContain(methodExpected);
        securityTests.ShouldContain(methodExpected);
    }

    [Fact]
    public void RawStringLiteral_WithSeparators_DoesNotHideNextTest()
    {
        const string source = """"
            namespace Autheris.Tests.Unit.Sample;
            using Xunit;

            [Trait("Category", "Security")]
            public sealed class OuterRawStringTests
            {
                [Fact]
                public void First_test_with_raw_string()
                {
                    const string payload = """
                        line 1; still inside raw string
                        } } }
                        """;
                }

                [Theory]
                [InlineData("""[{"type":"primary_key","columns":["id","ts DESC; DROP TABLE x"]}]""")]
                public void Second_test_after_raw_string(string spec) { }

                public sealed class NestedAfterUnbalancedRawStringTests
                {
                    [Fact]
                    public void Nested_inherits_outer_security_trait() { }
                }
            }
            """";

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(source, allTests, securityTests);

        const string secondExpected = "Autheris.Tests.Unit.Sample.OuterRawStringTests.Second_test_after_raw_string";
        const string nestedExpected = "Autheris.Tests.Unit.Sample.NestedAfterUnbalancedRawStringTests.Nested_inherits_outer_security_trait";

        allTests.ShouldContain(secondExpected);
        securityTests.ShouldContain(secondExpected);
        allTests.ShouldContain(nestedExpected);
        securityTests.ShouldContain(nestedExpected);
    }

    [Fact]
    public void Theory_WithLongInlineDataListOver600Chars_IsFound()
    {
        const string source = """
            namespace Autheris.Tests.Unit.Sample;
            using Xunit;

            public sealed class LongInlineDataTests
            {
                [Theory]
                [Trait("Category", "Security")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
                public void Rejects_long_inline_data_list(string payload) { }
            }
            """;

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(source, allTests, securityTests);

        const string expected = "Autheris.Tests.Unit.Sample.LongInlineDataTests.Rejects_long_inline_data_list";
        allTests.ShouldContain(expected);
        securityTests.ShouldContain(expected);
    }

    [Fact]
    public void Theory_WithArrayInitializerInInlineData_IsFound()
    {
        const string source = """
            namespace Autheris.Tests.Unit.Sample;
            using Xunit;

            public sealed class ArrayInlineDataTests
            {
                [Theory]
                [InlineData("Query", new[] { "query_sql", "query_graphql" })]
                [InlineData("Catalog", new[] { "list_datasets" })]
                [Trait("Category", "Security")]
                public void Rejects_array_inline_data_without_hiding_theory(string group, string[] tools) { }
            }
            """;

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(source, allTests, securityTests);

        const string expected = "Autheris.Tests.Unit.Sample.ArrayInlineDataTests.Rejects_array_inline_data_without_hiding_theory";
        allTests.ShouldContain(expected);
        securityTests.ShouldContain(expected);
    }

    [Fact]
    public void Masking_PreservesLengthAndNewlines()
    {
        const string source =
            "// line comment ; { }\r\n" +
            "/* multi\nline ; { } */\r\n" +
            "/// <summary>doc ; { }</summary>\n" +
            "public sealed class Sample\r\n" +
            "{\r\n" +
            "    private const string S = \"a;b{c}d\";\n" +
            "    private const string V = @\"v;\n{x}\";\n" +
            "    private readonly string I = $\"i {1}; {2} }} {{\";\n" +
            "    private readonly string IV = $@\"iv\n{1}; }}\";\n" +
            "    private readonly string IR = $\"\"\"\n        ir {1}; }}\n        \"\"\";\n" +
            "    private readonly string IRR = $$\"\"\"\n        irr {{1}}; }};\n        \"\"\";\n" +
            "    private const char C = ';';\n" +
            "    private static ReadOnlySpan<byte> U => \"u8; {}\"u8;\n" +
            "    private const string R = \"\"\"\n" +
            "        raw ; { }\n" +
            "        \"\"\";\n" +
            "}\n" +
            "// trailing comment at EOF ; { }";

        var masked = TestInventoryScanner.MaskLiteralsAndComments(source);

        masked.Length.ShouldBe(source.Length);
        for (int i = 0; i < source.Length; i++)
        {
            if (source[i] is '\r' or '\n')
            {
                masked[i].ShouldBe(source[i], $"Newline character at index {i} must be preserved.");
            }
        }

        masked.ShouldContain("public sealed class Sample");
        masked.ShouldContain("private const string S = ");
        masked.ShouldNotContain("line comment");
        masked.ShouldNotContain("a;b{c}d");
        masked.ShouldNotContain("raw ; { }");
        masked.ShouldNotContain("u8; {}");
        masked.ShouldNotContain("trailing comment at EOF");
        // Verify no stray ';' or '{' or '}' survived inside any literal or comment.
        // The only unmasked structural characters in source are class '{', '}', property '=>', and 9 trailing statement ';'.
        masked.Count(c => c == ';').ShouldBe(9);
        masked.Count(c => c == '{').ShouldBe(1);
        masked.Count(c => c == '}').ShouldBe(1);

        TestInventoryScanner.MaskLiteralsAndComments(string.Empty).ShouldBeEmpty();
        Should.Throw<ArgumentNullException>(() => TestInventoryScanner.MaskLiteralsAndComments(null!));
    }

    [Fact]
    public void CommentedOutSecurityTrait_DoesNotTagPlainTestAsSecurity()
    {
        const string source = """
            namespace Autheris.Tests.Unit.Sample;
            using Xunit;

            public sealed class CommentedOutTraitTests
            {
                [Fact]
                // [Trait("Category", "Security")]
                /* [Trait("Category", "Security")] */
                public void Plain_test_with_commented_out_trait() { }
            }
            """;

        var allTests = new HashSet<string>(StringComparer.Ordinal);
        var securityTests = new HashSet<string>(StringComparer.Ordinal);

        TestInventoryScanner.ExtractFromSource(source, allTests, securityTests);

        const string expected = "Autheris.Tests.Unit.Sample.CommentedOutTraitTests.Plain_test_with_commented_out_trait";
        allTests.ShouldContain(expected);
        securityTests.ShouldNotContain(expected);
    }
}
