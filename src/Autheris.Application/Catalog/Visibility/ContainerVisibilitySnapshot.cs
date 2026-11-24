using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Application.Catalog.Visibility;

/// <summary>Caller-independent description of a container (business domain or data product) as shown on a catalog tile.</summary>
public sealed record ContainerInfo(
    ContainerRef Container,
    string Name,
    string? Description,
    string? Contact,
    CatalogVisibilityMode Visibility,
    Sid? OwnerGroupSid,
    bool IsSystemUnassigned = false);

/// <summary>
/// Per (tenant, caller) view of what the container visibility rule needs: the tenant's containers with their visibility, the
/// containers the caller holds an active catalog grant on, the containers the caller owns, the tables with an active data grant,
/// and the caller's governance or IT capacity. Rule: an object is visible when its container is <c>Open</c>, or the caller holds a
/// catalog grant on the container, or a data grant on the object, or is Data Governance or IT, or is an owner-group member.
/// A snapshot that could not be built (<see cref="IsAvailable"/> false) proves nothing but the capacity flags.
/// </summary>
public sealed class ContainerVisibilitySnapshot
{
    private static readonly IReadOnlyDictionary<ContainerRef, ContainerInfo> NoContainers = new Dictionary<ContainerRef, ContainerInfo>();
    private static readonly IReadOnlySet<ContainerRef> NoContainerRefs = new HashSet<ContainerRef>();
    private static readonly IReadOnlySet<TableIdentifier> NoTables = new HashSet<TableIdentifier>();

    public ContainerVisibilitySnapshot(
        IReadOnlyDictionary<ContainerRef, ContainerInfo> containers,
        IReadOnlySet<ContainerRef> catalogGrantedContainers,
        IReadOnlySet<ContainerRef> ownedContainers,
        IReadOnlySet<TableIdentifier> dataGrantedTables,
        bool isGovernance,
        bool isItAdmin,
        bool groupsComplete,
        bool isAvailable = true)
    {
        Containers = containers ?? throw new ArgumentNullException(nameof(containers));
        CatalogGrantedContainers = catalogGrantedContainers ?? throw new ArgumentNullException(nameof(catalogGrantedContainers));
        OwnedContainers = ownedContainers ?? throw new ArgumentNullException(nameof(ownedContainers));
        DataGrantedTables = dataGrantedTables ?? throw new ArgumentNullException(nameof(dataGrantedTables));
        IsGovernance = isGovernance;
        IsItAdmin = isItAdmin;
        GroupsComplete = groupsComplete;
        IsAvailable = isAvailable;
    }

    /// <summary>Fail-closed snapshot: nothing is provably visible except through the capacity flags.</summary>
    public static ContainerVisibilitySnapshot Unavailable(bool isGovernance = false, bool isItAdmin = false) =>
        new(NoContainers, NoContainerRefs, NoContainerRefs, NoTables, isGovernance, isItAdmin, groupsComplete: false, isAvailable: false);

    public IReadOnlyDictionary<ContainerRef, ContainerInfo> Containers { get; }
    public IReadOnlySet<ContainerRef> CatalogGrantedContainers { get; }
    public IReadOnlySet<ContainerRef> OwnedContainers { get; }
    public IReadOnlySet<TableIdentifier> DataGrantedTables { get; }
    public bool IsGovernance { get; }
    public bool IsItAdmin { get; }
    public bool GroupsComplete { get; }
    public bool IsAvailable { get; }

    /// <summary>True when the caller has Data Governance or IT capacity and therefore sees every container and object.</summary>
    public bool SeesEverything => IsGovernance || IsItAdmin;

    public bool IsContainerVisible(ContainerRef container)
    {
        if (SeesEverything)
        {
            return true;
        }

        if (!IsAvailable || !Containers.TryGetValue(container, out var info))
        {
            return false;
        }

        return info.Visibility == CatalogVisibilityMode.Open
            || CatalogGrantedContainers.Contains(container)
            || OwnedContainers.Contains(container);
    }

    public bool IsTableVisible(ContainerRef container, TableIdentifier table) =>
        SeesEverything || DataGrantedTables.Contains(table) || IsContainerVisible(container);

    /// <summary>Access state shown on a container tile: <c>OPEN</c>, <c>GRANTED</c> or <c>NONE</c> (the caller adds <c>PENDING</c>).</summary>
    public string AccessState(ContainerRef container)
    {
        if (!Containers.TryGetValue(container, out var info))
        {
            return SeesEverything ? "GRANTED" : "NONE";
        }

        if (SeesEverything || CatalogGrantedContainers.Contains(container) || OwnedContainers.Contains(container))
        {
            return "GRANTED";
        }

        return info.Visibility == CatalogVisibilityMode.Open && IsAvailable ? "OPEN" : "NONE";
    }
}
