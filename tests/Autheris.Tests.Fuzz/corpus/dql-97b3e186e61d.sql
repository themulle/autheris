SELECT c.id, c.company_name, SUM(o.amount) AS total
            FROM customers c
            JOIN orders o ON c.id = o.customer_id
            WHERE c.country = @country
              AND o.amount >= @minAmount
            GROUP BY c.id, c.company_name;