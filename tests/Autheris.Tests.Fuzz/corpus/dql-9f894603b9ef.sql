SELECT id, name, revenue
            FROM customers
            WHERE country = @country 
              AND created_at >= @fromDate 
              AND status = {{status}};