SELECT o.id, o.total_amount,
                   CASE 
                       WHEN o.total_amount >= 1000 THEN 'PLATINUM'
                       WHEN EXISTS (
                           SELECT 1 
                           FROM finance_items AS f 
                           WHERE f.parent_id = o.id AND f.price > 250
                       ) THEN 'GOLD'
                       ELSE 'STANDARD'
                   END AS priority_tier
            FROM orders AS o
            JOIN customers AS c ON o.customer_id = c.id
            WHERE o.status = 'COMPLETED'
              AND NOT EXISTS (
                  SELECT 1 
                  FROM shipments AS s 
                  WHERE s.order_id = o.id AND s.status = 'RETURNED'
              )