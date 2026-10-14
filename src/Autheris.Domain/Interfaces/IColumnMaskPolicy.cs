namespace Autheris.Domain.Interfaces;

/// <summary>
/// Opaque typed column masks carried by a <see cref="TableAccessDecision"/> (WP-X0f, SEC-ADG-02). Domain must not reference the SQL
/// engine, so the typed mask descriptors stay behind this abstraction and are implemented in Application. There is deliberately no
/// factory from text: a mask is a typed descriptor whose values are bound parameters, never a dialect SQL string.
/// </summary>
public interface IColumnMaskPolicy
{
    /// <summary>Hash of the mask shapes (kinds, argument and parameter names and types); values are not part of it.</summary>
    string Fingerprint { get; }

    /// <summary>The columns that carry a typed mask.</summary>
    IReadOnlyCollection<string> MaskedColumns { get; }

    bool HasMask(string column);
}
