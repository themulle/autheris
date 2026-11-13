namespace Autheris.Extensions.OpenMetadata;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Catalog.OpenMetadata;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;

public sealed partial class OpenMetadataSyncService
{
    private (TableMetadata Metadata, int MaskingRulesCount) MapToTableMetadata(
        OpenMetadataTable omTable,
        TableIdentifier tableId,
        OpenMetadataOptions omOptions,
        bool isNewTable,
        string? existingSourceType = null,
        string? existingSourceName = null)
    {
        // D-1: the OpenMetadata service type (Mssql, Postgres, Snowflake, ...) becomes the dialect only if it is a supported
        // one and the table has no supported dialect yet; a sync never switches the dialect of an existing table.
        var sourceType = Autheris.Domain.Common.DatabaseDialectExtensions.ResolveCatalogSourceType(
            omTable.ServiceType ?? "PostgreSQL", existingSourceType, out var dialectSupported);
        if (!dialectSupported)
        {
            _logger.LogWarning(
                "OpenMetadata table {Table}: service type {ServiceType} is not a supported SQL dialect; the table cannot be queried until its source type is set.",
                tableId, omTable.ServiceType);
        }

        var catalogOptions = _options.Value.Catalog;
        var art9Tags = catalogOptions.GdprArticle9Tags;
        var piiTags = catalogOptions.PiiTags;

        var tableGuid = omTable.Id == Guid.Empty ? Guid.NewGuid() : omTable.Id;
        var columns = new List<TableColumn>();
        var maskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase);
        bool anyArt9Column = false;

        foreach (var omCol in omTable.Columns)
        {
            var colId = Guid.NewGuid();
            bool isSensitive = false;
            string? ruleType = null;

            foreach (var tag in omCol.Tags)
            {
                if (omOptions.TagToMaskingRuleMap.TryGetValue(tag.TagFQN, out var mappedRule))
                {
                    ruleType ??= mappedRule;
                    isSensitive = true;
                }

                // SEC E-06: Art. 9 and PII tags (Catalog.GdprArticle9Tags / Catalog.PiiTags, like the catalog sync) mark the
                // column as sensitive; without an explicit masking rule the column is redacted.
                var isArt9Tag = MatchesAnyTag(tag.TagFQN, art9Tags);
                anyArt9Column |= isArt9Tag;
                if (isArt9Tag ||
                    MatchesAnyTag(tag.TagFQN, piiTags) ||
                    tag.TagFQN.StartsWith("PII.", StringComparison.OrdinalIgnoreCase) ||
                    tag.TagFQN.StartsWith("PersonalData.", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(tag.TagFQN, "PII", StringComparison.OrdinalIgnoreCase))
                {
                    isSensitive = true;
                }
            }

            if (isSensitive)
            {
                ruleType ??= "REDACT";
                maskingRules[omCol.Name] = new MaskingRule
                {
                    Id = Guid.NewGuid(),
                    TableColumnId = colId,
                    RuleType = ruleType,
                    PatternOrFormat = string.Equals(ruleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase) ? "SHA256" : null,
                    Replacement = string.Equals(ruleType, "REDACT", StringComparison.OrdinalIgnoreCase) ? "[REDACTED]" : null,
                    HmacKeyId = string.Equals(ruleType, "HMAC_SHA256", StringComparison.OrdinalIgnoreCase) ? _options.Value.DataMasking.HmacKeyId : null
                };
            }

            columns.Add(new TableColumn
            {
                Id = colId,
                TableId = tableGuid,
                ColumnName = omCol.Name,
                DataType = omCol.DataType,
                IsSensitive = isSensitive
            });
        }

        // SEC E-06: Art. 9 on the table or on any column => HIGH + four-eyes (same rule as the catalog sync);
        // table-level PII / "Sensitive" tags => HIGH.
        var isArt9 = anyArt9Column || omTable.Tags.Any(t => MatchesAnyTag(t.TagFQN, art9Tags));
        var isHigh = isArt9 ||
                     omTable.Tags.Any(t => t.TagFQN.Contains("Sensitive", StringComparison.OrdinalIgnoreCase) || MatchesAnyTag(t.TagFQN, piiTags));

        var tableEntity = new Table
        {
            Id = tableGuid,
            SourceType = sourceType,
            SourceName = !string.IsNullOrWhiteSpace(existingSourceName) ? existingSourceName : tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            DisplayName = omTable.DisplayName ?? omTable.Name,
            Sensitivity = isHigh ? "HIGH" : "NORMAL",
            RequiresFourEyes = isArt9 || omTable.Tags.Any(t => t.TagFQN.Contains("FourEyes", StringComparison.OrdinalIgnoreCase)),
            // SEC E-09 / EX-06: new tables stay inactive until a gateway admin activates them (OpenMetadata.ActivateNewTables).
            // Existing tables keep their persisted state through the tighten-only ratchet.
            IsActive = !isNewTable || omOptions.ActivateNewTables
        };

        var metadata = new TableMetadata
        {
            Table = tableEntity,
            Identifier = tableId,
            Columns = columns,
            ColumnMaskingRules = maskingRules
        };

        // Issue #94 (D-8): PRIMARY_KEY and UNIQUE table constraints are the declared definition; replaced only when present and valid.
        if (omTable.TableConstraints is { Count: > 0 } constraints)
        {
            var collector = new Autheris.Application.Catalog.Services.CatalogKeyDefinitionImport.Collector();
            foreach (var constraint in constraints)
            {
                if (constraint is null)
                {
                    continue;
                }

                if (string.Equals(constraint.ConstraintType, "PRIMARY_KEY", StringComparison.OrdinalIgnoreCase))
                {
                    collector.AddPrimaryKey(constraint.Columns);
                }
                else if (string.Equals(constraint.ConstraintType, "UNIQUE", StringComparison.OrdinalIgnoreCase))
                {
                    collector.AddUnique(constraint.Columns);
                }
            }

            metadata = Autheris.Application.Catalog.Services.CatalogKeyDefinitionImport.Apply(metadata, collector, _logger, "OpenMetadata");
        }

        return (metadata, maskingRules.Count);
    }

