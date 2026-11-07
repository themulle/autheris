namespace Autheris.Application.Sql.Endpoints.Interfaces;

using System.Collections.Generic;
using Autheris.Domain.Model;

public interface ISqlEndpointRegistry
{
    void Register(SqlEndpointDefinition definition);
    bool TryGet(string name, out SqlEndpointDefinition? definition);
    IReadOnlyList<SqlEndpointDefinition> GetAll();
    bool Unregister(string name);
    void Clear();
}
