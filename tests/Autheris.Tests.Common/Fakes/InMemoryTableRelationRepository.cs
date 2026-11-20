using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;

namespace Autheris.Tests.Common.Fakes;

/// <summary>
/// In-memory <see cref="ITableRelationRepository"/> for unit tests.
/// </summary>
public sealed class InMemoryTableRelationRepository : ITableRelationRepository
{
    private readonly object _gate = new();
    private readonly List<TableRelation> _relations = [];
    private readonly List<TableRelation> _createCalls = [];

    public InMemoryTableRelationRepository(params TableRelation[] seed)
    {
        foreach (var relation in seed)
        {
            Add(relation);
        }
    }

    public bool BulkRelationsAuthoritative { get; init; } = true;
    public Action<TableRelation>? OnCreateRelation { get; set; }

    public IReadOnlyList<TableRelation> CreateCalls
    {
        get { lock (_gate) { return _createCalls.ToArray(); } }
    }

    public InMemoryTableRelationRepository Add(TableRelation relation)
    {
        ArgumentNullException.ThrowIfNull(relation);
        lock (_gate)
        {
            _relations.Add(relation);
        }

        return this;
    }

    public Task<IReadOnlyList<TableRelation>> GetRelationsForTableAsync(TableIdentifier parentTable, CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TableRelation>>(
                _relations.Where(r => r.ParentTableIdentifier == parentTable).ToList());
        }
    }

    public Task<IReadOnlyList<TableRelation>> GetAllRelationsAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<TableRelation>>(_relations.ToList());
        }
    }

    public Task<RelationWriteResult> CreateRelationAsync(TableRelation relation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(relation);
        OnCreateRelation?.Invoke(relation);
        lock (_gate)
        {
            _createCalls.Add(relation);
            var idx = _relations.FindIndex(r =>
                r.ParentTableIdentifier == relation.ParentTableIdentifier &&
                r.ChildTableIdentifier == relation.ChildTableIdentifier &&
                string.Equals(r.RelationName, relation.RelationName, StringComparison.OrdinalIgnoreCase));
            if (idx < 0)
            {
                _relations.Add(relation);
                return Task.FromResult(new RelationWriteResult(relation.Id, Created: true, KeysChanged: true));
            }

            var existing = _relations[idx];
            var keysChanged = !existing.JoinKeysParent.SequenceEqual(relation.JoinKeysParent, StringComparer.OrdinalIgnoreCase) ||
                              !existing.JoinKeysChild.SequenceEqual(relation.JoinKeysChild, StringComparer.OrdinalIgnoreCase);
            _relations[idx] = relation;
            return Task.FromResult(new RelationWriteResult(existing.Id, Created: false, KeysChanged: keysChanged));
        }
    }

    public Task<bool> SetRelationCrossDataSourceOptInAsync(Guid relationId, bool allowCrossDataSource, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var rel = _relations.FirstOrDefault(r => r.Id == relationId);
            if (rel is null)
            {
                return Task.FromResult(false);
            }

            rel.AllowCrossDataSource = allowCrossDataSource;
            return Task.FromResult(true);
        }
    }
}
