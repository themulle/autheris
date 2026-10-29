using System.Text.Json;
using Autheris.Application.Governance;
using Autheris.Extensions.Dbt;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Dbt;

public sealed class DbtOwnerSpecValidatorTests
{
    private static readonly string[] Caller = ["S-1-5-21-100", "CORP\\alice", "alice@corp.example"];

    private static DbtOwnerError? Run(string itemJson, params string[] callerIdentifiers)
    {
        using var doc = JsonDocument.Parse(itemJson);
        var parsed = DbtOwnerSpecValidator.Parse(doc.RootElement);
        if (parsed.Error is not null)
        {
            return parsed.Error;
        }

        return DbtOwnerSpecValidator.Validate(parsed.Owners, callerIdentifiers.Length == 0 ? Caller : callerIdentifiers);
    }

    [Fact]
    public void Validate_ValidOwnerObject_Accepts()
    {
        Run("""{"owner":{"sid":"S-1-5-21-200","account":"CORP\\bob","display_name":"Bob B","email":"bob@corp.example","role":"delegate"}}""").ShouldBeNull();
    }

    [Fact]
    public void Validate_MissingSid_Rejects()
    {
        var error = Run("""{"owner":{"display_name":"Bob"}}""");
        error.ShouldNotBeNull();
        error.Field.ShouldBe("sid");
    }

    [Theory]
    [InlineData("system:dbt-sync")]
    [InlineData("S-1-5-21-FORWARD-GRP-finance")]
    [InlineData("SYSTEM:x")]
    public void Validate_ReservedSidPrefix_Rejects(string sid)
    {
        var error = Run($$$"""{"owner":{"sid":"{{{sid}}}"}}""");
        error.ShouldNotBeNull();
        error.Field.ShouldBe("sid");
    }

    [Theory]
    [InlineData("Bob\\u0000")]
    [InlineData("Bob\\n")]
    [InlineData("Bob\\u202EBob")]
    [InlineData("Bob\\u200B")]
    public void Validate_ControlCharInDisplayName_Rejects(string name)
    {
        var error = Run($$$"""{"owner":{"sid":"S-1-5-21-200","display_name":"{{{name}}}"}}""");
        error.ShouldNotBeNull();
        error.Field.ShouldBe("display_name");
    }

    [Fact]
    public void Validate_DisplayNameTooLong_Rejects()
    {
        Run($$$"""{"owner":{"sid":"S-1-5-21-200","display_name":"{{{new string('a', 201)}}}"}}""")!.Field.ShouldBe("display_name");
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("Bob <bob@corp.example>")]
    [InlineData("a@b.c ")]
    public void Validate_InvalidEmail_Rejects(string email)
    {
        var error = Run($$$"""{"owner":{"sid":"S-1-5-21-200","email":"{{{email}}}"}}""");
        error.ShouldNotBeNull();
        error.Field.ShouldBe("email");
    }

    [Fact]
    public void Validate_GroupKey_Rejects()
    {
        var error = Run("""{"owner":{"sid":"S-1-5-21-200","group":"finance"}}""");
        error.ShouldNotBeNull();
        error.Code.ShouldBe("dbt_owner_group_unsupported");
        Run("""{"group":"finance"}""")!.Code.ShouldBe("dbt_owner_group_unsupported");
    }

    [Fact]
    public void Validate_UnknownOwnerKey_Rejects()
    {
        Run("""{"owner":{"sid":"S-1-5-21-200","manager":"x"}}""")!.Code.ShouldBe("dbt_owner_invalid");
    }

    [Fact]
    public void Validate_InvalidRole_Rejects()
    {
        Run("""{"owner":{"sid":"S-1-5-21-200","role":"admin"}}""")!.Field.ShouldBe("role");
    }

    [Fact]
    public void Validate_BareString_IsLookupOnly()
    {
        using var doc = JsonDocument.Parse("""{"owners":["bob@corp.example"]}""");
        var parsed = DbtOwnerSpecValidator.Parse(doc.RootElement);
        parsed.Error.ShouldBeNull();
        parsed.Owners.Single().LookupOnly.ShouldBeTrue();
        parsed.Owners.Single().Sid.ShouldBe("bob@corp.example");
    }

    [Theory]
    [InlineData(" S-1-5-21-200")]
    [InlineData("S-1-5-21 200")]
    public void Validate_WhitespaceInSid_Rejects(string sid)
    {
        Run($$$"""{"owner":{"sid":"{{{sid}}}"}}""")!.Field.ShouldBe("sid");
    }

    [Fact]
    public void Validate_SelfSid_Rejects()
    {
        Run("""{"owner":{"sid":"S-1-5-21-100"}}""")!.Code.ShouldBe("dbt_owner_self_assignment");
    }

    [Theory]
    [InlineData("""{"owner":{"sid":"S-1-5-21-200","account":"corp\\ALICE"}}""")]
    [InlineData("""{"owner":{"sid":"S-1-5-21-200","email":"ALICE@corp.example"}}""")]
    [InlineData("""{"owner":{"sid":"alice@Corp.Example"}}""")]
    [InlineData("""{"owners":["s-1-5-21-100"]}""")]
    [InlineData("""{"owner":{"sid":"ITSM_SNOW:inst:alice@corp.example"}}""")]
    public void Validate_SelfIdentifierAlias_Rejects(string json)
    {
        Run(json)!.Code.ShouldBe("dbt_owner_self_assignment");
    }

    [Fact]
    public void Validate_SelfSid_UnicodeNormalizationVariant_Rejects()
    {
        // "é" as e + combining acute versus precomposed
        Run("""{"owner":{"sid":"café"}}""", "café")!.Code.ShouldBe("dbt_owner_self_assignment");
    }

    [Fact]
    public void Validate_TooManyOwners_Rejects()
    {
        var owners = string.Join(",", Enumerable.Range(0, DbtOwnerSpecValidator.MaxOwnersPerTable + 1).Select(i => $$$"""{"sid":"S-1-5-21-{{{i + 500}}}"}"""));
        Run($$$"""{"owners":[{{{owners}}}]}""")!.Code.ShouldBe("dbt_owner_too_many");
    }

    [Fact]
    public void Validate_ErrorMessage_DoesNotEchoIdentifier()
    {
        var error = Run("""{"owner":{"sid":"S-1-5-21-100"}}""")!;
        (error.Code + error.Field).ShouldNotContain("S-1-5-21-100");
    }

    [Fact]
    public void OwnerIdentifier_AreSame_IgnoresCaseWhitespaceAndNormalization()
    {
        OwnerIdentifier.AreSame(" CORP\\Alice ", "corp\\alice").ShouldBeTrue();
        OwnerIdentifier.AreSame("café", "CAFÉ").ShouldBeTrue();
        OwnerIdentifier.AreSame("", "").ShouldBeFalse();
    }

    [Fact]
    public void HasOwnerKeys_DetectsOwnerOwnersAndGroup()
    {
        using var doc = JsonDocument.Parse("""{"table":"t","owners":[]}""");
        DbtOwnerSpecValidator.HasOwnerKeys(doc.RootElement).ShouldBeTrue();
        using var none = JsonDocument.Parse("""{"table":"t"}""");
        DbtOwnerSpecValidator.HasOwnerKeys(none.RootElement).ShouldBeFalse();
    }
}
