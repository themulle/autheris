namespace Autheris.Application.Procedures.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

/// <summary>F-SQL-02: Registry of declared stored procedure endpoints and their validation state.</summary>
public interface IProcedureRegistry
{
    void Register(ProcedureDefinition definition);
    bool TryGet(string name, out RegisteredProcedure? procedure);
    IReadOnlyList<RegisteredProcedure> GetAll();
    bool Unregister(string name);
    void MarkActive(string name, ProcedureValidationResult validation);
    void MarkDisabled(string name, string reason);
}
