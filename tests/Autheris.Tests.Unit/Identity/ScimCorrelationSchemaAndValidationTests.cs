namespace Autheris.Tests.Unit.Identity;

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Autheris.Application.Identity.Validators;
using Autheris.Domain.Identity;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ScimCorrelationSchemaAndValidationTests
{
    private const string CanonicalAdSid = "S-1-5-21-111-222-333-1001";
    private const string CanonicalCloudSid = "S-1-12-1-1-2-3-4";
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ValidateGroupRequest_MissingObjectSid_ThrowsValidationException()
    {
        var req = new ScimGroupRequest(
            [ScimSchemaUris.CoreGroup],
            "ext-g1",
            "Engineering",
            Members: null,
            AutherisExtension: null);

        var ex = Should.Throw<ValidationException>(() => ScimRequestValidator.ValidateGroupRequest(req));
        ex.Message.ShouldContain("objectSid");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("S-1-5-32-544")]
    [InlineData("S-1-5-21-100-200-300-512")]
    public void ValidateGroupRequest_NonCanonicalOrPrivilegedSid_ThrowsValidationException(string invalidSid)
    {
        var req = new ScimGroupRequest(
            [ScimSchemaUris.CoreGroup, ScimSchemaUris.AutherisGroupExtension],
            "ext-g1",
            "Privileged",
            Members: null,
            AutherisExtension: new ScimGroupAutherisExtension(invalidSid));

        Should.Throw<ValidationException>(() => ScimRequestValidator.ValidateGroupRequest(req));
    }

    [Theory]
    [InlineData(CanonicalAdSid)]
    [InlineData(CanonicalCloudSid)]
    public void ValidateGroupRequest_CanonicalAdAndCloudSid_Succeeds(string validSid)
    {
        var req = new ScimGroupRequest(
            [ScimSchemaUris.CoreGroup, ScimSchemaUris.AutherisGroupExtension],
            "ext-g1",
            "Analysts",
            Members: [new ScimGroupMember("u-1", "Alice", ScimMemberTypes.User)],
            AutherisExtension: new ScimGroupAutherisExtension(validSid));

        Should.NotThrow(() => ScimRequestValidator.ValidateGroupRequest(req));
    }

    [Fact]
    public void ValidateUserRequest_OptionalCorrelationFields_ValidatesFormatAndRejectsPrivilegedSid()
    {
        var valid = new ScimUserRequest(
            [ScimSchemaUris.CoreUser, ScimSchemaUris.AutherisUserExtension],
            "ext-u1",
            "alice",
            "Alice",
            null,
            null,
            Active: true,
            AutherisExtension: new ScimUserAutherisExtension(
                ObjectSid: CanonicalAdSid,
                OnPremisesImmutableId: "AbCdEf123456==",
                UserPrincipalName: "Alice@Corp.Example",
                EntraObjectId: "11111111-2222-3333-4444-555555555555"));
        Should.NotThrow(() => ScimRequestValidator.ValidateUserRequest(valid));

        var privilegedSid = new ScimUserRequest(
            [ScimSchemaUris.CoreUser],
            "ext-u2",
            "baduser",
            null,
            null,
            null,
            ObjectSid: "S-1-5-32-544");
        Should.Throw<ValidationException>(() => ScimRequestValidator.ValidateUserRequest(privilegedSid));

        var badGuid = new ScimUserRequest(
            [ScimSchemaUris.CoreUser],
            "ext-u3",
            "badoid",
            null,
            null,
            null,
            EntraObjectId: "not-a-guid");
        Should.Throw<ValidationException>(() => ScimRequestValidator.ValidateUserRequest(badGuid));
    }

    [Fact]
    public void ValidateGroupRequest_MemberType_AcceptsUserAndGroup_RejectsInvalidType()
    {
        var ok = new ScimGroupRequest(
            [ScimSchemaUris.CoreGroup, ScimSchemaUris.AutherisGroupExtension],
            "ext-g1",
            "Parent",
            Members:
            [
                new ScimGroupMember("u-1", "Alice", ScimMemberTypes.User),
                new ScimGroupMember("g-child", "Child", ScimMemberTypes.Group),
                new ScimGroupMember("u-2", "Bob", null)
            ],
            AutherisExtension: new ScimGroupAutherisExtension(CanonicalAdSid));
        Should.NotThrow(() => ScimRequestValidator.ValidateGroupRequest(ok));

        var bad = ok with { Members = [new ScimGroupMember("x-1", "Bad", "ServicePrincipal")] };
        Should.Throw<ValidationException>(() => ScimRequestValidator.ValidateGroupRequest(bad));
    }

    [Fact]
    public void ScimUserAndGroup_JsonSerialization_RoundTripsAutherisExtensions()
    {
        var userJson = """
        {
          "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User", "urn:ietf:params:scim:schemas:extension:autheris:2.0:User"],
          "externalId": "ext-1",
          "userName": "alice",
          "active": true,
          "urn:ietf:params:scim:schemas:extension:autheris:2.0:User": {
            "objectSid": "S-1-5-21-111-222-333-1001",
            "onPremisesImmutableId": "imm-123",
            "userPrincipalName": "alice@corp.example",
            "entraObjectId": "11111111-2222-3333-4444-555555555555"
          }
        }
        """;

        var req = JsonSerializer.Deserialize<ScimUserRequest>(userJson, WebJson);
        req.ShouldNotBeNull();
        req!.ResolvedObjectSid.ShouldBe(CanonicalAdSid);
        req.ResolvedOnPremisesImmutableId.ShouldBe("imm-123");
        req.ResolvedUserPrincipalName.ShouldBe("alice@corp.example");
        req.ResolvedEntraObjectId.ShouldBe("11111111-2222-3333-4444-555555555555");

        var user = new ScimUser(
            "u-1", "ext-1", "alice", "Alice", "alice@corp.example", true, null, null, null,
            ObjectSid: CanonicalAdSid,
            OnPremisesImmutableId: "imm-123",
            UserPrincipalName: "alice@corp.example",
            EntraObjectId: "11111111-2222-3333-4444-555555555555");
        var serializedUser = JsonSerializer.Serialize(user, WebJson);
        using (var doc = JsonDocument.Parse(serializedUser))
        {
            var ext = doc.RootElement.GetProperty(ScimSchemaUris.AutherisUserExtension);
            ext.GetProperty("objectSid").GetString().ShouldBe(CanonicalAdSid);
            ext.GetProperty("onPremisesImmutableId").GetString().ShouldBe("imm-123");
            ext.GetProperty("userPrincipalName").GetString().ShouldBe("alice@corp.example");
            ext.GetProperty("entraObjectId").GetString().ShouldBe("11111111-2222-3333-4444-555555555555");
        }

        var group = new ScimGroup(
            "g-1", "ext-g1", "Engineering",
            [new ScimGroupMember("u-1", Type: ScimMemberTypes.User), new ScimGroupMember("g-2", Type: ScimMemberTypes.Group)],
            null, null,
            ObjectSid: CanonicalCloudSid);
        var serializedGroup = JsonSerializer.Serialize(group, WebJson);
        using (var doc = JsonDocument.Parse(serializedGroup))
        {
            var ext = doc.RootElement.GetProperty(ScimSchemaUris.AutherisGroupExtension);
            ext.GetProperty("objectSid").GetString().ShouldBe(CanonicalCloudSid);
        }
    }
}