    /// <summary>
    /// SEC E-06: Tag match against configured catalog tags: exact (case-insensitive, like the catalog sync) or
    /// hierarchical in OpenMetadata FQN form ("Classification.Tag" matches "Tag", "GDPR.Art9.Health" matches "GDPR.Art9").
    /// </summary>
    internal static bool MatchesAnyTag(string tagFqn, IEnumerable<string> configuredTags)
    {
        if (string.IsNullOrWhiteSpace(tagFqn))
        {
            return false;
        }

        foreach (var configured in configuredTags)
        {
            if (string.IsNullOrWhiteSpace(configured))
            {
                continue;
            }

            if (string.Equals(tagFqn, configured, StringComparison.OrdinalIgnoreCase) ||
                tagFqn.EndsWith("." + configured, StringComparison.OrdinalIgnoreCase) ||
                tagFqn.StartsWith(configured + ".", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// SEC E-07: Team → group SID resolution with separate indexes per key kind. Config keys are
    /// <c>id:</c>, <c>fqn:</c> or <c>name:</c> prefixed (validated at startup, see <see cref="TeamToGroupSidMapRules"/>).
    /// Ids are compared as GUIDs, FQNs and names ordinally. Keys that map to different SIDs are dropped (ambiguous).
    /// </summary>
    internal sealed class TeamSidIndex
    {
        private readonly SidKeyMap<Guid> _byId = new(EqualityComparer<Guid>.Default);
        private readonly SidKeyMap<string> _byFqn = new(StringComparer.Ordinal);
        private readonly SidKeyMap<string> _byName = new(StringComparer.Ordinal);

        public static TeamSidIndex Build(IReadOnlyDictionary<string, string> map, Microsoft.Extensions.Logging.ILogger logger)
        {
            // NL-SC-09: startup validation rejects an invalid map; a map that reaches here unvalidated is refused, never partially accepted.
            if (TeamToGroupSidMapRules.Validate(map, "Gateway:OpenMetadata:TeamToGroupSidMap").Count > 0)
            {
                throw new InvalidOperationException("Gateway:OpenMetadata:TeamToGroupSidMap is invalid; the sync is refused.");
            }

            var index = new TeamSidIndex();
            foreach (var (rawKey, sidValue) in map)
            {
                if (!TeamToGroupSidMapRules.TryParseKey(rawKey, out var kind, out var value, out _))
                {
                    throw new InvalidOperationException("Gateway:OpenMetadata:TeamToGroupSidMap is invalid; the sync is refused.");
                }

                var sid = new Sid(sidValue.Trim());
                switch (kind)
                {
                    case TeamMapKeyKind.Id: index._byId.Add(Guid.Parse(value), sid); break;
                    case TeamMapKeyKind.Fqn: index._byFqn.Add(value, sid); break;
                    default: index._byName.Add(value, sid); break;
                }
            }

            return index;
        }

        public bool TryResolve(OpenMetadataTeam team, out Sid sid)
        {
            if (team.Id != Guid.Empty && _byId.TryGet(team.Id, out sid))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(team.FullyQualifiedName) && _byFqn.TryGet(team.FullyQualifiedName, out sid))
            {
                return true;
            }

            if (!string.IsNullOrWhiteSpace(team.Name) && _byName.TryGet(team.Name, out sid))
            {
                return true;
            }

            sid = default;
            return false;
        }
    }

    /// <summary>
    /// SEC E-07 / EX-01: User → user SID resolution only by stable OM id (GUID) or by e-mail, never by user name.
    /// E-mail normalization (documented): trimmed, compared OrdinalIgnoreCase on BOTH sides (config key and OM value).
    /// Config keys may be prefixed (<c>id:</c>, <c>email:</c>); unprefixed GUID keys are ids, keys containing '@' are
    /// e-mails, other keys are ignored. An e-mail used by more than one OM user (case-insensitive) is ambiguous and never
    /// resolves; config keys that map to different SIDs are dropped.
    /// </summary>
    internal sealed class UserSidIndex
    {
        private readonly SidKeyMap<Guid> _byId = new(EqualityComparer<Guid>.Default);
        private readonly SidKeyMap<string> _byEmail = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ambiguousOmEmails = new(StringComparer.OrdinalIgnoreCase);

        public static UserSidIndex Build(
            IReadOnlyDictionary<string, string> map,
            IEnumerable<OpenMetadataUser> omUsers,
            Microsoft.Extensions.Logging.ILogger logger)
        {
            var index = new UserSidIndex();
            foreach (var (rawKey, sidValue) in map)
            {
                if (string.IsNullOrWhiteSpace(rawKey) || string.IsNullOrWhiteSpace(sidValue))
                {
                    continue;
                }

                var key = rawKey.Trim();
                var sid = new Sid(sidValue.Trim());
                if (TryStripPrefix(key, "id:", out var idPart))
                {
                    if (Guid.TryParse(idPart, out var id)) index._byId.Add(id, sid);
                    else logger.LogWarning("Ignoring UserToUserSidMap entry with invalid user id.");
                }
                else if (TryStripPrefix(key, "email:", out var emailPart))
                {
                    index._byEmail.Add(emailPart, sid);
                }
                else if (Guid.TryParse(key, out var legacyId))
                {
                    index._byId.Add(legacyId, sid);
                }
                else if (key.Contains('@', StringComparison.Ordinal))
                {
                    index._byEmail.Add(key, sid);
                }
                else
                {
                    logger.LogWarning("Ignoring UserToUserSidMap entry that is neither an OpenMetadata user id nor an e-mail address (user names are never used).");
                }
            }

            var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var user in omUsers)
            {
                var email = user.Email?.Trim();
                if (!string.IsNullOrEmpty(email) && !seenEmails.Add(email))
                {
                    index._ambiguousOmEmails.Add(email);
                }
            }

            if (index._ambiguousOmEmails.Count > 0)
            {
                logger.LogWarning("{Count} OpenMetadata e-mail address(es) are used by more than one user (case-insensitive); they are not mapped to SIDs.", index._ambiguousOmEmails.Count);
            }

            return index;
        }

        public bool TryResolve(OpenMetadataUser user, out Sid sid)
        {
            if (user.Id != Guid.Empty && _byId.TryGet(user.Id, out sid))
            {
                return true;
            }

            var email = user.Email?.Trim();
            if (!string.IsNullOrEmpty(email) && !_ambiguousOmEmails.Contains(email) && _byEmail.TryGet(email, out sid))
            {
                return true;
            }

            sid = default;
            return false;
        }
    }

    /// <summary>Key → SID map that drops keys configured with conflicting SIDs (fail-closed).</summary>
    internal sealed class SidKeyMap<TKey> where TKey : notnull
    {
        private readonly Dictionary<TKey, Sid> _map;
        private readonly HashSet<TKey> _ambiguous;

        public SidKeyMap(IEqualityComparer<TKey> comparer)
        {
            _map = new Dictionary<TKey, Sid>(comparer);
            _ambiguous = new HashSet<TKey>(comparer);
        }

        public void Add(TKey key, Sid sid)
        {
            if (_ambiguous.Contains(key))
            {
                return;
            }

            if (_map.TryGetValue(key, out var current))
            {
                if (!string.Equals(current.Value, sid.Value, StringComparison.Ordinal))
                {
                    _map.Remove(key);
                    _ambiguous.Add(key);
                }

                return;
            }

            _map[key] = sid;
        }

        public bool TryGet(TKey key, out Sid sid) => _map.TryGetValue(key, out sid);
    }

    private static bool TryStripPrefix(string key, string prefix, out string rest)
    {
        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && key.Length > prefix.Length)
        {
            rest = key[prefix.Length..].Trim();
            return rest.Length > 0;
        }

        rest = string.Empty;
        return false;
    }

    private static Consent CreateConsentForRole(string roleName, TableMetadata table, TenantId tenantId, ConsentEffect effect, DateTimeOffset validFrom, DateTimeOffset validTo)
    {
        return new Consent
        {
            Id = Guid.NewGuid(),
            ConsentRequestId = OpenMetadataSyncConsentMarker,
            TableId = table.Table.Id,
            TableIdentifier = table.Identifier,
            TenantId = tenantId,
            GranteeType = GranteeType.Role,
            RoleName = roleName,
            Effect = effect,
            ValidFrom = validFrom,
            ValidTo = validTo
        };
    }
}
