namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Application.Procedures.Services;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-06 (recheck 4): R4-7 (empty roles, duplicate YAML keys).</summary>
public sealed class SecurityReviewRecheck4Block1Tests
{
    [Theory]
    [InlineData("procedure: api.usp_X\nrequired_roles: []")]
    [InlineData("procedure: api.usp_X\nrequired_roles:\n  - ''\n  - '  '")]
    public void ParseYaml_EmptyRequiredRoles_Throws(string yaml)
    {
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(yaml, "x", false, 30));
    }

    [Fact]
    public void ParseYaml_NoRolesKey_StillAllowedForAnyAuthenticatedUser()
    {
        var def = ProcedureDefinitionParser.ParseYaml("procedure: api.usp_X", "x", false, 30);
        def.RequiredRoles.ShouldBeEmpty();
    }

    [Fact]
    public void ParseYaml_DuplicateKey_Throws()
    {
        const string yaml = "procedure: api.usp_X\nrequired_roles: [admin]\nrequired_roles: [reader]";
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(yaml, "x", false, 30));
    }

    [Fact]
    public void Parse_EmptyRolesDirective_Throws()
    {
        const string sql = "-- @procedure api.usp_X\n-- @roles\n";
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.Parse(sql, "x", false, 30));
    }
}
