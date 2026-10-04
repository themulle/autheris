namespace Autheris.Application.Extensibility;

public enum IngressDecision
{
    Continue = 0,
    Challenge = 1,
    Deny = 2,
    ShortCircuit = 3
}
