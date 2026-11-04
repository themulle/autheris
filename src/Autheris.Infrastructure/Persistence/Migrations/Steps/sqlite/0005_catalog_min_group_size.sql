-- guard: SELECT CASE WHEN (SELECT COUNT(*) FROM pragma_table_info('TABLES') WHERE name IN ('min_group_size', 'non_personal')) = 2 AND (SELECT COUNT(*) FROM pragma_table_info('TABLE_CATALOG_SETTINGS') WHERE name IN ('min_group_size', 'non_personal')) = 2 THEN 1 ELSE 0 END
-- Issue #448: Catalog-owned minimum group size and non-personal table flag. Additive.
ALTER TABLE TABLES ADD COLUMN min_group_size INTEGER NULL;
ALTER TABLE TABLES ADD COLUMN non_personal INTEGER NOT NULL DEFAULT 0 CHECK (non_personal IN (0, 1));
ALTER TABLE TABLE_CATALOG_SETTINGS ADD COLUMN min_group_size INTEGER NULL;
ALTER TABLE TABLE_CATALOG_SETTINGS ADD COLUMN non_personal INTEGER NULL CHECK (non_personal IS NULL OR non_personal IN (0, 1));
