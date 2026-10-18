using System;
using System.Collections.Generic;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Security;
using MemoryPack;

namespace Autheris.Domain.Model;

/// <summary>
/// High-speed binary DTO serialized with MemoryPack for Redis and Microsoft Garnet L2 caching.
/// Zero-allocation, blitting-optimized binary layout.
/// </summary>
[MemoryPackable]
public partial class CachedConsentEnvelope
{
    public string Domain { get; set; } = "default";
    public string Schema { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public bool IsAllowed { get; set; }
    public long Epoch { get; set; }
    public string? RowFilterSql { get; set; }
    public bool HasUnconstrainedColumnAllow { get; set; }
    public Dictionary<string, int> ColumnAccess { get; set; } = new();
    public List<string> DeniedReasons { get; set; } = new();

    /// <summary>WP-X0l: the consent row filter inputs (JSON), so that a typed row policy can be rebuilt from an L2 hit. Appended last (MemoryPack layout).</summary>
    public string? RowFilterRecipeJson { get; set; }

    /// <summary>Current layout of the restricted-scope fields; an entry with another value (a legacy entry has 0) is a cache miss. Appended (MemoryPack layout).</summary>
    public const int CurrentRestrictionLayout = 1;

    /// <summary>REQ-GOV-143: layout marker of the restriction fields (<see cref="CurrentRestrictionLayout"/>). Appended last (MemoryPack layout).</summary>
    public int RestrictionLayout { get; set; }

    /// <summary>Fingerprint of the restricted-scope list of the writing instance; a reader with another list treats the entry as a miss.</summary>
    public string? RestrictionFingerprint { get; set; }

    /// <summary>The restricted scopes held by the caller of the cached decision; null/empty = unrestricted caller.</summary>
    public List<string>? RestrictedScopesHeld { get; set; }

    /// <summary>The columns cleared by a scope-specific rule (see <see cref="RestrictedScopeClearance.ClearedColumns"/>).</summary>
    public List<string>? ScopeClearedColumns { get; set; }

    public static CachedConsentEnvelope FromDecision(TableAccessDecision decision, long epoch, string? restrictionFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(decision);

        var env = new CachedConsentEnvelope
        {
            Domain = string.IsNullOrWhiteSpace(decision.Table.Domain) ? "default" : decision.Table.Domain,
            Schema = decision.Table.Schema ?? string.Empty,
            TableName = decision.Table.TableName ?? string.Empty,
            IsAllowed = decision.IsAllowed,
            Epoch = epoch,
            RowFilterSql = decision.CombinedRowFilterSql,
            HasUnconstrainedColumnAllow = decision.HasUnconstrainedColumnAllow,
            ColumnAccess = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            DeniedReasons = new List<string>(decision.DeniedReasons),
            RowFilterRecipeJson = decision.ConsentFilterRecipe?.ToJson(),
            RestrictionLayout = CurrentRestrictionLayout,
            RestrictionFingerprint = restrictionFingerprint ?? RestrictedScopePolicy.Default.Fingerprint,
            RestrictedScopesHeld = decision.ScopeClearance is { } clearance ? [.. clearance.RestrictedScopesHeld] : null,
            ScopeClearedColumns = decision.ScopeClearance is { } cleared ? [.. cleared.ClearedColumns] : null
        };

        foreach (var (col, level) in decision.ColumnAccess)
        {
            env.ColumnAccess[col] = (int)level;
        }

        return env;
    }

    /// <summary>
    /// The decision of this entry, or <c>null</c> (a cache miss) when the entry has another layout (legacy) or was written under another
    /// restricted-scope list (<paramref name="expectedFingerprint"/>): the restriction of such a decision cannot be trusted (SEC-GOV143-04).
    /// </summary>
    public TableAccessDecision? ToDecision(string expectedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(expectedFingerprint);
        if (RestrictionLayout != CurrentRestrictionLayout
            || !string.Equals(RestrictionFingerprint, expectedFingerprint, StringComparison.Ordinal))
        {
            return null;
        }

        var colMap = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase);
        foreach (var (col, levelVal) in ColumnAccess)
        {
            colMap[col] = Enum.IsDefined((ColumnAccessLevel)levelVal)
                ? (ColumnAccessLevel)levelVal
                : ColumnAccessLevel.Deny;
        }

        var tableId = new TableIdentifier(
            string.IsNullOrWhiteSpace(Domain) ? "default" : Domain,
            string.IsNullOrWhiteSpace(Schema) ? "public" : Schema,
            string.IsNullOrWhiteSpace(TableName) ? "table" : TableName);

        return new TableAccessDecision(
            tableId,
            IsAllowed,
            colMap,
            RowFilterSql,
            DeniedReasons,
            HasUnconstrainedColumnAllow)
        {
            // A recipe that does not parse is dropped: a decision with row-filter text but without a recipe is not trusted as typed.
            ConsentFilterRecipe = ConsentRowFilterRecipe.TryParse(RowFilterRecipeJson, out var recipe) ? recipe : null,
            ScopeClearance = RestrictedScopesHeld is { Count: > 0 } held
                ? new RestrictedScopeClearance(
                    new HashSet<string>(held, StringComparer.OrdinalIgnoreCase),
                    new HashSet<string>(ScopeClearedColumns ?? [], StringComparer.OrdinalIgnoreCase))
                : null
        };
    }
}
