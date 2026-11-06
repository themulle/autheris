-- WP-SIMPL-12 (OD-32): remove access-profile assignments of kind BreakGlass.
DELETE FROM dbo.ACCESS_PROFILE_ASSIGNMENTS WHERE kind = N'BreakGlass';
