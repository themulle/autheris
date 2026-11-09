namespace Autheris.Tests.Unit.Docs;

using Autheris.Application.Docs.Services;
using Shouldly;
using Xunit;

public sealed class DocumentRulesTests
{
    [Theory]
    [InlineData("..", false)]
    [InlineData("a..b", false)]
    [InlineData("app/name", false)]
    [InlineData("app\\name", false)]
    [InlineData("-invalid-start", false)]
    [InlineData(".invalid-start", false)]
    [InlineData("_invalid-start", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("valid-doc.id_1", true)]
    [InlineData("UPPERCASE-DOC-ID", true)] // normalized after ToLowerInvariant
    [Trait("Category", "Security")]
    public void Rejects_identifiers_outside_pattern(string identifier, bool expectedValid)
    {
        DocumentRules.IsValidDocId(identifier).ShouldBe(expectedValid);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Rejects_identifiers_exceeding_max_length()
    {
        var validApp = new string('a', 64);
        var invalidApp = new string('a', 65);
        DocumentRules.IsValidAppId(validApp).ShouldBeTrue();
        DocumentRules.IsValidAppId(invalidApp).ShouldBeFalse();

        var validDoc = new string('a', 128);
        var invalidDoc = new string('a', 129);
        DocumentRules.IsValidDocId(validDoc).ShouldBeTrue();
        DocumentRules.IsValidDocId(invalidDoc).ShouldBeFalse();
    }

    [Theory]
    [InlineData("autheris", true)]
    [InlineData("AUTHERIS", true)]
    [InlineData("global", true)]
    [InlineData("Global", true)]
    [InlineData("system", true)]
    [InlineData("SYSTEM", true)]
    [InlineData("other-app", false)]
    [InlineData("billing", false)]
    [Trait("Category", "Security")]
    public void Reserved_app_ids_are_reserved(string appId, bool expectedReserved)
    {
        DocumentRules.IsReservedAppId(appId).ShouldBe(expectedReserved);
    }

    [Fact]
    public void Generated_doc_id_is_deterministic_for_same_title_and_content()
    {
        var title = "Troubleshooting Database Timeouts";
        var content = "# Database Timeouts\n\nRun the remediation script.";

        var id1 = DocumentRules.GenerateDocId(title, content);
        var id2 = DocumentRules.GenerateDocId(title, content);

        id1.ShouldBe(id2);
        id1.ShouldStartWith("troubleshooting-database-timeouts-");
        id1.Length.ShouldBeLessThanOrEqualTo(128);
        DocumentRules.IsValidDocId(id1).ShouldBeTrue();

        var idDifferentContent = DocumentRules.GenerateDocId(title, content + " altered");
        idDifferentContent.ShouldNotBe(id1);
    }

    [Theory]
    [InlineData("ERR_TIMEOUT", true)]
    [InlineData("ERR-404_NOT-FOUND", true)]
    [InlineData("err_lowercase", false)]
    [InlineData("INVALID SPACE", false)]
    [InlineData("", false)]
    public void Validates_error_code_pattern(string errorCode, bool expectedValid)
    {
        DocumentRules.IsValidErrorCode(errorCode).ShouldBe(expectedValid);
    }
}
