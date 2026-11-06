-- WP-SIMPL-12 (OD-32): remove access-profile assignments of kind BreakGlass.
DELETE FROM ACCESS_PROFILE_ASSIGNMENTS WHERE kind = 'BreakGlass';
