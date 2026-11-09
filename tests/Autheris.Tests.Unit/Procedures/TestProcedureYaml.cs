namespace Autheris.Tests.Unit.Procedures;

using Autheris.Application.Procedures.Services;
using Autheris.Domain.Model;

/// <summary>Builds <see cref="ProcedureDefinition"/> instances from YAML declarations for tests.</summary>
internal static class TestProcedureYaml
{
    public static ProcedureDefinition Definition(string yaml, string defaultName = "x", bool allowRlsNone = false, int maxTimeoutSeconds = 60)
        => ProcedureDefinitionParser.ParseYaml(yaml, defaultName, allowRlsNone, maxTimeoutSeconds);
}
