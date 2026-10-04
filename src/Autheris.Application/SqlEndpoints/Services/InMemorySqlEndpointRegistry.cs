namespace Autheris.Application.SqlEndpoints.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.SqlEndpoints.Interfaces;
using Autheris.Domain.Model;

public sealed class InMemorySqlEndpointRegistry : ISqlEndpointRegistry
{
    private readonly ConcurrentDictionary<string, SqlEndpointDefinition> _endpoints = new(StringComparer.OrdinalIgnoreCase);

    public void Register(SqlEndpointDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        _endpoints[definition.Name] = definition;
    }

    public bool TryGet(string name, out SqlEndpointDefinition? definition)
    {
        return _endpoints.TryGetValue(name, out definition);
    }

    public IReadOnlyList<SqlEndpointDefinition> GetAll()
    {
        return _endpoints.Values.ToList();
    }

    public bool Unregister(string name)
    {
        return _endpoints.TryRemove(name, out _);
    }

    public void Clear()
    {
        _endpoints.Clear();
    }
}
