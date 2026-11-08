namespace Autheris.Tests.Unit.Docs;

using System;
using System.Text;
using Autheris.Application.Docs.Services;
using Shouldly;
using Xunit;

public sealed class UntrustedMarkdownSanitizerTests
{
    private const int DefaultMaxBytes = 1_048_576;

    [Fact]
    [Trait("Category", "Security")]
    public void Removes_bidi_zero_width_and_tag_characters()
    {
        // U+202E (RLO bidi), U+200B (ZWSP), U+FEFF (BOM), U+E0041 (tag char)
        var input = "# Title\u202E with \u200Bzero-\uFEFFwidth and \U000E0041tag characters";
        var result = UntrustedMarkdownSanitizer.Sanitize(input, DefaultMaxBytes);

        result.Changed.ShouldBeTrue();
        result.Text.ShouldBe("# Title with zero-width and tag characters");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Removes_control_characters_except_newline_and_tab()
    {
        // \x00 (NUL), \x07 (BEL), \x1B (ESC), \x7F (DEL), \x85 (NEL C1)
        var input = "# Heading\x00\x07\nLine 1\twith tab\x1B and text\x7F\x85.";
        var result = UntrustedMarkdownSanitizer.Sanitize(input, DefaultMaxBytes);

        result.Changed.ShouldBeTrue();
        result.Text.ShouldBe("# Heading\nLine 1\twith tab and text.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Caps_combining_mark_runs_at_four()
    {
        // 'x' followed by 8 combining accents (no precomposed character exists in Unicode)
        var input = "x\u0300\u0301\u0302\u0303\u0304\u0305\u0306\u0307 text";
        var result = UntrustedMarkdownSanitizer.Sanitize(input, DefaultMaxBytes);

        result.Changed.ShouldBeTrue();
        result.Text.ShouldBe("x\u0300\u0301\u0302\u0303 text");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Removes_html_comments_and_script_content()
    {
        var input = """
        # Documentation

        <!-- Confidential internal comment -->
        Visible paragraph.

        <script type="text/javascript">
            alert('xss');
        </script>

        <style>
            body { display: none; }
        </style>

        <b>Bold text</b> and <span class="badge">Span text</span>.
        """;

        var result = UntrustedMarkdownSanitizer.Sanitize(input, DefaultMaxBytes);

        result.Changed.ShouldBeTrue();
        result.Text.ShouldNotContain("<!--");
        result.Text.ShouldNotContain("Confidential internal comment");
        result.Text.ShouldNotContain("<script");
        result.Text.ShouldNotContain("alert('xss')");
        result.Text.ShouldNotContain("<style");
        result.Text.ShouldNotContain("display: none");
        result.Text.ShouldNotContain("<b>");
        result.Text.ShouldNotContain("</b>");
        result.Text.ShouldNotContain("<span");
        result.Text.ShouldContain("Visible paragraph.");
        result.Text.ShouldContain("Bold text and Span text.");
    }

    [Theory]
    [InlineData("javascript:alert(1)", "javascript")]
    [InlineData("data:text/html,<script>alert(1)</script>", "data")]
    [InlineData("vbscript:MsgBox(1)", "vbscript")]
    [InlineData("file:///etc/passwd", "file")]
    [Trait("Category", "Security")]
    public void Rewrites_links_with_disallowed_schemes_to_text(string url, string label)
    {
        var input = $"Here is a [{label} link]({url}) and an image ![{label} img]({url}).";
        var result = UntrustedMarkdownSanitizer.Sanitize(input, DefaultMaxBytes);

        result.Changed.ShouldBeTrue();
        result.Text.ShouldNotContain(url);
        result.Text.ShouldBe($"Here is a {label} link and an image {label} img.");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Drops_leading_front_matter_block()
    {
        var input = """
        ---
        title: Internal Runbook
        author: S-1-5-21-999
        private_metadata: forbidden
        ---
        # Actual Runbook Content

        Instructions for remediation.
        """;

        var result = UntrustedMarkdownSanitizer.Sanitize(input, DefaultMaxBytes);

        result.Changed.ShouldBeTrue();
        result.Text.ShouldNotContain("private_metadata");
        result.Text.ShouldNotContain("Internal Runbook");
        result.Text.TrimStart().ShouldStartWith("# Actual Runbook Content");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rejects_content_over_max_bytes_after_sanitising()
    {
        var input = new string('A', 500);

        var ex = Should.Throw<DocValidationException>(() =>
        {
            UntrustedMarkdownSanitizer.Sanitize(input, maxBytes: 100);
        });

        ex.Code.ShouldBe("DOC_TOO_LARGE");
    }

    [Fact]
    public void Leaves_clean_markdown_unchanged_and_reports_not_changed()
    {
        var input = """
        # Clean Documentation

        This is a standard paragraph with [a valid link](https://example.com/docs).
        Another relative link: [runbook](./runbook.md).

        - List item 1
        - List item 2

        ```csharp
        var x = 10;
        ```
        """;

        var result = UntrustedMarkdownSanitizer.Sanitize(input, DefaultMaxBytes);

        result.Changed.ShouldBeFalse();
        result.Text.ShouldBe(input);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rejects_empty_or_whitespace_markdown()
    {
        Should.Throw<DocValidationException>(() => UntrustedMarkdownSanitizer.Sanitize("", DefaultMaxBytes))
            .Code.ShouldBe("DOC_EMPTY");

        Should.Throw<DocValidationException>(() => UntrustedMarkdownSanitizer.Sanitize("   \n\t  ", DefaultMaxBytes))
            .Code.ShouldBe("DOC_EMPTY");

        // Front-matter only becomes empty
        var fmOnly = "---\nfoo: bar\n---\n";
        Should.Throw<DocValidationException>(() => UntrustedMarkdownSanitizer.Sanitize(fmOnly, DefaultMaxBytes))
            .Code.ShouldBe("DOC_EMPTY");
    }

    [Fact]
    public void SanitizeTitle_AppliesRuneRulesAndTruncates()
    {
        var title = "Clean Title \u202E\u200Bwith Controls\x00";
        var sanitized = UntrustedMarkdownSanitizer.SanitizeTitle(title, maxLength: 50);
        sanitized.ShouldBe("Clean Title with Controls");

        var longTitle = new string('A', 300);
        var truncated = UntrustedMarkdownSanitizer.SanitizeTitle(longTitle, maxLength: 200);
        truncated.Length.ShouldBe(200);
    }

    [Fact]
    public void SanitizeTags_CapsLengthAndCount()
    {
        var tags = new[] { "tag1", "tag\u200B2", new string('b', 100), "", "tag1" };
        var sanitized = UntrustedMarkdownSanitizer.SanitizeTags(tags, maxTags: 5, maxTagLength: 20);

        sanitized.Count.ShouldBe(3);
        sanitized[0].ShouldBe("tag1");
        sanitized[1].ShouldBe("tag2");
        sanitized[2].Length.ShouldBe(20);
    }
}
