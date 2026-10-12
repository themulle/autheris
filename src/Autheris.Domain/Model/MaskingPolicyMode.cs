namespace Autheris.Domain.Model;

public enum MaskingPolicyMode
{
    Default = 0,   // Standard: hinterlegte Spaltenmaskierungsregeln greifen
    Unmasked = 1,  // Ausnahme: alle Spalten im Klartext (Clear)
    Strict = 2     // Zwingend: alle sensiblen Spalten zwingend maskiert
}
