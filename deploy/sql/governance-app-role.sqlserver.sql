-- Provision least-privilege grants for the runtime application database user on SQL Server.
-- Run as the migration owner / db_owner after governance schema migrations complete.
--
-- Usage (sqlcmd):
--   sqlcmd -S <server> -d <database> -U <migration_owner> -v AppUser=autheris_app -i deploy/sql/governance-app-role.sqlserver.sql
--
-- Create the login and database user beforehand without sysadmin, db_owner, or db_ddladmin roles.
SET NOCOUNT ON;
GO

DECLARE @appUser sysname = N'$(AppUser)';
IF @appUser IS NULL OR LTRIM(RTRIM(@appUser)) = N'' OR @appUser = N'$' + N'(AppUser)'
BEGIN
    RAISERROR(N'Missing sqlcmd variable AppUser. Pass -v AppUser=<database_user_name>.', 16, 1);
    RETURN;
END;

DECLARE @appPrincipalId int = DATABASE_PRINCIPAL_ID(@appUser);
IF @appPrincipalId IS NULL
BEGIN
    RAISERROR(N'Database user "%s" does not exist in the current database. Create the database user first.', 16, 1, @appUser);
    RETURN;
END;

IF @appPrincipalId <= 4
BEGIN
    RAISERROR(N'Built-in database principal "%s" (principal_id <= 4) cannot be used as the least-privilege runtime application user.', 16, 1, @appUser);
    RETURN;
END;

IF OBJECT_ID(N'dbo.AUDIT_LOG_ENTRIES', N'U') IS NULL
BEGIN
    RAISERROR(N'Table dbo.AUDIT_LOG_ENTRIES does not exist. Run governance schema migrations before provisioning the runtime application user.', 16, 1);
    RETURN;
END;

DECLARE @quotedUser nvarchar(258) = QUOTENAME(@appUser);
DECLARE @quotedTable nvarchar(270);
DECLARE @roleName sysname;
DECLARE @sql nvarchar(max);

-- Drop any direct membership in privileged fixed database roles or broad writer roles.
DECLARE role_cur CURSOR LOCAL FAST_FORWARD FOR
    SELECT r.name
    FROM sys.database_role_members drm
    INNER JOIN sys.database_principals r ON r.principal_id = drm.role_principal_id
    WHERE drm.member_principal_id = @appPrincipalId
      AND r.name IN (N'db_owner', N'db_ddladmin', N'db_securityadmin', N'db_accessadmin', N'db_datawriter');

OPEN role_cur;
FETCH NEXT FROM role_cur INTO @roleName;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @sql = N'ALTER ROLE ' + QUOTENAME(@roleName) + N' DROP MEMBER ' + @quotedUser + N';';
    EXEC sp_executesql @sql;
    FETCH NEXT FROM role_cur INTO @roleName;
END;

CLOSE role_cur;
DEALLOCATE role_cur;

-- Fail closed if the user still inherits a privileged role indirectly via a nested role.
IF IS_ROLEMEMBER(N'db_owner', @appUser) = 1
   OR IS_ROLEMEMBER(N'db_ddladmin', @appUser) = 1
   OR IS_ROLEMEMBER(N'db_securityadmin', @appUser) = 1
   OR IS_ROLEMEMBER(N'db_accessadmin', @appUser) = 1
   OR IS_ROLEMEMBER(N'db_datawriter', @appUser) = 1
BEGIN
    RAISERROR(N'Database user "%s" indirectly inherits a privileged database role and cannot be used as the least-privilege runtime application user.', 16, 1, @appUser);
    RETURN;
END;

-- Ensure no database-level or dbo schema-level DDL or CONTROL grants exist.
SET @sql = N'REVOKE CREATE TABLE, CREATE VIEW, CREATE PROCEDURE, CREATE FUNCTION, ALTER, CONTROL FROM ' + @quotedUser + N';'
         + N'REVOKE ALTER, CONTROL, TAKE OWNERSHIP ON SCHEMA::dbo FROM ' + @quotedUser + N';';
EXEC sp_executesql @sql;

DECLARE @t sysname;
DECLARE c CURSOR LOCAL FAST_FORWARD FOR
    SELECT name
    FROM sys.tables
    WHERE schema_id = SCHEMA_ID(N'dbo');

OPEN c;
FETCH NEXT FROM c INTO @t;

WHILE @@FETCH_STATUS = 0
BEGIN
    SET @quotedTable = N'dbo.' + QUOTENAME(@t);

    IF UPPER(@t) = N'AUDIT_LOG_ENTRIES'
    BEGIN
        -- Append-only audit table: SELECT and INSERT only; revoke CONTROL and deny UPDATE, DELETE, ALTER, TAKE OWNERSHIP, REFERENCES.
        SET @sql = N'REVOKE SELECT, INSERT, UPDATE, DELETE, ALTER, CONTROL, TAKE OWNERSHIP, REFERENCES ON ' + @quotedTable + N' FROM ' + @quotedUser + N';'
                 + N'DENY UPDATE, DELETE, ALTER, TAKE OWNERSHIP, REFERENCES ON ' + @quotedTable + N' TO ' + @quotedUser + N';'
                 + N'GRANT SELECT, INSERT ON ' + @quotedTable + N' TO ' + @quotedUser + N';';
    END
    ELSE IF UPPER(@t) = N'AUDIT_LOG_TRUNCATE_GUARD'
    BEGIN
        -- Metadata visibility only for Mode=Verify checks; revoke CONTROL and deny data/DDL access.
        SET @sql = N'REVOKE SELECT, INSERT, UPDATE, DELETE, ALTER, CONTROL, TAKE OWNERSHIP, REFERENCES, VIEW DEFINITION ON ' + @quotedTable + N' FROM ' + @quotedUser + N';'
                 + N'DENY SELECT, INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP, REFERENCES ON ' + @quotedTable + N' TO ' + @quotedUser + N';'
                 + N'GRANT VIEW DEFINITION ON ' + @quotedTable + N' TO ' + @quotedUser + N';';
    END
    ELSE IF LOWER(@t) = N'autheris_schema_migrations'
    BEGIN
        -- Schema history: read-only for Mode=Verify startup checks; revoke CONTROL and deny writes/DDL.
        SET @sql = N'REVOKE SELECT, INSERT, UPDATE, DELETE, ALTER, CONTROL, TAKE OWNERSHIP, REFERENCES ON ' + @quotedTable + N' FROM ' + @quotedUser + N';'
                 + N'DENY INSERT, UPDATE, DELETE, ALTER, TAKE OWNERSHIP, REFERENCES ON ' + @quotedTable + N' TO ' + @quotedUser + N';'
                 + N'GRANT SELECT ON ' + @quotedTable + N' TO ' + @quotedUser + N';';
    END
    ELSE
    BEGIN
        -- Governance catalog, policy, consent, usage, governance_domains, governance_domain_tables,
        -- object_protection, access_policy_settings, governance_epochs, and remaining runtime tables.
        SET @sql = N'REVOKE ALTER, CONTROL, TAKE OWNERSHIP, REFERENCES ON ' + @quotedTable + N' FROM ' + @quotedUser + N';'
                 + N'GRANT SELECT, INSERT, UPDATE, DELETE ON ' + @quotedTable + N' TO ' + @quotedUser + N';';
    END;

    EXEC sp_executesql @sql;
    FETCH NEXT FROM c INTO @t;
END;

CLOSE c;
DEALLOCATE c;
GO
