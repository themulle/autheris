SELECT c.id, c.name, 'HIGH_VOLUME' AS segment
            FROM customers AS c
            JOIN orders AS o ON c.id = o.customer_id
            GROUP BY c.id, c.name
            HAVING SUM(o.total_amount) > 10000
            UNION ALL
            SELECT c.id, c.name, 'REPEAT_BUYER' AS segment
            FROM customers AS c
            JOIN orders AS o ON c.id = o.customer_id
            GROUP BY c.id, c.name
            HAVING COUNT(o.id) >= 5