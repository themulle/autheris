SELECT 
                c.company_name, 
                COUNT(i.invoice_id) as invoice_count, 
                SUM(i.total) as total_spent
            FROM customers c
            JOIN invoices i ON c.customer_id = i.customer_id
            GROUP BY c.company_name
            ORDER BY total_spent DESC