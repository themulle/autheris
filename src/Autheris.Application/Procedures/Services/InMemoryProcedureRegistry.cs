namespace Autheris.Application.Procedures.Services;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Domain.Model;

public sealed class InMemoryProcedureRegistry : IProcedureRegistry
{
    private readonly ConcurrentDictionary<string, RegisteredProcedure> _items = new(StringComparer.OrdinalIgnoreCase);

    public void Register(ProcedureDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // A (re)registered declaration is always Pending until the validator confirms it (fail-closed).
        _items[definition.Name] = new RegisteredProcedure(definition, ProcedureState.Pending, null, null, null);
    }

    public bool TryGet(string name, out RegisteredProcedure? procedure) => _items.TryGetValue(name, out procedure);

    public IReadOnlyList<RegisteredProcedure> GetAll() => _items.Values.ToList();

    public bool Unregister(string name) => _items.TryRemove(name, out _);

    public void MarkActive(string name, ProcedureValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(validation);
        _items.AddOrUpdate(
            name,
            _ => throw new KeyNotFoundException(name),
            (_, current) => current with
            {
                State = ProcedureState.Active,
                Validation = validation,
                ValidatedAt = DateTimeOffset.UtcNow,
                DisabledReason = null
            });
    }

    public void MarkDisabled(string name, string reason)
    {
        _items.AddOrUpdate(
            name,
            _ => throw new KeyNotFoundException(name),
            (_, current) => current with
            {
                State = ProcedureState.Disabled,
                ValidatedAt = DateTimeOffset.UtcNow,
                DisabledReason = reason
            });
    }

    public bool TryMarkActive(ProcedureDefinition expected, ProcedureValidationResult validation)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(validation);
        return TryUpdate(expected, current => current with
        {
            State = ProcedureState.Active,
            Validation = validation,
            ValidatedAt = DateTimeOffset.UtcNow,
            DisabledReason = null
        });
    }

    public bool TryMarkDisabled(ProcedureDefinition expected, string reason)
    {
        ArgumentNullException.ThrowIfNull(expected);
        return TryUpdate(expected, current => current with
        {
            State = ProcedureState.Disabled,
            ValidatedAt = DateTimeOffset.UtcNow,
            DisabledReason = reason
        });
    }

    private bool TryUpdate(ProcedureDefinition expected, Func<RegisteredProcedure, RegisteredProcedure> update)
    {
        while (_items.TryGetValue(expected.Name, out var current))
        {
            if (!ReferenceEquals(current.Definition, expected))
            {
                return false; // replaced (hot reload) or a different declaration with the same name
            }

            if (_items.TryUpdate(expected.Name, update(current), current))
            {
                return true;
            }
        }

        return false;
    }
}
