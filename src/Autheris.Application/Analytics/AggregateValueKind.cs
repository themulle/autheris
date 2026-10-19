namespace Autheris.Application.Analytics;

/// <summary>Static value kind of an aggregate or metric result (decided from the function and the column type, so a NULL value still has a kind).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Represents result value kinds.")]
public enum AggregateValueKind
{
    String,
    Int,
    Decimal,
    Float,
    Boolean,
    DateTime
}
