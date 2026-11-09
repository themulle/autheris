namespace Autheris.Tests.Unit.Security;

using System;
using Autheris.Application.Procedures.Services;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-06 (recheck 4): R4-7 (empty roles, duplicate YAML keys).</summary>
public sealed class SecurityReviewRecheck4Block1Tests
{
    [Theory]
    [InlineData("procedure: api.usp_X\nroles: []")]
    [InlineData("procedure: api.usp_X\nroles:\n  - ''\n  - '  '")]
    public void ParseYaml_EmptyRoles_Throws(string yaml)
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
        const string yaml = "procedure: api.usp_X\nroles: [admin]\nroles: [reader]";
        Should.Throw<FormatException>(() => ProcedureDefinitionParser.ParseYaml(yaml, "x", false, 30));
    }
}
