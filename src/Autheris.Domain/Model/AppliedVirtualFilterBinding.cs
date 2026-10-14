namespace Autheris.Domain.Model;

/// <summary>
/// Value identity of an applied (filter, binding) pair (F-GOV-14 SEC-20): tenant, filter id, definition hash, binding
/// hash and the snapshot generation the pair was resolved from. Two pairs with the same identity enforce the same rule.
/// </summary>
public readonly record struct AppliedVirtualFilterBindingKey(
    TenantId TenantId,
    Guid FilterId,
    string FilterDefinitionHash,
    string BindingHash,
    long SnapshotGeneration);

/// <summary>
/// F-GOV-14 (M1): one Virtual Filter applied to an object through one binding, as decided by the
/// <c>MandatoryRowFilterResolver</c> (after grantee matching and <c>supersedes</c>). Equality is by
/// <see cref="Key"/>, never by reference.
/// </summary>
public sealed record AppliedVirtualFilterBinding
{
    public AppliedVirtualFilterBinding(VirtualFilter filter, FilterBinding binding, long snapshotGeneration)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(binding);
        Filter = filter;
        Binding = binding;
        SnapshotGeneration = snapshotGeneration;
        Key = new AppliedVirtualFilterBindingKey(filter.TenantId, filter.Id, filter.ComputeDefinitionHash(), binding.ComputeHash(), snapshotGeneration);
    }

    public VirtualFilter Filter { get; }

    public FilterBinding Binding { get; }

    public long SnapshotGeneration { get; }

    public AppliedVirtualFilterBindingKey Key { get; }

    public bool Equals(AppliedVirtualFilterBinding? other) => other is not null && Key == other.Key;

    public override int GetHashCode() => Key.GetHashCode();

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append("Filter = ").Append(Filter.Name).Append(", Generation = ").Append(SnapshotGeneration);
        return true;
    }
}
