-- WP-SIMPL-20e (WP-SIMPL-12): drop orphaned ITSM outbox table of deleted feature.
IF OBJECT_ID(N'dbo.ITSM_OUTBOX', N'U') IS NOT NULL DROP TABLE dbo.ITSM_OUTBOX;
