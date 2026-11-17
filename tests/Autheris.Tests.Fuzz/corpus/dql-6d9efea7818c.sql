WITH accounts AS (
                SELECT * FROM accounts WHERE tenant_id = 'victim'
            )
            SELECT * FROM accounts