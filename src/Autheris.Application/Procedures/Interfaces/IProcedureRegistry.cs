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

    /// <summary>
    /// Review P-7: Activates the entry only if it still holds <paramref name="expected"/> (compare-and-swap). A definition
    /// that was replaced by hot reload during validation stays Pending and is validated in the next round.
    /// </summary>
    bool TryMarkActive(ProcedureDefinition expected, ProcedureValidationResult validation);

    /// <summary>Review P-7: Disables the entry only if it still holds <paramref name="expected"/>.</summary>
    bool TryMarkDisabled(ProcedureDefinition expected, string reason);
}
