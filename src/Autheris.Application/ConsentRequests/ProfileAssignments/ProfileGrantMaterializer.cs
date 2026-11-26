namespace Autheris.Application.ConsentRequests.ProfileAssignments;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Grants;
using Autheris.Domain.Model;

/// <summary>
/// Materializes approved access-profile items into individual signed grants (<see cref="ConsentGrantSource.AccessProfile"/>),
/// extends pattern approving-owner sets, and revokes all grants of an assignment atomically.
/// </summary>
public sealed class ProfileGrantMaterializer(
    IGrantWriter grantWriter,
    IProfileAssignmentStore store,
    TimeProvider? timeProvider = null) : IProfileGrantMaterializer
{
    private readonly IGrantWriter _grantWriter = grantWriter ?? throw new ArgumentNullException(nameof(grantWriter));
    private readonly IProfileAssignmentStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<IReadOnlyList<Consent>> MaterializeApprovedAsync(
        DbTransaction transaction,
        ConsentRequest req,
        ProfileAssignmentRequestContext context,
        IReadOnlyList<ProfileAssignmentItemRow> itemRows,
        IReadOnlyList<TableMetadata> tenantTables,
        Sid actorSid,
        IReadOnlyList<Sid> externalApproverSids,
        DateTimeOffset approvedValidTo,
        DateTimeOffset decidedAt,
        string? description,
        IReadOnlyList<WriteRule>? narrowedWriteRules,
        IReadOnlyList<int>? approvedItemNos,
        IReadOnlyList<string>? approvedClearColumns,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(req);
        ArgumentNullException.ThrowIfNull(context);
        StoredTenant.RequireWritable(req.TenantId);

        var itemsToMaterialize = ResolveApprovedItems(req, context, itemRows, externalApproverSids, approvedItemNos);
        if (itemsToMaterialize.Count == 0)
        {
            return Array.Empty<Consent>();
        }

        var clearFilter = BuildApprovedClearColumnFilter(context.MaskingMode, approvedClearColumns, tenantTables, context.Items);
        var effectiveValidFrom = req.RequestedValidFrom is { } requestedFrom && requestedFrom > decidedAt
            ? requestedFrom
            : decidedAt;
        var materialized = new List<Consent>(itemsToMaterialize.Count);

        foreach (var (item, approvingOwners) in itemsToMaterialize)
        {
            var matchedForItem = MatchTablesForItem(item, tenantTables);
            var representativeMeta = !item.IsPattern ? matchedForItem.FirstOrDefault() : null;
            if (!item.IsPattern && representativeMeta is null)
            {
                throw new ConsentDecisionException("BAD_REQUEST", $"Catalog metadata for '{item.Target}' was not found.", statusCode: 400);
            }

            var writeRules = ResolveItemWriteRules(item, narrowedWriteRules, representativeMeta);
            var columnRules = BuildColumnRules(item, context.MaskingMode, matchedForItem, clearFilter);
            var consentId = Guid.NewGuid();
            foreach (var r in columnRules)
            {
                // Preserve clean per-consent rule linkage.
            }

            columnRules = columnRules
                .Select(r => new ConsentColumnRule
                {
                    Id = r.Id == Guid.Empty ? Guid.NewGuid() : r.Id,
                    ConsentId = consentId,
                    TableColumnId = r.TableColumnId,
                    ColumnName = r.ColumnName,
                    AccessLevel = r.AccessLevel,
                    ApplicableScopes = r.ApplicableScopes
                })
                .ToList();

            var dataSource = item.IsPattern
                ? (string.IsNullOrWhiteSpace(item.DataSource) ? ObjectPatternSpec.Parse(item.Target).DataSource : item.DataSource)
                : representativeMeta!.Identifier.Domain;

            var consent = new Consent
            {
                Id = consentId,
                TableId = item.IsPattern ? Guid.Empty : representativeMeta!.Table.Id,
                TableIdentifier = item.IsPattern ? default : representativeMeta!.Identifier,
                ObjectPattern = item.IsPattern ? ObjectPatternSpec.Parse(item.Target).NormalizedPattern : null,
                DataSource = dataSource,
                ColumnSetMode = item.IsPattern ? ColumnSetMode.AllNonHidden : ColumnSetMode.Explicit,
                WorkflowMode = req.WorkflowMode,
                ProfileItemNo = item.ItemNo,
                ConsentRequestId = req.Id,
                Effect = ConsentEffect.Allow,
                TenantId = req.TenantId,
                GranteeType = req.RequestedGranteeType,
                GranteeSid = new Sid(req.RequestedGranteeRef),
                CreatedBySid = actorSid,
                ValidFrom = effectiveValidFrom,
                ValidTo = approvedValidTo,
                Description = description,
                GrantSource = ConsentGrantSource.AccessProfile,
                ProfileAssignmentId = req.Id,
                ColumnRules = columnRules,
                RowScope = context.RowScope ?? [],
                WriteRules = writeRules,
                ApprovingOwners = approvingOwners
            };
            consent.Validate();

            var objectId = item.IsPattern ? consent.ObjectPattern! : consent.TableIdentifier.ToString();
            var scopeJson = JsonSerializer.Serialize(new
            {
                profileId = context.ProfileId,
                itemNo = item.ItemNo,
                target = objectId,
                isPattern = item.IsPattern,
                maskingMode = context.MaskingMode.ToString(),
                columnSetMode = consent.ColumnSetMode.ToSqlString(),
                writeRulesCount = consent.WriteRules.Count
            });
            var decisionJson = JsonSerializer.Serialize(new
            {
                Action = "ProfileAssignmentApproved",
                RequestId = req.Id,
                WorkflowMode = req.WorkflowMode.ToSqlString(),
                ApprovingOwners = approvingOwners,
                DecidedBy = actorSid.Value
            });

            var input = new GrantRecordInput(
                req.TenantId.Value,
                scopeJson,
                req.RequesterSid.Value,
                decisionJson,
                decidedAt,
                ObjectId: objectId,
                GranteeType: consent.GranteeType.ToString(),
                GranteeRef: req.RequestedGranteeRef,
                RequestId: req.Id,
                GrantSource: ConsentGrantSource.AccessProfile,
                ProfileAssignmentId: req.Id,
                ValidFrom: effectiveValidFrom,
                ValidUntil: approvedValidTo,
                RecordType: GrantRecordType.Issued);

            var record = await _grantWriter.InsertAndGrantAsync(transaction, consent, input, ct).ConfigureAwait(false);

            // A pattern consent has no table: the store advances the catalog version instead of a table epoch (issue #1044).
            await _store.IncrementTableEpochInTransactionAsync(transaction, consent.TableIdentifier, ct).ConfigureAwait(false);

            await _store.RecordAuditInTransactionAsync(transaction, new AuditLogEntry
            {
                TenantId = req.TenantId,
                OccurredAt = decidedAt,
                EventType = AuditEventTypes.ConsentGranted,
                ActorSid = actorSid,
                TargetTable = objectId,
                Decision = "ALLOW",
                TraceId = Guid.NewGuid().ToString("N"),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    ConsentId = consent.Id,
                    ProfileAssignmentId = req.Id,
                    ProfileId = context.ProfileId,
                    ItemNo = item.ItemNo,
                    GrantRecordSeq = record.Seq,
                    GrantSource = ConsentGrantSource.AccessProfile.ToSqlString(),
                    consent.ValidFrom,
                    consent.ValidTo
                })
            }, ct).ConfigureAwait(false);

            materialized.Add(new Consent
            {
                Id = consent.Id,
                TableId = consent.TableId,
                TableIdentifier = consent.TableIdentifier,
                ObjectPattern = consent.ObjectPattern,
                DataSource = consent.DataSource,
                ColumnSetMode = consent.ColumnSetMode,
                WorkflowMode = consent.WorkflowMode,
                ProfileItemNo = consent.ProfileItemNo,
                ConsentRequestId = consent.ConsentRequestId,
                Effect = consent.Effect,
                TenantId = consent.TenantId,
                GranteeType = consent.GranteeType,
                GranteeSid = consent.GranteeSid,
                CreatedBySid = consent.CreatedBySid,
                ValidFrom = consent.ValidFrom,
                ValidTo = consent.ValidTo,
                Description = consent.Description,
                GrantSource = consent.GrantSource,
                GrantRecordSeq = record.Seq,
                ProfileAssignmentId = consent.ProfileAssignmentId,
                ColumnRules = consent.ColumnRules,
                RowScope = consent.RowScope,
                WriteRules = consent.WriteRules,
                ApprovingOwners = consent.ApprovingOwners
            });
        }

        return materialized;
    }

    public async Task<Consent> ExtendApprovingOwnersAsync(
        DbTransaction transaction,
        Consent existingPatternConsent,
        Guid requestId,
        IReadOnlyCollection<string> newApprovingOwners,
        Sid decidedBy,
        DateTimeOffset decidedAt,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(existingPatternConsent);
        StoredTenant.RequireWritable(existingPatternConsent.TenantId);

        var distinctNew = (newApprovingOwners ?? [])
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Where(s => !existingPatternConsent.ApprovingOwners.Contains(s, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctNew.Count > 0)
        {
            await _store.AddApprovingOwnersInTransactionAsync(transaction, existingPatternConsent, distinctNew, ct).ConfigureAwait(false);
        }

        var mergedOwners = existingPatternConsent.ApprovingOwners
            .Concat(distinctNew)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var updated = new Consent
        {
            Id = existingPatternConsent.Id,
            TableId = existingPatternConsent.TableId,
            TableIdentifier = existingPatternConsent.TableIdentifier,
            ObjectPattern = existingPatternConsent.ObjectPattern,
            DataSource = existingPatternConsent.DataSource,
            ColumnSetMode = existingPatternConsent.ColumnSetMode,
            WorkflowMode = existingPatternConsent.WorkflowMode,
            ProfileItemNo = existingPatternConsent.ProfileItemNo,
            ConsentRequestId = existingPatternConsent.ConsentRequestId,
            Effect = existingPatternConsent.Effect,
            TenantId = existingPatternConsent.TenantId,
            GranteeType = existingPatternConsent.GranteeType,
            GranteeSid = existingPatternConsent.GranteeSid,
            CreatedBySid = decidedBy,
            ValidFrom = existingPatternConsent.ValidFrom,
            ValidTo = existingPatternConsent.ValidTo,
            Description = existingPatternConsent.Description,
            GrantSource = existingPatternConsent.GrantSource,
            ProfileAssignmentId = existingPatternConsent.ProfileAssignmentId,
            ColumnRules = existingPatternConsent.ColumnRules,
            RowFilters = existingPatternConsent.RowFilters,
            RowScope = existingPatternConsent.RowScope,
            WriteRules = existingPatternConsent.WriteRules,
            ApprovingOwners = mergedOwners
        };

        var objectId = existingPatternConsent.ObjectPattern ?? existingPatternConsent.TableIdentifier.ToString();
        var input = new GrantRecordInput(
            existingPatternConsent.TenantId.Value,
            JsonSerializer.Serialize(new
            {
                profileAssignmentId = existingPatternConsent.ProfileAssignmentId,
                itemNo = existingPatternConsent.ProfileItemNo,
                target = objectId,
                extendedOwners = distinctNew
            }),
            existingPatternConsent.GranteeSid?.Value ?? decidedBy.Value,
            JsonSerializer.Serialize(new
            {
                Action = "PatternApprovingOwnerExtended",
                RequestId = requestId,
                DecidedBy = decidedBy.Value,
                AddedApprovingOwners = distinctNew
            }),
            decidedAt,
            ObjectId: objectId,
            GranteeType: existingPatternConsent.GranteeType.ToString(),
            GranteeRef: existingPatternConsent.GranteeSid?.Value ?? decidedBy.Value,
            RequestId: requestId,
            GrantSource: ConsentGrantSource.AccessProfile,
            ProfileAssignmentId: existingPatternConsent.ProfileAssignmentId,
            ValidFrom: existingPatternConsent.ValidFrom,
            ValidUntil: existingPatternConsent.ValidTo,
            RecordType: GrantRecordType.Issued);

        var record = await _grantWriter.GrantAsync(transaction, updated, input, ct).ConfigureAwait(false);
        await _store.RecordAuditInTransactionAsync(transaction, new AuditLogEntry
        {
            TenantId = existingPatternConsent.TenantId,
            OccurredAt = decidedAt,
            EventType = AuditEventTypes.ConsentGranted,
            ActorSid = decidedBy,
            TargetTable = objectId,
            Decision = "ALLOW",
            TraceId = Guid.NewGuid().ToString("N"),
            DetailsJson = JsonSerializer.Serialize(new
            {
                ConsentId = existingPatternConsent.Id,
                ProfileAssignmentId = existingPatternConsent.ProfileAssignmentId,
                RequestId = requestId,
                GrantRecordSeq = record.Seq,
                AddedApprovingOwners = distinctNew
            })
        }, ct).ConfigureAwait(false);

        return updated;
    }

    public async Task<int> RevokeAssignmentAsync(
        TenantId tenantId,
        Guid profileAssignmentId,
        Sid actorSid,
        string reason,
        CancellationToken ct = default)
    {
        StoredTenant.RequireWritable(tenantId);

        var now = _time.GetUtcNow();
        var normalizedReason = string.IsNullOrWhiteSpace(reason) ? "PROFILE_ASSIGNMENT_REVOKED" : reason.Trim();
        var invalidatedTables = new HashSet<TableIdentifier>();
        var revokedCount = 0;
        var revokedGrants = new List<RevokedProfileGrant>();

        await using var tx = await _store.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var activeConsents = await _store.GetConsentsByProfileAssignmentAsync(tx.Transaction, tenantId, profileAssignmentId, ct)
                .ConfigureAwait(false);
            foreach (var consent in activeConsents)
            {
                if (consent.IsRevoked || consent.TenantId != tenantId)
                {
                    continue;
                }

                // A consent revoked concurrently no longer matches the conditional update: no ledger, epoch, audit or event for it.
                if (!await _store.MarkConsentRevokedInTransactionAsync(tx.Transaction, tenantId, consent.Id, actorSid, normalizedReason, now, ct)
                        .ConfigureAwait(false))
                {
                    continue;
                }

                var objectId = consent.IsPatternGrant ? consent.ObjectPattern! : consent.TableIdentifier.ToString();
                var revokeInput = new GrantRecordInput(
                    tenantId.Value,
                    "{}",
                    actorSid.Value,
                    JsonSerializer.Serialize(new { Action = "ProfileAssignmentRevoke", ProfileAssignmentId = profileAssignmentId, Reason = normalizedReason }),
                    now,
                    ObjectId: objectId,
                    GranteeType: consent.GranteeType.ToString(),
                    GranteeRef: consent.GranteeSid?.Value ?? consent.Id.ToString("D"),
                    RequestId: consent.ConsentRequestId,
                    GrantSource: consent.GrantSource,
                    ProfileAssignmentId: profileAssignmentId,
                    ValidFrom: consent.ValidFrom,
                    ValidUntil: consent.ValidTo,
                    RecordType: GrantRecordType.Revoked);

                await _grantWriter.RevokeAsync(tx.Transaction, consent, revokeInput, ct).ConfigureAwait(false);
                // Pattern consents included: the store advances the catalog version for them (issue #1044).
                await _store.IncrementTableEpochInTransactionAsync(tx.Transaction, consent.TableIdentifier, ct).ConfigureAwait(false);
                invalidatedTables.Add(consent.TableIdentifier);

                await _store.RecordAuditInTransactionAsync(tx.Transaction, new AuditLogEntry
                {
                    TenantId = tenantId,
                    OccurredAt = now,
                    EventType = AuditEventTypes.ConsentRevoked,
                    ActorSid = actorSid,
                    TargetTable = objectId,
                    Decision = "REVOKED",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = JsonSerializer.Serialize(new
                    {
                        ConsentId = consent.Id,
                        ProfileAssignmentId = profileAssignmentId,
                        Reason = normalizedReason,
                        RevokedAt = now
                    })
                }, ct).ConfigureAwait(false);

                revokedGrants.Add(new RevokedProfileGrant(consent.Id, consent.TableIdentifier));
                revokedCount++;
            }

            // The event lock is the last lock of the transaction; a failed append rolls everything back.
            if (revokedGrants.Count > 0)
            {
                await _store.AppendGrantRevokedEventsInTransactionAsync(tx.Transaction, tenantId, revokedGrants, now, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }

        if (invalidatedTables.Count > 0)
        {
            await _store.InvalidateTablesAfterCommitAsync(invalidatedTables.ToList(), ct).ConfigureAwait(false);
        }

        return revokedCount;
    }

    private static List<(ProfileAssignmentSnapshotItem Item, IReadOnlyList<string> ApprovingOwners)> ResolveApprovedItems(
        ConsentRequest req,
        ProfileAssignmentRequestContext context,
        IReadOnlyList<ProfileAssignmentItemRow> itemRows,
        IReadOnlyList<Sid> externalApproverSids,
        IReadOnlyList<int>? approvedItemNos)
    {
        var results = new List<(ProfileAssignmentSnapshotItem, IReadOnlyList<string>)>();
        if (req.WorkflowMode == ConsentWorkflowMode.External)
        {
            HashSet<int>? allowedSet = null;
            if (approvedItemNos != null)
            {
                ValidateApprovedItemNos(context, approvedItemNos);
                allowedSet = approvedItemNos.ToHashSet();
            }

            var owners = externalApproverSids.Select(s => s.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var item in context.Items)
            {
                if (allowedSet == null || allowedSet.Contains(item.ItemNo))
                {
                    results.Add((item, owners));
                }
            }

            return results;
        }

        var rowsByItem = itemRows.GroupBy(r => r.ItemNo).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var item in context.Items)
        {
            if (!rowsByItem.TryGetValue(item.ItemNo, out var rows) || rows.Count == 0)
            {
                continue;
            }

            var approvedOwners = rows
                .Where(r => string.Equals(r.OwnerDecision, "APPROVED", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.OwnerSid)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (approvedOwners.Count == 0)
            {
                continue;
            }

            results.Add((item, approvedOwners));
        }

        return results;
    }

    internal static void ValidateExternalNarrowing(
        ProfileAssignmentRequestContext context,
        IReadOnlyList<TableMetadata> tenantTables,
        IReadOnlyList<int>? approvedItemNos,
        IReadOnlyList<string>? approvedClearColumns)
    {
        if (approvedItemNos != null) ValidateApprovedItemNos(context, approvedItemNos);
        BuildApprovedClearColumnFilter(context.MaskingMode, approvedClearColumns, tenantTables, context.Items);
    }

    private static void ValidateApprovedItemNos(ProfileAssignmentRequestContext context, IReadOnlyList<int> approvedItemNos)
    {
        if (approvedItemNos.Count == 0)
        {
            throw new ConsentDecisionException("VALIDATION_ERROR", "At least one profile item must be approved.", statusCode: 400);
        }

        var validSet = context.Items.Select(i => i.ItemNo).ToHashSet();
        foreach (var no in approvedItemNos)
        {
            if (!validSet.Contains(no))
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.DecisionExceedsRequest,
                    $"Approved item index {no} is outside the requested profile items.",
                    statusCode: 422);
            }
        }
    }

    private static HashSet<string>? BuildApprovedClearColumnFilter(
        MaskingPolicyMode maskingMode,
        IReadOnlyList<string>? approvedClearColumns,
        IReadOnlyList<TableMetadata> tenantTables,
        IReadOnlyList<ProfileAssignmentSnapshotItem> items)
    {
        if (approvedClearColumns == null)
        {
            return null;
        }

        var filter = new HashSet<string>(approvedClearColumns, StringComparer.OrdinalIgnoreCase);
        if (maskingMode != MaskingPolicyMode.Unmasked)
        {
            var matched = items.SelectMany(i => MatchTablesForItem(i, tenantTables)).ToList();
            var forbidden = filter.FirstOrDefault(c =>
                matched.Any(t => t.Columns.Any(mc =>
                    string.Equals(mc.ColumnName, c, StringComparison.OrdinalIgnoreCase) &&
                    ClassificationMetadata.IsColumnSensitive(t, mc))));
            if (forbidden is not null)
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.DecisionExceedsRequest,
                    $"Sensitive column '{forbidden}' cannot be cleared under masking mode '{maskingMode}'.",
                    statusCode: 422);
            }
        }

        return filter;
    }

    private static IReadOnlyList<WriteRule> ResolveItemWriteRules(
        ProfileAssignmentSnapshotItem item,
        IReadOnlyList<WriteRule>? narrowedWriteRules,
        TableMetadata? meta)
    {
        if (narrowedWriteRules == null)
        {
            return item.WriteRules ?? [];
        }

        if (item.WriteRules.Count == 0)
        {
            if (narrowedWriteRules.Count > 0)
            {
                throw new ConsentDecisionException(
                    ConsentDecisionErrorCodes.DecisionExceedsRequest,
                    "Decision write rules exceed the requested profile item write rules.",
                    statusCode: 422);
            }

            return [];
        }

        try
        {
            return WriteRuleWorkflowHelper.NarrowWriteRules(
                item.WriteRules,
                narrowedWriteRules,
                isWriteOnlyRequest: false,
                meta,
                meta?.Identifier ?? default);
        }
        catch (ConsentRequestException ex)
        {
            throw new ConsentDecisionException(ConsentDecisionErrorCodes.DecisionExceedsRequest, ex.Message, statusCode: 422);
        }
    }

    private static List<ConsentColumnRule> BuildColumnRules(
        ProfileAssignmentSnapshotItem item,
        MaskingPolicyMode maskingMode,
        IReadOnlyList<TableMetadata> matchedTables,
        HashSet<string>? clearFilter)
    {
        var rules = new List<ConsentColumnRule>();
        if (item.IsPattern)
        {
            // Pattern items use ColumnSetMode.AllNonHidden for non-sensitive columns; explicit rules are added only
            // for currently known sensitive/PII columns when Unmasked (and allowed by clearFilter), so newly added PII stays masked.
            if (maskingMode != MaskingPolicyMode.Unmasked)
            {
                return rules;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var col in matchedTables.SelectMany(t => t.Columns.Select(c => (Table: t, Col: c))))
            {
                if (col.Col.DiscoveryHidden || !ClassificationMetadata.IsColumnSensitive(col.Table, col.Col) || !seen.Add(col.Col.ColumnName))
                {
                    continue;
                }

                if (clearFilter != null && !clearFilter.Contains(col.Col.ColumnName))
                {
                    continue;
                }

                rules.Add(new ConsentColumnRule
                {
                    Id = Guid.NewGuid(),
                    TableColumnId = col.Col.Id,
                    ColumnName = col.Col.ColumnName,
                    AccessLevel = ColumnAccessLevel.Clear
                });
            }

            return rules;
        }

        var meta = matchedTables[0];
        foreach (var col in meta.Columns)
        {
            if (col.DiscoveryHidden)
            {
                continue;
            }

            bool classified = ClassificationMetadata.IsColumnSensitive(meta, col);
            ColumnAccessLevel level;
            if (maskingMode == MaskingPolicyMode.Unmasked)
            {
                level = clearFilter == null || clearFilter.Contains(col.ColumnName)
                    ? ColumnAccessLevel.Clear
                    : ColumnAccessLevel.Mask;
            }
            else if (classified)
            {
                level = ColumnAccessLevel.Mask;
            }
            else
            {
                level = clearFilter == null || clearFilter.Contains(col.ColumnName)
                    ? ColumnAccessLevel.Clear
                    : ColumnAccessLevel.Mask;
            }

            rules.Add(new ConsentColumnRule
            {
                Id = Guid.NewGuid(),
                TableColumnId = col.Id,
                ColumnName = col.ColumnName,
                AccessLevel = level
            });
        }

        return rules;
    }

    internal static IReadOnlyList<TableMetadata> MatchTablesForItem(
        ProfileAssignmentSnapshotItem item,
        IReadOnlyList<TableMetadata> tenantTables)
    {
        if (item.IsPattern && ObjectPatternSpec.TryParse(item.Target, out var spec, out _))
        {
            return tenantTables.Where(t => spec!.Matches(t.Identifier)).ToList();
        }

        if (TableIdentifier.TryParse(item.Target, out var exact))
        {
            return tenantTables.Where(t => t.Identifier == exact).ToList();
        }

        return tenantTables.Where(t => AccessProfile.MatchesPattern(item.Target, t.Identifier)).ToList();
    }
}
