namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Autheris.Application.Catalog.Localization;
using Autheris.Domain.Model;
using Shouldly;
using Xunit;

public sealed class LocalizedTextsValidatorTests
{
    [Fact]
    public void Validate_ValidTexts_DoesNotThrow()
    {
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["de"] = new("Bestellungen", "Kundenbestellungen und Lieferungen", "Detaillierte Beschreibung der Bestellhistorie."),
            ["en"] = new("Orders", "Customer orders and shipments", "Detailed description of order history.")
        });

        Should.NotThrow(() => LocalizedTextsValidator.Validate(texts));
    }

    [Fact]
    public void Validate_TitleExceedingMaxLength_ThrowsValidationException()
    {
        var longTitle = new string('a', 129);
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["en"] = new(longTitle, "Valid description", null)
        });

        var ex = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(texts));
        ex.Message.ShouldContain("Title exceeds maximum length");
    }

    [Fact]
    public void Validate_DescriptionExceedingMaxLength_ThrowsValidationException()
    {
        var longDesc = new string('a', 4001);
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["en"] = new("Title", longDesc, null)
        });

        var ex = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(texts));
        ex.Message.ShouldContain("Description exceeds maximum length");
    }

    [Fact]
    public void Validate_LongDescriptionExceedingMaxLength_ThrowsValidationException()
    {
        var longDesc = new string('a', 32001);
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["en"] = new("Title", "Desc", longDesc)
        });

        var ex = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(texts));
        ex.Message.ShouldContain("LongDescription exceeds maximum length");
    }

    [Theory]
    [InlineData("<script>alert('xss')</script>")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("hello <a href=\"evil.com\">click</a>")]
    [InlineData("test <b>bold</b> tag")]
    public void Validate_HtmlTags_ThrowsValidationException(string malicious)
    {
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["de"] = new(malicious, "Safe description", null)
        });

        var ex = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(texts));
        ex.Message.ShouldContain("HTML tags are not permitted");
    }

    [Theory]
    [InlineData("Title with \u202E RTL override")]
    [InlineData("Desc with \u202B embedded RTL")]
    [InlineData("Long with \u2066 isolate")]
    public void Validate_BiDiOverrides_ThrowsValidationException(string bidi)
    {
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["de"] = new("Safe title", bidi, null)
        });

        var ex = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(texts));
        ex.Message.ShouldContain("BiDi control characters are not permitted");
    }

    [Theory]
    [InlineData("Title with \u0000 null byte")]
    [InlineData("Desc with \u0007 bell char")]
    [InlineData("Desc with \u001B escape char")]
    public void Validate_DisallowedControlCharacters_ThrowsValidationException(string ctrl)
    {
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["de"] = new("Safe title", ctrl, null)
        });

        var ex = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(texts));
        ex.Message.ShouldContain("Control characters are not permitted");
    }

    [Fact]
    public void Validate_AllowedWhitespaceCharacters_DoesNotThrow()
    {
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["de"] = new("Title", "Line 1\r\nLine 2\tTabbed", "Paragraph 1\n\nParagraph 2")
        });

        Should.NotThrow(() => LocalizedTextsValidator.Validate(texts));
    }

    [Fact]
    public void Validate_InvalidLanguageTag_ThrowsValidationException()
    {
        var texts = new LocalizedTexts(new Dictionary<string, LocalizedTextEntry>
        {
            ["invalid_tag_123"] = new("Title", "Description", null)
        });

        var ex = Should.Throw<ValidationException>(() => LocalizedTextsValidator.Validate(texts));
        ex.Message.ShouldContain("Invalid BCP 47 language tag");
    }
}
