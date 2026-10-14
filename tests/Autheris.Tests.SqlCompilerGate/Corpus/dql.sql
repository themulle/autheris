-- @id 91278b35fd0c class=select expected=accept source=benchmarks/Autheris.Benchmarks/CompilePathBenchmark.cs
SELECT o.id, o.status, sum(o.amount) AS total FROM orders o JOIN entitlements e ON e.orderid = o.id WHERE o.status = 'open' GROUP BY o.id, o.status ORDER BY total DESC

-- @id 85aaa1d18ad0 class=select expected=accept source=generated:agg-sum
SELECT status, sum(amount) FROM orders GROUP BY status

-- @id 1ef8a3929f91 class=select expected=accept source=generated:alias
SELECT t.id FROM orders t WHERE t.id > 1

-- @id 767b0f540337 class=select expected=accept source=generated:alias
SELECT t.id FROM users t WHERE t.id > 1

-- @id 8e49cd0d2905 class=select expected=accept source=generated:alias
SELECT t.id FROM employees t WHERE t.id > 1

-- @id aa960f79851f class=select expected=accept source=generated:alias
SELECT t.id FROM customers t WHERE t.id > 1

-- @id fa63940818e6 class=select expected=accept source=generated:alias
SELECT t.id FROM entitlements t WHERE t.id > 1

-- @id 6648974c4da3 class=select expected=accept source=generated:case-expr
SELECT id, CASE WHEN amount > 100 THEN 'big' ELSE 'small' END FROM orders

-- @id 9e1b05ac03d8 class=select expected=accept source=generated:cast
SELECT CAST(id AS varchar(20)) FROM orders

-- @id 11b99a014f52 class=select expected=accept source=generated:column
SELECT id FROM customers

-- @id 196d8c4afa65 class=select expected=accept source=generated:column
SELECT name FROM customers

-- @id 1f2e8847b7fb class=select expected=accept source=generated:column
SELECT dept FROM orders

-- @id 2e794f593318 class=select expected=accept source=generated:column
SELECT note FROM entitlements

-- @id 2e9b5c7b7a7e class=select expected=accept source=generated:column
SELECT tenant_id FROM employees

-- @id 35ca2b4bf259 class=select expected=accept source=generated:column
SELECT tenant_id FROM orders

-- @id 409abf7beb52 class=select expected=accept source=generated:column
SELECT email FROM customers

-- @id 5aed4eccf503 class=select expected=accept source=generated:column
SELECT region FROM customers

-- @id 5c05012f6f06 class=select expected=accept source=generated:column
SELECT salary FROM employees

-- @id 5dc500f6d364 class=select expected=accept source=generated:column
SELECT tenant_id FROM users

-- @id 5f0523b49476 class=select expected=accept source=generated:column
SELECT status FROM users

-- @id 69d418f1b3f4 class=select expected=accept source=generated:column
SELECT email FROM users

-- @id 6a5032f3b55d class=select expected=accept source=generated:column
SELECT order_id FROM entitlements

-- @id 6ffcbf973d6d class=select expected=accept source=generated:column
SELECT name FROM users

-- @id 7414121bd955 class=select expected=accept source=generated:column
SELECT id FROM entitlements

-- @id 796921f700ea class=select expected=accept source=generated:column
SELECT tenant_id FROM customers

-- @id 8083cce16b71 class=select expected=accept source=generated:column
SELECT id FROM users

-- @id 881d03bf4e74 class=select expected=accept source=generated:column
SELECT region FROM orders

-- @id 8abd195374f8 class=select expected=accept source=generated:column
SELECT created_at FROM orders

-- @id 8c2dbaf3526a class=select expected=accept source=generated:column
SELECT amount FROM orders

-- @id 96f539bfa206 class=select expected=accept source=generated:column
SELECT dept FROM employees

-- @id abc661b22075 class=select expected=accept source=generated:column
SELECT name FROM employees

-- @id b18cc504ec18 class=select expected=accept source=generated:column
SELECT customer_id FROM orders

-- @id bcbbe5e01cf1 class=select expected=accept source=generated:column
SELECT tenant_id FROM entitlements

-- @id d49f7d851c61 class=select expected=accept source=generated:column
SELECT note FROM orders

-- @id 2131278ee39a class=select expected=accept source=generated:count
SELECT count(*) FROM employees

-- @id 46880a16e933 class=select expected=accept source=generated:count
SELECT count(*) FROM customers

-- @id eb9c3522711f class=select expected=accept source=generated:count
SELECT count(*) FROM users

-- @id ebd346b8b15c class=select expected=accept source=generated:count
SELECT count(*) FROM entitlements

-- @id 0a25fc163ad8 class=select expected=accept source=generated:cte
WITH x AS (SELECT id FROM entitlements) SELECT id FROM x

-- @id 11b99fe1fc98 class=select expected=accept source=generated:cte
WITH x AS (SELECT id FROM users) SELECT id FROM x

-- @id 24cab767c10e class=select expected=accept source=generated:cte
WITH x AS (SELECT id FROM orders) SELECT id FROM x

-- @id 43900033aa14 class=select expected=accept source=generated:cte
WITH x AS (SELECT id FROM employees) SELECT id FROM x

-- @id 68a5464fdc4e class=select expected=accept source=generated:cte
WITH x AS (SELECT id FROM customers) SELECT id FROM x

-- @id ffd53dc175f3 class=select expected=accept source=generated:cte-masked
WITH m AS (SELECT email FROM users) SELECT email FROM m

-- @id 10bfd6f237f1 class=select expected=accept source=generated:derived
SELECT d.id FROM (SELECT id FROM customers) d

-- @id 3978ba534a4a class=select expected=accept source=generated:derived
SELECT d.id FROM (SELECT id FROM orders) d

-- @id 70b4c736999f class=select expected=accept source=generated:derived
SELECT d.id FROM (SELECT id FROM entitlements) d

-- @id 72384306e276 class=select expected=accept source=generated:derived
SELECT d.id FROM (SELECT id FROM users) d

-- @id c440a378d0a0 class=select expected=accept source=generated:derived
SELECT d.id FROM (SELECT id FROM employees) d

-- @id 1856a7dbf212 class=select expected=accept source=generated:distinct
SELECT DISTINCT customer_id FROM orders

-- @id 1bb9fe9db030 class=select expected=accept source=generated:distinct
SELECT DISTINCT tenant_id FROM entitlements

-- @id 20758496c55a class=select expected=accept source=generated:distinct
SELECT DISTINCT email FROM orders

-- @id 2a014bf4849b class=select expected=accept source=generated:distinct
SELECT DISTINCT status FROM orders

-- @id 359fbc4be6bf class=select expected=accept source=generated:distinct
SELECT DISTINCT name FROM customers

-- @id 416d44426bb4 class=select expected=accept source=generated:distinct
SELECT DISTINCT order_id FROM entitlements

-- @id 4438ec8e4330 class=select expected=accept source=generated:distinct
SELECT DISTINCT created_at FROM orders

-- @id 5688e1f58fec class=select expected=accept source=generated:distinct
SELECT DISTINCT salary FROM employees

-- @id 578e53d65462 class=select expected=accept source=generated:distinct
SELECT DISTINCT id FROM users

-- @id 59ec943aa9c6 class=select expected=accept source=generated:distinct
SELECT DISTINCT tenant_id FROM customers

-- @id 6609d11e700c class=select expected=accept source=generated:distinct
SELECT DISTINCT email FROM customers

-- @id 6ccb7f462d21 class=select expected=accept source=generated:distinct
SELECT DISTINCT name FROM employees

-- @id 7d1e956f087d class=select expected=accept source=generated:distinct
SELECT DISTINCT amount FROM orders

-- @id 86f25821f4cd class=select expected=accept source=generated:distinct
SELECT DISTINCT note FROM orders

-- @id 8e79faec103a class=select expected=accept source=generated:distinct
SELECT DISTINCT id FROM orders

-- @id 9768305ada59 class=select expected=accept source=generated:distinct
SELECT DISTINCT tenant_id FROM orders

-- @id 9d6aac8e4af2 class=select expected=accept source=generated:distinct
SELECT DISTINCT dept FROM employees

-- @id a4ad4539b32f class=select expected=accept source=generated:distinct
SELECT DISTINCT tenant_id FROM users

-- @id ab106e73a3e7 class=select expected=accept source=generated:distinct
SELECT DISTINCT tenant_id FROM employees

-- @id b920b902ad69 class=select expected=accept source=generated:distinct
SELECT DISTINCT id FROM entitlements

-- @id c1cc8ccc84ed class=select expected=accept source=generated:distinct
SELECT DISTINCT region FROM customers

-- @id cf254a360ca2 class=select expected=accept source=generated:distinct
SELECT DISTINCT dept FROM orders

-- @id da98db5fec1c class=select expected=accept source=generated:distinct
SELECT DISTINCT id FROM customers

-- @id e071ee05849c class=select expected=accept source=generated:distinct
SELECT DISTINCT name FROM users

-- @id e320164fbc54 class=select expected=accept source=generated:distinct
SELECT DISTINCT region FROM orders

-- @id f434afd62bc0 class=select expected=accept source=generated:distinct
SELECT DISTINCT status FROM users

-- @id f482b1905194 class=select expected=accept source=generated:distinct
SELECT DISTINCT email FROM users

-- @id f97b0c779166 class=select expected=accept source=generated:distinct
SELECT DISTINCT id FROM employees

-- @id f9b34e3921e7 class=select expected=accept source=generated:distinct
SELECT DISTINCT note FROM entitlements

-- @id 1d1f126601f8 class=select expected=accept source=generated:except
SELECT id FROM orders EXCEPT SELECT id FROM customers

-- @id 176594dbcb32 class=select expected=accept source=generated:exists
SELECT id FROM orders a WHERE EXISTS (SELECT 1 FROM orders b WHERE b.id = a.id)

-- @id 230ae64ec3f1 class=select expected=accept source=generated:exists
SELECT id FROM entitlements a WHERE EXISTS (SELECT 1 FROM entitlements b WHERE b.id = a.id)

-- @id 4df0cd54e0af class=select expected=accept source=generated:exists
SELECT id FROM users a WHERE EXISTS (SELECT 1 FROM users b WHERE b.id = a.id)

-- @id 6200c79731e1 class=select expected=accept source=generated:exists
SELECT id FROM customers a WHERE EXISTS (SELECT 1 FROM customers b WHERE b.id = a.id)

-- @id 67eb91177ee4 class=select expected=accept source=generated:exists
SELECT id FROM employees a WHERE EXISTS (SELECT 1 FROM employees b WHERE b.id = a.id)

-- @id 96056d0c19e5 class=select expected=accept source=generated:exists-cross
SELECT id FROM orders o WHERE EXISTS (SELECT 1 FROM entitlements e WHERE e.order_id = o.id)

-- @id 009901b2b24f class=select expected=accept source=generated:group
SELECT email, count(*) FROM customers GROUP BY email

-- @id 04e42666fc04 class=select expected=accept source=generated:group
SELECT id, count(*) FROM employees GROUP BY id

-- @id 0a0ba1368f62 class=select expected=accept source=generated:group
SELECT email, count(*) FROM orders GROUP BY email

-- @id 1980b740655e class=select expected=accept source=generated:group
SELECT email, count(*) FROM users GROUP BY email

-- @id 28fa8620eae6 class=select expected=accept source=generated:group
SELECT status, count(*) FROM users GROUP BY status

-- @id 35f970d2bce0 class=select expected=accept source=generated:group
SELECT note, count(*) FROM orders GROUP BY note

-- @id 3bd59d01536e class=select expected=accept source=generated:group
SELECT name, count(*) FROM customers GROUP BY name

-- @id 408d104f1c68 class=select expected=accept source=generated:group
SELECT id, count(*) FROM orders GROUP BY id

-- @id 5d774b72bc17 class=select expected=accept source=generated:group
SELECT name, count(*) FROM users GROUP BY name

-- @id 6049c0bc205c class=select expected=accept source=generated:group
SELECT id, count(*) FROM entitlements GROUP BY id

-- @id 698ddc6131a6 class=select expected=accept source=generated:group
SELECT tenant_id, count(*) FROM users GROUP BY tenant_id

-- @id 69bcbcc50e45 class=select expected=accept source=generated:group
SELECT created_at, count(*) FROM orders GROUP BY created_at

-- @id 776c8a1d62be class=select expected=accept source=generated:group
SELECT salary, count(*) FROM employees GROUP BY salary

-- @id 8612fbb2b835 class=select expected=accept source=generated:group
SELECT note, count(*) FROM entitlements GROUP BY note

-- @id 8be9570006e1 class=select expected=accept source=generated:group
SELECT status, count(*) FROM orders GROUP BY status

-- @id 8dedc8d3c76f class=select expected=accept source=generated:group
SELECT amount, count(*) FROM orders GROUP BY amount

-- @id 9624842b5a11 class=select expected=accept source=generated:group
SELECT tenant_id, count(*) FROM customers GROUP BY tenant_id

-- @id 9f3c31578295 class=select expected=accept source=generated:group
SELECT tenant_id, count(*) FROM orders GROUP BY tenant_id

-- @id a5c0c8a55836 class=select expected=accept source=generated:group
SELECT region, count(*) FROM customers GROUP BY region

-- @id bb1be8be198f class=select expected=accept source=generated:group
SELECT tenant_id, count(*) FROM entitlements GROUP BY tenant_id

-- @id bb629fa29a00 class=select expected=accept source=generated:group
SELECT id, count(*) FROM users GROUP BY id

-- @id bd64ab177e86 class=select expected=accept source=generated:group
SELECT region, count(*) FROM orders GROUP BY region

-- @id bf5215afb130 class=select expected=accept source=generated:group
SELECT id, count(*) FROM customers GROUP BY id

-- @id c1a6cfba1a89 class=select expected=accept source=generated:group
SELECT dept, count(*) FROM employees GROUP BY dept

-- @id c2bce3d09929 class=select expected=accept source=generated:group
SELECT dept, count(*) FROM orders GROUP BY dept

-- @id c56a64507eac class=select expected=accept source=generated:group
SELECT order_id, count(*) FROM entitlements GROUP BY order_id

-- @id e03f72ad1c94 class=select expected=accept source=generated:group
SELECT tenant_id, count(*) FROM employees GROUP BY tenant_id

-- @id e8a70682bba1 class=select expected=accept source=generated:group
SELECT customer_id, count(*) FROM orders GROUP BY customer_id

-- @id f28f24459c58 class=select expected=accept source=generated:group
SELECT name, count(*) FROM employees GROUP BY name

-- @id 0627f884529d class=select expected=accept source=generated:group-having
SELECT tenant_id, count(*) FROM orders GROUP BY tenant_id HAVING count(*) > 0

-- @id 10b21acb6799 class=select expected=accept source=generated:group-having
SELECT tenant_id, count(*) FROM customers GROUP BY tenant_id HAVING count(*) > 0

-- @id 22e41c4ed0f6 class=select expected=accept source=generated:group-having
SELECT tenant_id, count(*) FROM users GROUP BY tenant_id HAVING count(*) > 0

-- @id 336aad7804c8 class=select expected=accept source=generated:group-having
SELECT tenant_id, count(*) FROM entitlements GROUP BY tenant_id HAVING count(*) > 0

-- @id 35e7d7ead054 class=select expected=accept source=generated:group-having
SELECT tenant_id, count(*) FROM employees GROUP BY tenant_id HAVING count(*) > 0

-- @id 29dae9692559 class=select expected=accept source=generated:in-subquery
SELECT id FROM customers WHERE id IN (SELECT id FROM customers WHERE id < 3)

-- @id 2da5cd160daa class=select expected=accept source=generated:in-subquery
SELECT id FROM orders WHERE id IN (SELECT id FROM orders WHERE id < 3)

-- @id 65777dab178d class=select expected=accept source=generated:in-subquery
SELECT id FROM employees WHERE id IN (SELECT id FROM employees WHERE id < 3)

-- @id 7bb1ae2f5249 class=select expected=accept source=generated:in-subquery
SELECT id FROM users WHERE id IN (SELECT id FROM users WHERE id < 3)

-- @id df98f24c8146 class=select expected=accept source=generated:in-subquery
SELECT id FROM entitlements WHERE id IN (SELECT id FROM entitlements WHERE id < 3)

-- @id 4adec7c8f218 class=select expected=accept source=generated:intersect
SELECT id FROM orders INTERSECT SELECT id FROM customers

-- @id 04f66cca50e9 class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE amount IS NOT NULL

-- @id 07de73300cf1 class=select expected=accept source=generated:is-null
SELECT id FROM employees WHERE name IS NOT NULL

-- @id 10c176447ec7 class=select expected=accept source=generated:is-null
SELECT id FROM users WHERE name IS NOT NULL

-- @id 1b1a71c89bb3 class=select expected=accept source=generated:is-null
SELECT id FROM entitlements WHERE tenant_id IS NOT NULL

-- @id 1bd4e9aff3f3 class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE id IS NOT NULL

-- @id 2532a4516f6c class=select expected=accept source=generated:is-null
SELECT id FROM customers WHERE name IS NOT NULL

-- @id 2de523f1e6ee class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE customer_id IS NOT NULL

-- @id 4641ae7f75d2 class=select expected=accept source=generated:is-null
SELECT id FROM entitlements WHERE order_id IS NOT NULL

-- @id 51dd546699de class=select expected=accept source=generated:is-null
SELECT id FROM employees WHERE salary IS NOT NULL

-- @id 6ff098b74c66 class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE region IS NOT NULL

-- @id 75914d8eabd8 class=select expected=accept source=generated:is-null
SELECT id FROM employees WHERE dept IS NOT NULL

-- @id 7e36be87268e class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE status IS NOT NULL

-- @id 915ee550ad78 class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE note IS NOT NULL

-- @id 92dc10ff5873 class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE email IS NOT NULL

-- @id 95af79a78902 class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE tenant_id IS NOT NULL

-- @id 96b080007850 class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE created_at IS NOT NULL

-- @id 9752b393c0c6 class=select expected=accept source=generated:is-null
SELECT id FROM customers WHERE region IS NOT NULL

-- @id ae9c9c6a37ec class=select expected=accept source=generated:is-null
SELECT id FROM customers WHERE id IS NOT NULL

-- @id b4a43de50b23 class=select expected=accept source=generated:is-null
SELECT id FROM employees WHERE tenant_id IS NOT NULL

-- @id ba4ff8251786 class=select expected=accept source=generated:is-null
SELECT id FROM users WHERE email IS NOT NULL

-- @id c960418973be class=select expected=accept source=generated:is-null
SELECT id FROM users WHERE id IS NOT NULL

-- @id cbfbe1128f0f class=select expected=accept source=generated:is-null
SELECT id FROM users WHERE tenant_id IS NOT NULL

-- @id d22d0014ebc3 class=select expected=accept source=generated:is-null
SELECT id FROM entitlements WHERE id IS NOT NULL

-- @id d6dec4ec6c47 class=select expected=accept source=generated:is-null
SELECT id FROM customers WHERE tenant_id IS NOT NULL

-- @id d917fc475638 class=select expected=accept source=generated:is-null
SELECT id FROM entitlements WHERE note IS NOT NULL

-- @id dce53e52a68a class=select expected=accept source=generated:is-null
SELECT id FROM employees WHERE id IS NOT NULL

-- @id f63d88ecb0d5 class=select expected=accept source=generated:is-null
SELECT id FROM customers WHERE email IS NOT NULL

-- @id f6bfd8b3685b class=select expected=accept source=generated:is-null
SELECT id FROM orders WHERE dept IS NOT NULL

-- @id fb35b696449d class=select expected=accept source=generated:is-null
SELECT id FROM users WHERE status IS NOT NULL

-- @id 0fa0775dc990 class=select expected=accept source=generated:join-cross-table
SELECT e.id, o.status FROM entitlements e JOIN orders o ON e.order_id = o.id

-- @id bc4d663c0a89 class=select expected=accept source=generated:join-left
SELECT o.id, c.email FROM orders o LEFT JOIN customers c ON o.customer_id = c.id

-- @id 1adf7d5a40e4 class=select expected=accept source=generated:join-masked
SELECT o.email, c.email FROM orders o JOIN customers c ON o.customer_id = c.id

-- @id 06a709f39512 class=select expected=accept source=generated:lateral-like
SELECT id FROM orders WHERE note LIKE 'orders-B-%'

-- @id 005b1cbf3b64 class=select expected=accept source=generated:literal-tenant
SELECT id FROM orders WHERE tenant_id = 'A' OR tenant_id = 'B'

-- @id 309429807c06 class=select expected=accept source=generated:masked-case
SELECT CASE WHEN email IS NULL THEN 0 ELSE 1 END FROM users

-- @id 72c679e2c035 class=select expected=accept source=generated:masked-case
SELECT CASE WHEN email IS NULL THEN 0 ELSE 1 END FROM customers

-- @id 745023bb1db4 class=select expected=accept source=generated:masked-case
SELECT CASE WHEN email IS NULL THEN 0 ELSE 1 END FROM orders

-- @id 400bb66c1266 class=select expected=accept source=generated:masked-function
SELECT upper(email) FROM customers

-- @id 8003989e901c class=select expected=accept source=generated:masked-function
SELECT upper(email) FROM users

-- @id 9d76cb6bb832 class=select expected=reject source=generated:masked-predicate
SELECT id FROM users WHERE email = 'x'

-- @id cccd0c46a058 class=select expected=reject source=generated:masked-predicate
SELECT id FROM customers WHERE email = 'x'

-- @id 36eba39c100b class=select expected=reject source=generated:masked-sort
SELECT id FROM customers ORDER BY email

-- @id 818d39247803 class=select expected=reject source=generated:masked-sort
SELECT id FROM users ORDER BY email

-- @id 92acb2a696b5 class=select expected=accept source=generated:not-in
SELECT id FROM customers WHERE id NOT IN (SELECT customer_id FROM orders)

-- @id 53a8ed7e8c08 class=select expected=accept source=generated:or-tenant
SELECT id FROM customers WHERE id = 1 OR tenant_id = 'B'

-- @id 779a0db1facc class=select expected=accept source=generated:or-tenant
SELECT id FROM users WHERE id = 1 OR tenant_id = 'B'

-- @id 803a19711924 class=select expected=accept source=generated:or-tenant
SELECT id FROM employees WHERE id = 1 OR tenant_id = 'B'

-- @id 9f86c1e431fe class=select expected=accept source=generated:or-tenant
SELECT id FROM entitlements WHERE id = 1 OR tenant_id = 'B'

-- @id ee404d195349 class=select expected=accept source=generated:or-tenant
SELECT id FROM orders WHERE id = 1 OR tenant_id = 'B'

-- @id 0a4af1b77122 class=select expected=accept source=generated:order
SELECT id, status FROM orders ORDER BY id

-- @id 0d5ba2721480 class=select expected=accept source=generated:order
SELECT id, email FROM customers ORDER BY id

-- @id 13612906c097 class=select expected=accept source=generated:order
SELECT id, region FROM customers ORDER BY id

-- @id 1a16c749c1bf class=select expected=accept source=generated:order
SELECT id, tenant_id FROM customers ORDER BY id

-- @id 1c388dfdf0ba class=select expected=accept source=generated:order
SELECT id, tenant_id FROM users ORDER BY id

-- @id 27f134b70f56 class=select expected=accept source=generated:order
SELECT id, tenant_id FROM orders ORDER BY id

-- @id 2dcddce8ae6d class=select expected=accept source=generated:order
SELECT id, customer_id FROM orders ORDER BY id

-- @id 32c93f76f325 class=select expected=accept source=generated:order
SELECT id, status FROM users ORDER BY id

-- @id 39a7e9b0be5e class=select expected=accept source=generated:order
SELECT id, id FROM customers ORDER BY id

-- @id 425e920954ac class=select expected=accept source=generated:order
SELECT id, order_id FROM entitlements ORDER BY id

-- @id 4c4402861420 class=select expected=accept source=generated:order
SELECT id, email FROM users ORDER BY id

-- @id 505610a03bf7 class=select expected=accept source=generated:order
SELECT id, dept FROM employees ORDER BY id

-- @id 52cfd1f7515f class=select expected=accept source=generated:order
SELECT id, id FROM orders ORDER BY id

-- @id 56df212fcd89 class=select expected=accept source=generated:order
SELECT id, id FROM users ORDER BY id

-- @id 5b353ad0c2a0 class=select expected=accept source=generated:order
SELECT id, dept FROM orders ORDER BY id

-- @id 719520b493d0 class=select expected=accept source=generated:order
SELECT id, amount FROM orders ORDER BY id

-- @id 78a311d8f245 class=select expected=accept source=generated:order
SELECT id, note FROM orders ORDER BY id

-- @id 7be1bc8b0bdf class=select expected=accept source=generated:order
SELECT id, salary FROM employees ORDER BY id

-- @id 7decd4f4ac24 class=select expected=accept source=generated:order
SELECT id, name FROM employees ORDER BY id

-- @id 7f207097e23c class=select expected=accept source=generated:order
SELECT id, tenant_id FROM employees ORDER BY id

-- @id 9cf4a10efedd class=select expected=accept source=generated:order
SELECT id, region FROM orders ORDER BY id

-- @id b71c0d87623e class=select expected=accept source=generated:order
SELECT id, created_at FROM orders ORDER BY id

-- @id c2e3461186af class=select expected=accept source=generated:order
SELECT id, name FROM users ORDER BY id

-- @id ce4993379e55 class=select expected=accept source=generated:order
SELECT id, tenant_id FROM entitlements ORDER BY id

-- @id d4a58a9534b1 class=select expected=accept source=generated:order
SELECT id, note FROM entitlements ORDER BY id

-- @id d67d41856fe1 class=select expected=accept source=generated:order
SELECT id, email FROM orders ORDER BY id

-- @id df3160995ac5 class=select expected=accept source=generated:order
SELECT id, id FROM employees ORDER BY id

-- @id ef032bd306ba class=select expected=accept source=generated:order
SELECT id, name FROM customers ORDER BY id

-- @id fabd0fdf2902 class=select expected=accept source=generated:order
SELECT id, id FROM entitlements ORDER BY id

-- @id c889984ecb16 class=select expected=accept source=generated:pagination
SELECT id FROM orders ORDER BY id OFFSET 1 LIMIT 2

-- @id 43226cf954d6 class=select expected=accept source=generated:scalar-subquery
SELECT (SELECT count(*) FROM users) AS n FROM users WHERE id = 1

-- @id a51c6e5a66f2 class=select expected=accept source=generated:scalar-subquery
SELECT (SELECT count(*) FROM orders) AS n FROM orders WHERE id = 1

-- @id cea7a73038a9 class=select expected=accept source=generated:scalar-subquery
SELECT (SELECT count(*) FROM employees) AS n FROM employees WHERE id = 1

-- @id d0ea68c125f0 class=select expected=accept source=generated:scalar-subquery
SELECT (SELECT count(*) FROM customers) AS n FROM customers WHERE id = 1

-- @id fd2f693dafd4 class=select expected=accept source=generated:scalar-subquery
SELECT (SELECT count(*) FROM entitlements) AS n FROM entitlements WHERE id = 1

-- @id 4a79fa40a4ff class=select expected=accept source=generated:self-join
SELECT a.id, b.id FROM employees a JOIN employees b ON a.id = b.id

-- @id bd7ce213cae5 class=select expected=accept source=generated:self-join
SELECT a.id, b.id FROM entitlements a JOIN entitlements b ON a.id = b.id

-- @id c43afa0c0b86 class=select expected=accept source=generated:self-join
SELECT a.id, b.id FROM orders a JOIN orders b ON a.id = b.id

-- @id e975df1e461d class=select expected=accept source=generated:self-join
SELECT a.id, b.id FROM customers a JOIN customers b ON a.id = b.id

-- @id ed76d006d723 class=select expected=accept source=generated:self-join
SELECT a.id, b.id FROM users a JOIN users b ON a.id = b.id

-- @id 0a361cf5a646 class=select expected=accept source=generated:star
SELECT * FROM entitlements

-- @id e663ebda6b8c class=select expected=accept source=generated:star
SELECT * FROM employees

-- @id fac85266495b class=select expected=accept source=generated:string-literal-hostile
SELECT id FROM orders WHERE status = 'it''s'

-- @id f794dcbb4456 class=select expected=accept source=generated:subquery-masked
SELECT x.email FROM (SELECT email FROM orders) x

-- @id 9e1ab229d4b2 class=select expected=reject source=generated:system-column
SELECT ctid FROM orders

-- @id 0654dd377d8f class=select expected=accept source=generated:tautology
SELECT id FROM users WHERE 1 = 1 OR id = 1

-- @id 37058853a28e class=select expected=accept source=generated:tautology
SELECT id FROM orders WHERE 1 = 1 OR id = 1

-- @id abd8ac500177 class=select expected=accept source=generated:tautology
SELECT id FROM entitlements WHERE 1 = 1 OR id = 1

-- @id bff9c4408914 class=select expected=accept source=generated:tautology
SELECT id FROM customers WHERE 1 = 1 OR id = 1

-- @id ea60e63b8673 class=select expected=accept source=generated:tautology
SELECT id FROM employees WHERE 1 = 1 OR id = 1

-- @id 07d5bda6b0d3 class=select expected=accept source=generated:tenant-predicate
SELECT id FROM customers WHERE tenant_id = 'B'

-- @id 843384865732 class=select expected=accept source=generated:tenant-predicate
SELECT id FROM entitlements WHERE tenant_id = 'B'

-- @id a8afb7585fd7 class=select expected=accept source=generated:tenant-predicate
SELECT id FROM users WHERE tenant_id = 'B'

-- @id b3881e43770c class=select expected=accept source=generated:tenant-predicate
SELECT id FROM orders WHERE tenant_id = 'B'

-- @id c3189d557896 class=select expected=accept source=generated:tenant-predicate
SELECT id FROM employees WHERE tenant_id = 'B'

-- @id 145cefb6de4b class=select expected=accept source=generated:top
SELECT id FROM orders ORDER BY id DESC LIMIT 3

-- @id 58f18c4d4bea class=select expected=accept source=generated:top
SELECT id FROM users ORDER BY id DESC LIMIT 3

-- @id b1be19286dfb class=select expected=accept source=generated:top
SELECT id FROM customers ORDER BY id DESC LIMIT 3

-- @id b66ce3117fba class=select expected=accept source=generated:top
SELECT id FROM employees ORDER BY id DESC LIMIT 3

-- @id dab32e3dc826 class=select expected=accept source=generated:top
SELECT id FROM entitlements ORDER BY id DESC LIMIT 3

-- @id 098035fc214c class=select expected=accept source=generated:union
SELECT id FROM users UNION ALL SELECT id FROM users

-- @id 97c11a7564c1 class=select expected=accept source=generated:union
SELECT id FROM employees UNION ALL SELECT id FROM employees

-- @id b462f00703ff class=select expected=accept source=generated:union
SELECT id FROM orders UNION ALL SELECT id FROM orders

-- @id b498c4df9fc4 class=select expected=accept source=generated:union
SELECT id FROM entitlements UNION ALL SELECT id FROM entitlements

-- @id bca06891dc8b class=select expected=accept source=generated:union
SELECT id FROM customers UNION ALL SELECT id FROM customers

-- @id 465281ca7c47 class=select expected=accept source=generated:union-masked
SELECT email FROM orders UNION ALL SELECT email FROM customers

-- @id 0666c127b9c4 class=select expected=accept source=generated:window
SELECT id, row_number() OVER (ORDER BY id) FROM orders

-- @id 2468fdab2ce7 class=select expected=accept source=generated:window
SELECT id, row_number() OVER (ORDER BY id) FROM users

-- @id 515b09958a25 class=select expected=accept source=generated:window
SELECT id, row_number() OVER (ORDER BY id) FROM entitlements

-- @id 7cf2530b5df0 class=select expected=accept source=generated:window
SELECT id, row_number() OVER (ORDER BY id) FROM customers

-- @id ed90452a7a9d class=select expected=accept source=generated:window
SELECT id, row_number() OVER (ORDER BY id) FROM employees

-- @id e99a51371dd2 class=select expected=accept source=generated:window-partition
SELECT id, sum(amount) OVER (PARTITION BY status ORDER BY id) FROM orders

-- @id 07002d19c3cb class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT extract(year FROM date '2024-05-06') AS y, extract(dow FROM date '2024-05-06') AS w FROM orders

-- @id 17ddb12aaedd class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT a.id FROM orders a WHERE a.amount IS DISTINCT FROM 20.00

-- @id 288132b1596e class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id, CASE WHEN amount > 15 THEN 'big' ELSE 'small' END AS size FROM orders

-- @id 36a13dd77c1a class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT status, count(*) AS n, sum(amount) AS total FROM orders GROUP BY status ORDER BY status

-- @id 3eea919039b8 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders WHERE status = __param_s

-- @id 473a0dcfdbca class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id, cast(amount AS integer) AS a, try_cast(status AS integer) AS t FROM orders

-- @id 4fe90815c5c3 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT approx_distinct(region) AS d, arbitrary(status) AS a FROM orders

-- @id 5b5272870e59 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders

-- @id 62b2206129ff class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders GROUP BY id, status HAVING count(*) > 0

-- @id 63f310d7e6fc class=select expected=reject source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders WHERE status = '${x}'

-- @id 6a5f7c1b0df3 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM default.orders ORDER BY id LIMIT 2 OFFSET 1

-- @id 6dab0623ca2a class=select expected=reject source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT secret('scope', 'key') FROM orders

-- @id 6dc4995fa89a class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id, email FROM orders

-- @id 6e39bdf9adee class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
WITH orders AS (SELECT id FROM default.orders) SELECT id FROM orders

-- @id 76876e0a2cb2 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT DISTINCT region FROM orders ORDER BY region

-- @id 783cf328d368 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders WHERE status = __param_st

-- @id 7ed8ff2aaa25 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT region, GROUPING(region) AS g, count(*) AS n FROM orders GROUP BY ROLLUP (region)

-- @id 8b051e05a0dc class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id, amount, email FROM orders WHERE id = 1

-- @id 96314b85c466 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders WHERE 1 = 1 OR status = 'open'

-- @id 9a87cbb784f9 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT count(*) FROM default.orders_ci WHERE tenantid = 'acme'

-- @id 9aa1a02ac52a class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id, sum(amount) OVER (PARTITION BY status ORDER BY amount ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS rn FROM orders

-- @id 9ac0dcae5415 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders ORDER BY amount DESC NULLS LAST, id

-- @id 9de15d614b40 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT o.id FROM default.orders o, LATERAL (SELECT e.id FROM default.entitlements e WHERE e.orderid = o.id) l

-- @id ac36893cd2f0 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders WHERE status = 'open' AND amount > 10.5 ORDER BY id OFFSET 1 LIMIT 5

-- @id aea75f204505 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT * FROM (VALUES (1, 'a'), (2, 'b')) AS v (x, y)

-- @id b6ef1a9ca0ef class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT CAST('2024-01-15 10:00:00' AS TIMESTAMP_NTZ) AS t

-- @id bb2e15e689ed class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT reflect('java.lang.System', 'getProperty', 'java.version') FROM orders

-- @id be4d771d1a8c class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders WHERE email LIKE '%@acme.example' OR email IS NULL

-- @id bece59cf5818 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT count(*) FILTER (WHERE amount > 15) AS n FROM orders

-- @id c24b685f6efe class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT substring(email FROM 1 FOR 3) AS s, upper(status) AS u, length(status) AS l, strpos(email, '@') AS p FROM orders

-- @id e004ebd5b553 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT 1

-- @id e1f4d888cd6e class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders WHERE id BETWEEN 1 AND 6 AND status IN ('open', 'closed') AND NOT (amount < 0)

-- @id e2df86324271 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT date '2024-01-15' AS d, timestamp '2024-01-15 10:30:00' AS ts FROM orders

-- @id e54f8196ddd2 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id, email FROM orders WHERE id = 1

-- @id ec0713f8d778 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
WITH o AS (SELECT id, status FROM orders) SELECT o.id FROM o JOIN orders p ON p.id = o.id WHERE o.id IN (SELECT id FROM orders)

-- @id ecf344c50257 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT * FROM orders

-- @id ed1d9dc74410 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id FROM orders_ci

-- @id f3645b38d308 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT java_method('java.lang.System', 'getProperty', 'java.version') FROM orders

-- @id fd6eac6932b9 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDatabricksSparkExecutionTests.cs
SELECT id, amount > 15 AS flag FROM orders

-- @id c51d15550ab6 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerDuckDbDmlExecutionTests.cs
SELECT count(*) FROM "Orders" WHERE "TenantId" = 'acme'

-- @id 0e645e441865 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
WITH "orders" AS (SELECT 1 AS id) SELECT id FROM orders

-- @id 1070fd99bfe5 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT o.id FROM orders o CROSS JOIN LATERAL (SELECT e.id FROM entitlements e WHERE e.order_id = o.id) l

-- @id 17220c984a49 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT id FROM orders ORDER BY id FETCH FIRST 2 ROWS WITH TIES

-- @id 365aace67b38 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT id FROM AUTH_APP.ORDERS WHERE tenant_id = :t AND id <= :n AND tenant_id = :t ORDER BY id

-- @id 3934469d1f37 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER') FROM DUAL

-- @id 4657e7b1326d class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
WITH "orders" AS (SELECT id FROM AUTH_APP.orders) SELECT id FROM "orders"

-- @id 60edbd77bf22 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT COUNT(*) FROM AUTH_APP.ORDERS WHERE TENANT_ID = 'acme'

-- @id 6e60ae760277 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT id, cast(amount AS integer) AS a, cast(status AS varchar) AS t FROM orders

-- @id 6f5237d21f25 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
WITH orders AS (SELECT id FROM AUTH_APP.orders) SELECT id FROM orders

-- @id 7b32585baa7e class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
WITH orders AS (SELECT 1 AS id) SELECT id FROM Orders

-- @id 8221e4bdbb78 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT (SELECT value FROM nls_session_parameters WHERE parameter = 'NLS_COMP') || '|' || SYS_CONTEXT('USERENV','NLS_SORT') || '|' || SYS_CONTEXT('USERENV','NLS_DATE_FORMAT') FROM DUAL

-- @id 82e8d731fe53 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
WITH RECURSIVE orders (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT id, n + 1 FROM orders WHERE n < 3) SELECT id FROM orders

-- @id 9e1cf50b1c2f class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT SYS_CONTEXT('USERENV','CLIENT_IDENTIFIER') || '|' || SYS_CONTEXT('USERENV','MODULE') FROM DUAL

-- @id a8b1050c1f60 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT id FROM orders WHERE id IN (

-- @id bfab0251b8ea class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT COUNT(*) FROM DUAL WHERE

-- @id c3b9b519cbdd class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT 1 AS x

-- @id c5a39174cf01 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT ID FROM AUTH_APP.ORDERS ORDER BY ID OFFSET :gql_offset ROWS FETCH NEXT :gql_limit ROWS ONLY

-- @id d119663e7737 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
WITH RECURSIVE chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT id, n + 1 FROM chain WHERE n < 3) SELECT id, n FROM chain

-- @id ee154db585a9 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT count(*) FILTER (WHERE id > 0) AS hit, count(*) FILTER (WHERE id > 1000) AS miss FROM orders

-- @id ef70a8063eeb class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
SELECT extract(year FROM date '2024-05-06') AS y, extract(week FROM date '2024-05-06') AS w FROM orders

-- @id f7d03393fdce class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerOracleExecutionTests.cs
WITH "orders" AS (SELECT 1 AS id) SELECT id FROM ORDERS

-- @id dd613b0c06cd class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlDmlExecutionTests.cs
SELECT status FROM pg_temp.orders

-- @id 04a51fdb099d class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT count(*) FROM generate_series(1, 10) AS g(x) WHERE x IN (

-- @id 083b12813eec class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT count(*) FROM public.orders_icu WHERE tenantid = 'acme'

-- @id 0c44e75a1d25 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT count(*) FROM orders WHERE id = 77

-- @id 225d9e9fac41 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
WITH orders AS (SELECT id FROM public.orders) SELECT id FROM orders

-- @id 27524e860b25 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT id FROM orders WHERE email = 'x'

-- @id 2e113a12d9af class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT date '2024-01-15' AS d, timestamp '2024-01-15 10:30:00' AS ts, time '10:30:00' AS tm FROM orders

-- @id 373f2013e699 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT id FROM orders WHERE status = __param_st AND amount > __param_min

-- @id 50bd51035503 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT to_jsonb(q)::text FROM (

-- @id 655397aa4f94 class=select expected=reject source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT CAST(status AS xml) FROM orders

-- @id 75aadd23e08f class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT CAST(__param_t AS regrole) FROM orders

-- @id 7a5d622a0af4 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT o.id FROM o JOIN orders p ON p.id = o.id WHERE o.id IN (SELECT id FROM orders)

-- @id 9166d9488b92 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT count(*) FROM public.orders WHERE tenantid = 'acme'

-- @id 9adbe8bb0b37 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
WITH o AS (SELECT id, status FROM orders)

-- @id a169cb31d154 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT CAST(__param_t AS regclass) FROM orders

-- @id aafb9c38c930 class=select expected=reject source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT __param_t::regclass FROM orders

-- @id baa9cb9ca135 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT o.id FROM orders o CROSS JOIN LATERAL (SELECT e.id FROM entitlements e WHERE e.orderid = o.id) l

-- @id c521511c7d8c class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT id FROM orders ORDER BY email

-- @id d4b42ac5aeb2 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT count(*) FILTER (WHERE amount > 15) AS n, array_agg(id ORDER BY id) AS ids FROM orders

-- @id e2a2f1392abd class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerPostgreSqlExecutionTests.cs
SELECT id FROM orders_icu

-- @id 0118db3b95f8 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
SELECT id FROM dbo.Orders

-- @id 58d4653d3981 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
WITH Orders AS (SELECT 1 AS id) SELECT id FROM ORDERS

-- @id 6d626a7442c0 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
SELECT substring(email FROM 1 FOR 3) AS s, upper(status) AS u, length(status) AS l FROM orders

-- @id 81fa25c48adf class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
SELECT id FROM "DBO"."ORDERS"

-- @id 89da6d93b586 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
SELECT id FROM ansiorders

-- @id 9c1d045e56d3 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
WITH orders AS (SELECT id FROM dbo.orders) SELECT id FROM orders

-- @id ecc4995eac72 class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
WITH orders AS (SELECT Id FROM dbo.Orders) SELECT Id FROM orders

-- @id f1185d76787e class=select expected=accept source=tests/Autheris.Tests.Integration/AstCompilerSqlServerExecutionTests.cs
SELECT id FROM orders WHERE status = 'open' AND amount > 10.5 ORDER BY id OFFSET 1 ROWS FETCH NEXT 5 ROWS ONLY

-- @id 1fb80582f03e class=select expected=accept source=tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs
SELECT serial_number, crane_type, tonnage FROM e2e_cranes WHERE crane_type = @crane_type ORDER BY serial_number

-- @id 355e72b814cb class=select expected=reject source=tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs
SELECT * FROM not_in_catalog

-- @id 5dc8a75c44f5 class=select expected=reject source=tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs
SELECT COUNT(*) FROM e2e_cranes

-- @id 8b6a879805e9 class=select expected=accept source=tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs
SELECT serial_number, tonnage FROM e2e_cranes WHERE crane_type = 'Mobilkran' ORDER BY tonnage

-- @id 9d10a4b067c9 class=select expected=accept source=tests/Autheris.Tests.Integration/EndToEndSqliteTests.cs
SELECT COUNT(*) AS n FROM e2e_cranes

-- @id d5dd403ea324 class=select expected=accept source=tests/Autheris.Tests.Integration/FinOpsEndToEndSqliteTests.cs
SELECT serial_number FROM fin_cranes ORDER BY id

-- @id 00ca292889e7 class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT id, name FROM users

-- @id 1ba0945cc5b3 class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT * FROM orders WHERE total > 100 LIMIT 5000

-- @id 1fd01f02bb1c class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT id, name, email FROM customers WHERE active = 1

-- @id 6abf62c3085f class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT c.id, o.id FROM crm_customers c, orders o WHERE c.email = o.email

-- @id 6d9efea7818c class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
WITH accounts AS (
                SELECT * FROM accounts WHERE tenant_id = 'victim'
            )
            SELECT * FROM accounts

-- @id 9d7814adcf3f class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT c.id, o.id FROM crm_customers c JOIN orders o ON c.email = o.email

-- @id b4b5962f008c class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT e.id, p.id FROM hr_employees e JOIN payroll p ON e.ssn = p.ssn

-- @id d083b5c3d5f8 class=select expected=reject source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT statements

-- @id d2d87814a8d9 class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT id, name, ssn FROM employees

-- @id e35618a3b68a class=select expected=accept source=tests/Autheris.Tests.Integration/GovernedWebSqlIntegrationTests.cs
SELECT u.id, o.id FROM secure_users u JOIN orders o ON u.email = o.email

-- @id 7566f6a80913 class=select expected=accept source=tests/Autheris.Tests.Integration/MsSqlIntegrationTests.cs
SELECT id, customername, totalamount FROM orders ORDER BY id OFFSET 1 LIMIT 2

-- @id ac7968734323 class=select expected=accept source=tests/Autheris.Tests.Integration/MsSqlIntegrationTests.cs
SELECT TenantKey, DisplayName, IsActive FROM dbo.IntegrationTenants WHERE Id = @id;

-- @id f6d70ebd8982 class=select expected=accept source=tests/Autheris.Tests.Integration/OpenApiDocsEndToEndTests.cs
SELECT serial_number, crane_type FROM e2e_cranes WHERE crane_type = @crane_type ORDER BY serial_number

-- @id 65c0adc602c9 class=select expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
SELECT tenant_key, display_name, is_active FROM integration_tenants WHERE tenant_key = :p1

-- @id a7d65c195dd7 class=select expected=accept source=tests/Autheris.Tests.Integration/OracleIntegrationTests.cs
SELECT id, customer_name, total_amount FROM orders ORDER BY id OFFSET 1 LIMIT 2

-- @id d8d0b1957286 class=select expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlConsentContractTests.cs
SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_consent_requests_ticket'

-- @id 149dadc45b42 class=select expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlGovernanceContractTests.cs
SELECT count(*), count(DISTINCT seq), max(seq), min(seq) FROM audit_log_entries

-- @id 7512a62fa151 class=select expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlIntegrationTests.cs
SELECT tenant_key, display_name, is_active FROM integration_tenants WHERE id = $1;

-- @id 8dffacb59d8c class=select expected=accept source=tests/Autheris.Tests.Integration/PostgreSqlWebSqlTransactionTests.cs
SELECT id, amount, active_tenant FROM active_invoices WHERE id = 1

-- @id 15ff6a60339b class=select expected=reject source=tests/Autheris.Tests.Integration/RowFilterChannelParityTests.cs
SELECT id FROM air1

-- @id 958c212fd66a class=select expected=reject source=tests/Autheris.Tests.Integration/RowFilterChannelParityTests.cs
SELECT id FROM default.main.air1

-- @id bbd98a31d442 class=select expected=accept source=tests/Autheris.Tests.Integration/RowFilterChannelParityTests.cs
SELECT a.id FROM air1 a
WHERE EXISTS (SELECT 1 FROM client c JOIN crane k ON k.serial_number = c.crane_serial_number
              WHERE c.client_id = a.client_id AND k.is_delivered IS NULL)

-- @id e8cb2370b2c9 class=select expected=accept source=tests/Autheris.Tests.Integration/SqlServerConsentContractTests.cs
SELECT is_unique FROM sys.indexes WHERE name = 'ux_consent_requests_ticket' AND object_id = OBJECT_ID('dbo.CONSENT_REQUESTS')

-- @id 4a874bd30819 class=select expected=accept source=tests/Autheris.Tests.Integration/SqlServerGovernanceContractTests.cs
SELECT COUNT_BIG(*) FROM AUDIT_LOG_ENTRIES

-- @id d478276e0eda class=select expected=accept source=tests/Autheris.Tests.Integration/SqlServerGovernanceContractTests.cs
SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT seq), MAX(seq), MIN(seq) FROM AUDIT_LOG_ENTRIES

-- @id 4f21ffddb3b3 class=select expected=reject source=tests/Autheris.Tests.Integration/VirtualFilterChannelParityTests.cs
SELECT serial_number FROM crane

-- @id 17a0f9e6bd25 class=select expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
SELECT COUNT(*) FROM orders WHERE amount = 0 AND tenant_id = 'tenant_b'

-- @id 40ce74e4a1d0 class=select expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
SELECT COUNT(*) FROM orders WHERE amount = 0 AND tenant_id = 'tenant_a'

-- @id 4f02f7b132ec class=select expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
SELECT COUNT(*) FROM orders WHERE amount = 0

-- @id b83e4b4007f5 class=select expected=accept source=tests/Autheris.Tests.Unit/BypassSemanticsAndDmlGuardrailTests.cs
SELECT COUNT(*) FROM orders

-- @id 6e9c86ce7f23 class=select expected=accept source=tests/Autheris.Tests.Unit/CompiledSqlQueryPlanCacheTests.cs
SELECT json_agg(...) FROM orders WHERE tenant_id = 'tenant-beta'

-- @id 6f6fef1beded class=select expected=accept source=tests/Autheris.Tests.Unit/CompiledSqlQueryPlanCacheTests.cs
SELECT json_agg(...) FROM orders WHERE tenant_id = 'tenant-alpha'

-- @id 3e73e60b4378 class=select expected=accept source=tests/Autheris.Tests.Unit/DataApi/GovernedDataQueryServiceTests.cs
SELECT ...

-- @id 6e267ad83dc6 class=select expected=accept source=tests/Autheris.Tests.Unit/DataApi/GovernedDataQueryServiceTests.cs
SELECT id, email, name FROM sales.dbo.customers

-- @id 5ae46252d063 class=select expected=accept source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
SELECT id, name, email FROM customers WHERE active = true

-- @id b3a98dcc4ed8 class=select expected=accept source=tests/Autheris.Tests.Unit/Diagnostics/TrafficShadowingGuardTests.cs
SELECT COUNT(*) AS total FROM transactions GROUP BY category

-- @id bb7d61b73aa2 class=select expected=accept source=tests/Autheris.Tests.Unit/Governance/Pol12ApprovalAuditFailClosedTests.cs
SELECT status FROM CONSENT_REQUESTS WHERE id = @id

-- @id c87cdf54b562 class=select expected=accept source=tests/Autheris.Tests.Unit/GovernedSqlPlanCacheTests.cs
SELECT id, name FROM orders

-- @id 272ee17617e7 class=select expected=accept source=tests/Autheris.Tests.Unit/InfrastructureTests.cs
SELECT COUNT(*) FROM CONSENTS WHERE id = @id

-- @id 058ebc42c25d class=select expected=accept source=tests/Autheris.Tests.Unit/IntegrationGapCTests.cs
SELECT id FROM employees

-- @id 049846306bdc class=select expected=accept source=tests/Autheris.Tests.Unit/Jobs/AsyncQueryJobManagerTests.cs
SELECT id, mask(email) FROM users

-- @id 81661af1600a class=select expected=accept source=tests/Autheris.Tests.Unit/Jobs/AsyncQueryJobManagerTests.cs
SELECT * FROM huge_table

-- @id 819ed510af1f class=select expected=accept source=tests/Autheris.Tests.Unit/Jobs/AsyncQueryJobManagerTests.cs
SELECT id, email FROM users

-- @id 965277c93c08 class=select expected=accept source=tests/Autheris.Tests.Unit/Jobs/AsyncQueryJobManagerTests.cs
SELECT * FROM sales.orders

-- @id 00075c6e785d class=select expected=reject source=tests/Autheris.Tests.Unit/Masking/AdvancedMaskingRuleTests.cs
SELECT department, sum(bonus) FROM sales GROUP BY department HAVING bonus > 1000

-- @id 154257fd5023 class=select expected=reject source=tests/Autheris.Tests.Unit/Masking/AdvancedMaskingRuleTests.cs
SELECT a.id, b.info FROM a JOIN b ON a.ssn = b.ssn

-- @id 606fb5947ff1 class=select expected=reject source=tests/Autheris.Tests.Unit/Masking/AdvancedMaskingRuleTests.cs
SELECT id, name FROM users ORDER BY ssn

-- @id 81098ccbc776 class=select expected=reject source=tests/Autheris.Tests.Unit/Masking/AdvancedMaskingRuleTests.cs
SELECT id, name FROM employees WHERE salary > 50000

-- @id d6a152f34e7c class=select expected=accept source=tests/Autheris.Tests.Unit/Masking/AdvancedMaskingRuleTests.cs
SELECT id, name, salary FROM employees WHERE id = 123 ORDER BY name

-- @id 528d20c5c47c class=select expected=accept source=tests/Autheris.Tests.Unit/Mcp/McpHybridToolsTests.cs
SELECT id, mask(email) FROM sales.public.customers

-- @id cbae50fe45c3 class=select expected=accept source=tests/Autheris.Tests.Unit/Mcp/McpHybridToolsTests.cs
SELECT id, email FROM sales.public.customers

-- @id 55c442ab6966 class=select expected=accept source=tests/Autheris.Tests.Unit/ProcedureEndpointTests.cs
SELECT * FROM TABLE(api.ufn_GetCraneTelemetry(:crane_id, :tenant_id))

-- @id 87df8be46708 class=select expected=accept source=tests/Autheris.Tests.Unit/ProcedureEndpointTests.cs
SELECT * FROM api.ufn_GetCraneTelemetry(@crane_id, DEFAULT, @tenant_id)

-- @id f0a9c6efbf7f class=select expected=accept source=tests/Autheris.Tests.Unit/ProcedureEndpointTests.cs
SELECT * FROM api.fn_Telemetry(@tenant_id, @region)

-- @id 1d9cc5317cb4 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT * FROM "public.employees"

-- @id 241a32fea502 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT id FROM employees FOR VERSION AS OF 1

-- @id 343efbe59ea3 class=select expected=reject source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT id FROM employees -- comment

-- @id 41db4630fc1e class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT ts_stat('SELECT 1'), id FROM employees

-- @id 4e4fff613bef class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT region, count(*), max(id), round(avg(id), 2), lower(name), coalesce(region, 'n/a'),

-- @id 538532c3cb55 class=select expected=reject source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT (name).f(1) FROM employees

-- @id 54bd2cee63c1 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT e.id, o.id FROM employees e JOIN orders o ON e.id = o.id

-- @id 5b477a10fa29 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT $$x$$, id FROM orders

-- @id 6797143a4766 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT len(name), isnull(region, 'n/a') FROM orders

-- @id 8c86d4030f99 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT load_extension('x'), id FROM employees

-- @id 8dfe6d270026 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT len(name) FROM orders

-- @id 9b02f3861d7b class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT setval('s', 1), id FROM employees

-- @id b30687f06b75 class=select expected=reject source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT mytype::f(1) FROM employees

-- @id bc123ef8d7a6 class=select expected=reject source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT name[1].f() FROM employees

-- @id c3edab642599 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT md5(name) FROM employees

-- @id ec71bba276d8 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT id FROM employees FOR TIMESTAMP AS OF TIMESTAMP '2026-01-01 00:00:00'

-- @id ed8f55ed7261 class=select expected=accept source=tests/Autheris.Tests.Unit/Round4ParserGatewayTests.cs
SELECT id FROM orders LIMIT 5

-- @id 99de70515144 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/ArrowExportRebacBodyTests.cs
SELECT id FROM sales.public.orders

-- @id 1ab99ac52adc class=select expected=accept source=tests/Autheris.Tests.Unit/Security/ArrowFlightSqlSecurityTests.cs
SELECT * FROM customers

-- @id 44f135f3a132 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/ArrowFlightSqlSecurityTests.cs
SELECT * FROM secrets

-- @id 1b0da4ef59ae class=select expected=accept source=tests/Autheris.Tests.Unit/Security/AstCompilerHmacSql4Tests.cs
SELECT id, email FROM employees

-- @id 02c1d6b64ee2 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/AuditAnchorHardeningTests.cs
SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES

-- @id 1ff099fdd1b3 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/AuditArchitectureHardeningAu01To19Tests.cs
SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE EVENT_TYPE = 'CONSENT_CREATED'

-- @id 6ebb60b8ee74 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/AuditArchitectureHardeningAu01To19Tests.cs
SELECT id, name, salary FROM hr.salaries WHERE salary > 50000 AND department = 'Engineering' AND active = 1

-- @id 756ea7a1be10 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/AuditArchitectureHardeningAu01To19Tests.cs
SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE EVENT_TYPE = 'CONSENT_FAILED_ATTEMPT'

-- @id 2b030a641d6d class=select expected=reject source=tests/Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs
SELECT o.id, s.tracking FROM crm.public.orders o CROSS JOIN logistics.v1.shipments s

-- @id 4e332f8d4078 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs
SELECT o.id, read_csv('secret.csv') FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.id = s.id

-- @id 86cf7a11ba28 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs
SELECT c.id, e.credit FROM crm_api.public.customers c JOIN erp.public.customers e ON c.id = e.id

-- @id 9db5d58888f5 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs
SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.shipment_id = s.id WHERE o.secret_note = 'classified'

-- @id cc6375031982 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs
SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.id = s.id

-- @id d642ceba5ea7 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs
SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.shipment_id = s.id

-- @id fc4732910341 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/CrossSourcePlannerTests.cs
SELECT o.id, s.tracking FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.secret_note = s.id

-- @id 396d6d192735 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbInterruptSql29Tests.cs
SELECT sum(a.id * b.id + c.id * d.id) FROM nums a, nums b, nums c, nums d

-- @id 411372291e79 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbInterruptSql29Tests.cs
WITH RECURSIVE x(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM x) SELECT count(*) FROM x, nums

-- @id dc8b8c5d123b class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbInterruptSql29Tests.cs
SELECT n.id, r.range FROM nums n, range(1000000000) r

-- @id e9c4fb06e729 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbInterruptSql29Tests.cs
SELECT * FROM nums, generate_series(1, 100000000)

-- @id 03f6f6c573f3 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT * FROM read_csv('/etc/passwd')

-- @id 1931f2f2d218 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT * FROM read_blob('.tmp/*')

-- @id 1a955cc7384d class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT * FROM glob('*')

-- @id 2409e66277f3 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT id, note, closed_amount FROM deals ORDER BY id

-- @id 2933eaa05e91 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT 
                c.company_name, 
                COUNT(i.invoice_id) as invoice_count, 
                SUM(i.total) as total_spent
            FROM customers c
            JOIN invoices i ON c.customer_id = i.customer_id
            GROUP BY c.company_name
            ORDER BY total_spent DESC

-- @id 32facf3bf257 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT * FROM transactions

-- @id 640b7bd7e6e9 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT * FROM duckdb_settings()

-- @id 7571b3f72965 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT COUNT(*) FROM sales.orders

-- @id 9256ebbd1323 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT secret_token FROM transactions WHERE id = 1

-- @id a14cc7f73b23 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT $$some_text$$

-- @id a82418a16c77 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT count(*) FROM orders

-- @id a9c6ab3b2034 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT 
                id, 
                email, 
                revenue, 
                RANK() OVER (ORDER BY revenue DESC) as rev_rank,
                SUM(revenue) OVER () as total_rev
            FROM customers 
            ORDER BY rev_rank ASC

-- @id b6f66cdd9fcd class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT lpad('A', 2000, 'A') AS big_col

-- @id caf0bf768b12 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapSecurityTests.cs
SELECT * FROM sniff_csv('/tmp/foo')

-- @id 73279bdfd93f class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapValidatorG1Tests.cs
SELECT 1; /* comment */ DROP TABLE users

-- @id 742c3df9bd43 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapValidatorG1Tests.cs
SELECT * FROM generate_series  (1, 100000000)

-- @id 87b85939fd8b class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapValidatorG1Tests.cs
SELECT 'a\' ; SELECT 2

-- @id c2f598d9d3d6 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapValidatorG1Tests.cs
SELECT * FROM range (1000000000)

-- @id da8ab9499ab5 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/DuckDbOlapValidatorG1Tests.cs
SELECT 1 /* Inline comment */

-- @id e0dd78bb1327 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/DuckDbOlapValidatorG1Tests.cs
SELECT 1; -- comment
SELECT 2

-- @id 0442f3d34a0a class=select expected=accept source=tests/Autheris.Tests.Unit/Security/FlightSqlGovernedExecutionTests.cs
SELECT * FROM hr.salaries

-- @id 0bec9906851b class=select expected=accept source=tests/Autheris.Tests.Unit/Security/FlightSqlGovernedExecutionTests.cs
SELECT id, name FROM t

-- @id 28aefd72ef72 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/FlightSqlGovernedExecutionTests.cs
SELECT id FROM t LIMIT 1

-- @id 40d0a58d6f32 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/FlightSqlGovernedExecutionTests.cs
SELECT id FROM t

-- @id f212c4045d75 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/FlightSqlGovernedExecutionTests.cs
SELECT id, name FROM lwetem_prod.md.crane LIMIT 5

-- @id 28a05db6765e class=select expected=accept source=tests/Autheris.Tests.Unit/Security/PreStagingMaskingTests.cs
SELECT id, salary FROM s0

-- @id f690c44f7958 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/ProcedureLobBudgetRSql8Tests.cs
SELECT id, txt, bin FROM lob

-- @id 0e76f7d80ea4 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/RepeatedReviewRound2Tests.cs
SELECT * FROM "Orders" a JOIN orders b ON a.id = b.id

-- @id 3969a478400b class=select expected=accept source=tests/Autheris.Tests.Unit/Security/RepeatedReviewRound3Tests.cs
SELECT 1; DROP TABLE orders;

-- @id 6750faece3ed class=select expected=accept source=tests/Autheris.Tests.Unit/Security/RepeatedReviewRound3Tests.cs
SELECT * FROM range(1000000000)

-- @id ec7b9c91d951 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/RepeatedReviewRound3Tests.cs
SELECT id, ssn FROM hr.employees

-- @id d14a5f222d7a class=select expected=accept source=tests/Autheris.Tests.Unit/Security/Sql6JoinGuardrailTests.cs
SELECT a.id FROM dbo.table_a a JOIN dbo.table_b b ON a.user_id = b.user_id

-- @id f456a3258a7f class=select expected=accept source=tests/Autheris.Tests.Unit/Security/Sql6JoinGuardrailTests.cs
SELECT b.id FROM dbo.table_b b JOIN dbo.table_c c ON b.user_id = c.user_id, dbo.table_a a WHERE a.id = 1

-- @id 3e101030ffb6 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/SqlProcedureRowScopeResolverTests.cs
SELECT [serial_number] FROM [main].[crane] AS [autheris_target] WHERE [tenant_id] = @rs_tenant AND (f) AND (([serial_number] = @rs_0_0) OR ([serial_number] = @rs_1_0))

-- @id 9780a847178f class=select expected=accept source=tests/Autheris.Tests.Unit/Security/SqlProcedureRowScopeResolverTests.cs
SELECT "serial_number" FROM "crane" AS "autheris_target" WHERE "tenant_id" = @rs_tenant AND (f) AND (("serial_number" = @rs_0_0) OR ("serial_number" = @rs_1_0))

-- @id 97f061df5155 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/SqlProcedureRowScopeResolverTests.cs
SELECT "serial_number" FROM "main"."crane" AS "autheris_target" WHERE "tenant_id" = @rs_tenant AND (f) AND (("serial_number" = @rs_0_0) OR ("serial_number" = @rs_1_0))

-- @id 2a696085640b class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs
SELECT a.id FROM dbo.Orders a JOIN dbo.Orders b ON a.id = b.id

-- @id 2ae04143a5d5 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs
SELECT a.id FROM cat1.dbo.Orders a JOIN cat2.dbo.Orders b ON a.id = b.id

-- @id 39a25e429829 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs
SELECT a.id FROM dbo.Orders a JOIN dbo.orders b ON a.id = b.id

-- @id 5c8be72fe3e4 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs
SELECT id FROM default.dbo.Orders

-- @id 5f5523138bed class=select expected=reject source=tests/Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs
SELECT id FROM otherdb.dbo.Orders

-- @id 94457ac0df14 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/WebSqlCaseCollisionSql2Tests.cs
SELECT a.id FROM orders a JOIN "Orders" b ON a.id = b.id

-- @id 16d1e99c1c58 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT @@version, @other FROM t WHERE x = __param_crane_type

-- @id 20ba4bda6a64 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT @@version, @other FROM t WHERE x = @crane_type

-- @id 4279b96b2bf6 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT __param_crane_type FROM t WHERE x = @crane_type

-- @id 43277b8863a1 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT a FROM t WHERE crane_type = @crane_type LIMIT 10

-- @id 466e62631642 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT 'it''s @crane_type', "@crane_type" FROM t WHERE x = __param_crane_type

-- @id 617487e17f5b class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT a FROM t WHERE crane_type = @crane_type

-- @id 632073935e4c class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT 'it''s @crane_type', "@crane_type" FROM t WHERE x = @crane_type

-- @id 67cbf715c709 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT a FROM t WHERE crane_type = __param_crane_type

-- @id 7c97cd215e7a class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT @x FROM t

-- @id e37641467f48 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlClientParameterTests.cs
SELECT a FROM t WHERE crane_type = "__param_crane_type" LIMIT 10

-- @id 6b27c281e31f class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlCrossSourceGuardTests.cs
SELECT id, name FROM sales.public.http_table

-- @id b93577d5425d class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlCrossSourceGuardTests.cs
SELECT id, name FROM sales.public.api_data

-- @id 620108f92885 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlHeterogeneousJoinTests.cs
SELECT id, carrier FROM logistics.v1.shipments

-- @id c98bda9f09c8 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlHeterogeneousJoinTests.cs
SELECT o.id, o.amount, s.carrier, s.status

-- @id d6b6ac415bfc class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlHeterogeneousJoinTests.cs
SELECT o.id, s.carrier FROM crm.public.orders o JOIN logistics.v1.shipments s ON o.tracking_id = s.id

-- @id 022009d0d4b9 class=select expected=reject source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id, row_number() OVER (ORDER BY salary) FROM dbo.people

-- @id 07e7c8afbeb3 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id FROM dbo.people WHERE email LIKE 'a%'

-- @id 2161629d22a7 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT c.id FROM dbo.contacts c JOIN dbo.people p ON c.id = p.id WHERE c.email = 'anna@example.com' ORDER BY c.salary

-- @id 4485c520a39b class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id, email, salary FROM dbo.people

-- @id 47e5009af49b class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id FROM dbo.people WHERE id IN (SELECT id FROM dbo.people WHERE salary > 1)

-- @id 55fe3239c351 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT c.id FROM dbo.contacts c JOIN dbo.people p ON c.id = p.id WHERE p.email = 'anna@example.com'

-- @id 5d13c9a78b02 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id, email FROM dbo.people WHERE id > 10 ORDER BY id

-- @id 5f36ef7b4006 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id FROM dbo.people p ORDER BY p.salary DESC

-- @id 64a85edab32c class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id, COUNT(*) FROM dbo.people GROUP BY id HAVING MAX(salary) > 1

-- @id c4a57e6c6d3a class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id FROM dbo.people WHERE id = 1 AND salary IS NOT NULL

-- @id c76f9bd9e339 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id FROM dbo.people WHERE email = 'anna@example.com'

-- @id ca59296b95d4 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id, COUNT(*) FROM dbo.people GROUP BY id HAVING COUNT(*) > 1

-- @id d3179c0b1a80 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id FROM dbo.people ORDER BY email

-- @id ee1a15e46582 class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT COUNT(*) FILTER (WHERE email = 'anna@example.com') FROM dbo.people

-- @id ef2f02bbeedf class=select expected=accept source=tests/Autheris.Tests.Unit/Security/WebSqlRedactedPredicateGuardrailTests.cs
SELECT id FROM dbo.people p WHERE p.salary > 5000

-- @id 199e2329bb81 class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002ExtensionsCoreTests.cs
SELECT 1
-- @datasource: other_db

-- @id aa8bec0a07c1 class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002ExtensionsCoreTests.cs
SELECT "customer_id", "segment"

-- @id 8e46e00993b9 class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002InfraTests.cs
SELECT seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid DESC LIMIT 1

-- @id f53c965f218f class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002InfraTests.cs
SELECT seq FROM AUDIT_LOG_ENTRIES ORDER BY rowid

-- @id 01635629ed3c class=select expected=reject source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT query_to_xml('SELECT * FROM hr.salaries', true, false, ''), id FROM employees

-- @id 0a641f2db62d class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT id FROM inactive_orders

-- @id 1a0e77c3216b class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT id, ssn FROM employees

-- @id 1e441336cae4 class=select expected=reject source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT id, '__gql_mk0' FROM employees

-- @id 760d5a56c85d class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT id, name FROM public.employees

-- @id cb7bbe8adfae class=select expected=reject source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT * FROM hr_salaries

-- @id cc751fa17b93 class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT id, name FROM employees

-- @id e8e1f01209ac class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReview20261002WebSqlTests.cs
SELECT query_to_xml('SELECT * FROM hr.salaries', true, false, '')

-- @id 50a4d03b48d4 class=select expected=accept source=tests/Autheris.Tests.Unit/SecurityReviewG3EndpointAuthzTests.cs
SELECT secret FROM hr.salaries

-- @id a8b6856eaf8e class=select expected=reject source=tests/Autheris.Tests.Unit/Services/SyntheticDataAndExceptionHandlingTests.cs
SELECT id, name FROM dbo.customers

-- @id 0942d51d3edb class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstCompilerDuckDbExecutionTests.cs
WITH orders AS (SELECT id FROM orders) SELECT id FROM orders

-- @id 1cd80fae1f33 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstCompilerDuckDbExecutionTests.cs
WITH orders AS (SELECT id FROM entitlements) SELECT id FROM orders

-- @id d19fa052093f class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstCompilerDuckDbExecutionTests.cs
SELECT count(*) FROM range(10) AS t(x) WHERE x IN (

-- @id 6c6a512ad4ae class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstCompilerWebSqlTests.cs
SELECT dept, SUM(amount) FROM orders GROUP BY AUTO

-- @id 6caa75363ab6 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstCompilerWebSqlTests.cs
SELECT id, amount FROM orders

-- @id c1a470b2ec6d class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstCompilerWebSqlTests.cs
SELECT dept, COUNT(*) AS n, SUM(COALESCE(amount, 0)) FROM orders GROUP BY dept HAVING COUNT(*) > 1

-- @id f54ad0c7e79e class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstCompilerWebSqlTests.cs
SELECT id FROM orders WHERE dept = @d AND amount > @m

-- @id 05e914549604 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT COUNT(*) FROM orders WHERE NULL IS NOT DISTINCT FROM NULL

-- @id 1912588dfbca class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT substring(created_at FROM 9) FROM orders

-- @id 1c07e0bfd9c9 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT trim(TRAILING '5' FROM created_at) FROM orders

-- @id 474e2dafa33e class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT COUNT(*) FROM orders WHERE created_at IS NOT DISTINCT FROM '2024-03-15'

-- @id 4d04ab4a0da4 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT EXTRACT(DAY_OF_YEAR FROM created_at) FROM orders

-- @id 5bdbf61f218c class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT trim(LEADING '20' FROM created_at) FROM orders

-- @id 737004374b22 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT position('-' IN created_at) FROM orders

-- @id 915a2d3d62a1 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT substring(created_at FROM 6 FOR 2) FROM orders

-- @id 97fa2ae2b1dc class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT COUNT(*) FROM orders WHERE created_at IS DISTINCT FROM NULL

-- @id a62b6ec88db0 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT EXTRACT(DAY FROM created_at) FROM orders

-- @id aa57f8ecde70 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT position('x' IN created_at) FROM orders

-- @id acd3594210fc class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT EXTRACT(MONTH FROM created_at) FROM orders

-- @id add89ea89c49 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT EXTRACT(DOW FROM created_at) FROM orders

-- @id c9fffa868d38 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT EXTRACT(QUARTER FROM created_at) FROM orders

-- @id caf6d4717828 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT EXTRACT(YEAR FROM created_at) FROM orders

-- @id fb86d3d6274a class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/AstSqliteExecutionTests.cs
SELECT trim(BOTH '2' FROM created_at) FROM orders

-- @id 130b4a95675e class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/BoundedPlanCacheTests.cs
SELECT id FROM orders;

-- @id 17db4fd369ed class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/BoundedPlanCacheTests.cs
SELECT 1;

-- @id 26e7e05427bc class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/BoundedPlanCacheTests.cs
SELECT * FROM users

-- @id 4d5a4229ce2f class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/BoundedPlanCacheTests.cs
SELECT * FROM users WHERE dept = 'sales';

-- @id 165a22d8eabe class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/DuckDbBindingProbeTests.cs
SELECT $1

-- @id 1d42386ad26a class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/DuckDbBindingProbeTests.cs
SELECT $1 || $1

-- @id 2e7d3c85602f class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/DuckDbBindingProbeTests.cs
SELECT $1 || $2

-- @id 90a63837f1e3 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/DuckDbBindingProbeTests.cs
SELECT length(CAST($1 AS VARCHAR))

-- @id e95bba47b6bc class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/DuckDbBindingProbeTests.cs
SELECT count(*) FROM (SELECT 1 UNION ALL SELECT 2 UNION ALL SELECT 3) LIMIT $1

-- @id 3e4e368895aa class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT dept, MAX(amount) FROM orders GROUP BY dept HAVING MAX(amount) > 100

-- @id 5edf1c812500 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT COUNT(id) FILTER (WHERE amount > 100), COUNT(*) FILTER (WHERE dept = 'Sales') FROM orders

-- @id 5f7925cd015b class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT dept, COUNT(*) FROM orders GROUP BY dept

-- @id 62e625a51213 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
WITH big AS (SELECT * FROM orders WHERE amount > 100) SELECT id FROM big

-- @id 656aa92b4b6f class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id, CAST(amount AS integer) FROM orders

-- @id 66781ab8da48 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id, row_number() OVER (PARTITION BY dept ORDER BY amount DESC, id) FROM orders

-- @id 6d8066e0a3b2 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT o.id, c.name FROM orders o JOIN customers c ON o.customer_id = c.id

-- @id 717c5dfbf756 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id, CASE WHEN amount > 100 THEN 'big' ELSE 'small' END AS size FROM orders

-- @id 766416145830 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id, SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders

-- @id 7f2b8737da4c class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT dept, SUM(amount) FROM orders GROUP BY dept

-- @id a83278b1df90 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT DISTINCT dept FROM orders ORDER BY dept

-- @id aa63c022040f class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id, COALESCE(dept, 'none') FROM orders

-- @id b1b0e073827d class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT dept FROM orders UNION SELECT name FROM customers

-- @id b258a3c028e5 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id FROM orders WHERE dept LIKE 'S%'

-- @id b5946ca1a553 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT * FROM orders WHERE amount > 100

-- @id d508d3db015b class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id FROM orders WHERE amount BETWEEN 50 AND 150

-- @id d8f3400b08bf class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id FROM orders WHERE dept IN (SELECT dept FROM orders WHERE amount > 200)

-- @id df6e91065cc0 class=select expected=reject source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id, dept FROM orders

-- @id ee94b34a8de4 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id FROM orders ORDER BY amount DESC, id LIMIT 3

-- @id f96abb04d2df class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/EngineExecutionDifferentialTests.cs
SELECT id FROM orders o WHERE NOT EXISTS (SELECT 1 FROM customers c WHERE c.id = o.customer_id)

-- @id 662327cdb1b0 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/ExactTruncationTests.cs
SELECT id FROM lwetem_prod.md.crane

-- @id a3601f6e467a class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/ExactTruncationTests.cs
SELECT id FROM lwetem_prod.md.crane LIMIT 60000

-- @id ff16786f072f class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/ExactTruncationTests.cs
SELECT id FROM lwetem_prod.md.crane LIMIT 5

-- @id 2878f46b0d66 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionMultiSchemaShortNameTests.cs
SELECT id, name FROM crm.leads

-- @id f982177e0824 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionMultiSchemaShortNameTests.cs
SELECT p.id, a.id FROM public.orders p JOIN archive.orders a ON p.id = a.id

-- @id 07b8b9ba59cf class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionParameterSecurityTests.cs
SELECT id FROM orders WHERE id = @p_rls_0

-- @id ab94185ed850 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionParameterSecurityTests.cs
SELECT id FROM orders WHERE id = @__gql_internal

-- @id 9fbb99f7808e class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/GovernedSqlExecutionTenantColumnTests.cs
SELECT id, amount FROM public.invoices

-- @id 593e27847e65 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/OracleBindByNameWrapperTests.cs
SELECT 1 FROM DUAL

-- @id e9bb11e8eb8d class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/SqlRewritePipelineTests.cs
SELECT a, b FROM t WHERE tenant_id = 42

-- @id 40483905a81e class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/TrinoColumnTypeTests.cs
SELECT COUNT(*), 1.5 AS x, 'a' AS x

-- @id 828b13409ef7 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/TrinoColumnTypeTests.cs
SELECT COUNT(*), created FROM t

-- @id 35c8c2e15596 class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/TypeSafeMaskingExpressionTests.cs
SELECT id, secret_val FROM test_items

-- @id 5e2c6759bdaa class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/WebSqlTwoPartNameDataSourceTests.cs
SELECT id, name FROM md.crane

-- @id 8e6ea6bbc54b class=select expected=accept source=tests/Autheris.Tests.Unit/Sql/WebSqlTwoPartNameDataSourceTests.cs
SELECT id, name FROM lwetem_prod.md.crane

-- @id 91b359fc7d5a class=select expected=reject source=tests/Autheris.Tests.Unit/SqlEndpointDynamicRegistrationTests.cs
SELECT id, username FROM users

-- @id ed5da3ae24ed class=select expected=accept source=tests/Autheris.Tests.Unit/SqlEndpointDynamicRegistrationTests.cs
SELECT id, username FROM users WHERE active = true

-- @id 7ffcf4ce407b class=select expected=accept source=tests/Autheris.Tests.Unit/SqlEndpointsTests.cs
SELECT month, total_sales FROM analytics.monthly_sales WHERE year = @year

-- @id a1b333d1a08a class=select expected=accept source=tests/Autheris.Tests.Unit/SqlEndpointsTests.cs
SELECT 1 WHERE 1 = @req

-- @id 084c62cf76e7 class=select expected=accept source=tests/Autheris.Tests.Unit/SqlParameterPagingTests.cs
SELECT id FROM t WHERE note = 'OFFSET @x' AND a = @a

-- @id 0a9066a817a1 class=select expected=accept source=tests/Autheris.Tests.Unit/SqlParameterPagingTests.cs
SELECT id FROM t WHERE a = @a ORDER BY id OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY

-- @id 8235da2d307d class=select expected=accept source=tests/Autheris.Tests.Unit/SqlParameterPagingTests.cs
SELECT id FROM t ORDER BY id OFFSET @o ROW FETCH FIRST @n ROW ONLY

-- @id e4daa983e071 class=select expected=accept source=tests/Autheris.Tests.Unit/SqlParameterPagingTests.cs
select id from t order by id offset @o rows

-- @id eb075ccec684 class=select expected=accept source=tests/Autheris.Tests.Unit/SqlParameterPagingTests.cs
SELECT id FROM t LIMIT @n

-- @id f5e674b33b47 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/FederatedVirtualFilterDuckDbTests.cs
SELECT id, name FROM web_crm.public.customers

-- @id 26d6608112ed class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/CrossSourcePlannerVirtualFilterTests.cs
SELECT c.id FROM crm_api.public.customers c JOIN recent r ON r.customer_id = c.id

-- @id 4bb5d05ffaa4 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/CrossSourcePlannerVirtualFilterTests.cs
WITH recent AS (SELECT id, customer_id FROM erp.public.orders)

-- @id 7035ac677c4a class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/CrossSourcePlannerVirtualFilterTests.cs
SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id

-- @id 7ee07f4f7111 class=select expected=reject source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/CrossSourcePlannerVirtualFilterTests.cs
SELECT c.id FROM crm_api.public.customers c JOIN unknown_src.public.ghost g ON g.id = c.id

-- @id a60cbbbef6a0 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/CrossSourcePlannerVirtualFilterTests.cs
SELECT a.id, b.id FROM crm_api.public.customers a JOIN crm_api.public.customers b ON a.id = b.id

-- @id 5f79ccfb147b class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedDecisionKeyTests.cs
SELECT c.id, c.status, e.credit FROM crm_api.public.customers c JOIN erp.public.customers e ON c.id = e.id

-- @id bd797a59cc0e class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedDecisionKeyTests.cs
SELECT c.id, c.status, e.credit FROM erp.public.customers e JOIN crm_api.public.customers c ON c.id = e.id

-- @id 88b807384474 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedStagingRowFilterRegressionTests.cs
SELECT id, status FROM crm_api.public.tickets

-- @id dc5abf6d8d4d class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedStagingRowFilterRegressionTests.cs
SELECT t.id, o.id FROM crm_api.public.tickets t JOIN erp.public.orders o ON o.ticket_id = t.id

-- @id fd07b36b9bb9 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterAuditTests.cs
SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id

-- @id 07a7c1a52208 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
SELECT COUNT(*) AS n FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id

-- @id 24e7c2a473c0 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
SELECT id FROM crm_api.public.customers UNION ALL SELECT id FROM erp.public.orders

-- @id 49a96723024e class=select expected=reject source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
SELECT o.id FROM erp.public.orders o JOIN crm_api.public.customers c ON o.customer_id = c.id

-- @id 53ee6ba78f76 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
SELECT c.* FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id

-- @id 69277250bf1a class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
SELECT a.id FROM crm_api.public.customers a JOIN crm_api.public.customers b ON a.id = b.id JOIN erp.public.orders o ON o.customer_id = a.id

-- @id b6eb86442e3f class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
SELECT x.id FROM (SELECT id FROM crm_api.public.customers) x UNION ALL SELECT id FROM erp.public.orders

-- @id f1556d5f8db7 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
SELECT c.id, c.name FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id

-- @id f2b0ce15b2ca class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterEnforcementTests.cs
WITH c AS (SELECT id FROM crm_api.public.customers) SELECT c.id FROM c UNION ALL SELECT id FROM erp.public.orders

-- @id 5ee13db94669 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterNoPlannerTests.cs
SELECT c.id, o.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 999

-- @id 4a9d7964a207 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT o.id FROM erp.public.orders o WHERE o.customer_id IN (SELECT id FROM crm_api.public.customers WHERE id = '2') OR o.id = '10'

-- @id 4c9de0ed4c38 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT c.id, c.name

-- @id 815af51c658a class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT COUNT(*) AS n

-- @id 8867d3341044 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT COUNT(*) AS n FROM crm_api.public.customers c WHERE c.id = '2'

-- @id a1f1acf85184 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT c.id FROM crm_api.public.customers c WHERE c.id = '1'

-- @id a64f0bd9745d class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT c.id

-- @id a90a65b0afb4 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT o.id, c.id AS cid FROM erp.public.orders o LEFT JOIN crm_api.public.customers c ON c.id = o.customer_id WHERE c.id = '2'

-- @id b1628116ebf5 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT o.id FROM erp.public.orders o LEFT JOIN crm_api.public.customers c ON c.id = o.customer_id AND c.id = '2' ORDER BY o.id

-- @id b9f46524ecb0 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT c.id FROM crm_api.public.customers c WHERE c.id IN ('1', '2', '3') AND c.account_id IN ('X', 'Y')

-- @id cbba63e09125 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT c.id FROM crm_api.public.customers c WHERE c.id = 48

-- @id e1db24324c58 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
WITH c AS (SELECT id FROM crm_api.public.customers WHERE id = '2') SELECT c.id FROM c UNION ALL SELECT id FROM erp.public.orders

-- @id e44f3822e5e8 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT id FROM crm_api.public.customers WHERE id = '2' UNION ALL SELECT id FROM erp.public.orders

-- @id fb9c0264e3fe class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/FederatedVirtualFilterPointLookupTests.cs
SELECT c.id FROM crm_api.public.customers c WHERE c.id IN ('2', '4')

-- @id 0ae1ceb2f9ea class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/SqlVirtualFilterKeySetProviderTests.cs
SELECT DISTINCT client_id FROM air1 WHERE client_id IS NOT NULL

-- @id 0830d396c761 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/StructuredFilterKeyQueryBuilderTests.cs
SELECT DISTINCT "client"."client_id" AS "client_id" FROM "client" AS "client"

-- @id 5e57ef501b06 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/StructuredFilterKeyQueryBuilderTests.cs
SELECT DISTINCT "a"."client_id" AS "client_id", "a"."region" AS "region"

-- @id df8aa45f3d0c class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/StructuredFilterKeyQueryBuilderTests.cs
SELECT DISTINCT TOP (50001) [client].[client_id] AS [client_id] FROM [conf].[client] AS [client]

-- @id 168a00a974de class=select expected=reject source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterFederationPlannerTests.cs
SELECT client_id FROM erp.auth.client

-- @id 037789b076b9 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterPointLookupExtractorTests.cs
SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 1 AND EXISTS (SELECT 1 FROM erp.public.orders)

-- @id 0a2b82af2c62 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterPointLookupExtractorTests.cs
SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 1 AND o.id IN (SELECT id FROM erp.public.orders)

-- @id 10533c2e184e class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterPointLookupExtractorTests.cs
SELECT a.id FROM crm_api.public.customers a JOIN crm_api.public.customers b ON a.id = b.id JOIN erp.public.orders o ON o.id = a.id WHERE a.id = 1

-- @id 5a44ec80fae2 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterPointLookupExtractorTests.cs
SELECT x.id FROM (SELECT id FROM crm_api.public.customers) x JOIN erp.public.orders o ON o.id = x.id WHERE x.id = 1

-- @id ae6138253773 class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterPointLookupExtractorTests.cs
SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = c.id WHERE c.id = 1 UNION SELECT id FROM erp.public.orders

-- @id bf4ba32fdd1c class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterPointLookupExtractorTests.cs
SELECT c.id FROM crm_api.public.customers c JOIN erp.public.orders o ON o.customer_id = (SELECT MAX(id) FROM erp.public.orders) WHERE c.id = 1

-- @id d0eb452d0f6c class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterPointLookupExtractorTests.cs
WITH x AS (SELECT id FROM erp.public.orders) SELECT c.id FROM crm_api.public.customers c JOIN x ON x.id = c.id WHERE c.id = 1

-- @id e6a41ce7640d class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/Federation/VirtualFilterStrategyMatrixTests.cs
SELECT c.id FROM crm_api.public.customers c

-- @id 46e7224ebd4c class=select expected=accept source=tests/Autheris.Tests.Unit/VirtualFilters/SqlFilterDefinitionTests.cs
select 1 from conf.client client where target.client_id = client.client_id

-- @id 1cdb68818cff class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlDataSourceMappingTests.cs
SELECT id, amount FROM finance.dbo.invoices

-- @id 0e361330c5ca class=select expected=reject source=tests/Autheris.Tests.Unit/WebSqlFilterGuardrailTests.cs
SELECT name, SUM(id) FROM dbo.employees GROUP BY name HAVING salary > 1000

-- @id 559b46b3829d class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlFilterGuardrailTests.cs
SELECT id, name FROM dbo.employees WHERE id = 1

-- @id 5666bff66ef3 class=select expected=reject source=tests/Autheris.Tests.Unit/WebSqlFilterGuardrailTests.cs
SELECT id, name FROM dbo.employees WHERE salary > 50000

-- @id eaf3099efff8 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlFilterGuardrailTests.cs
SELECT id, name FROM dbo.employees WHERE email = 'alice@example.com'

-- @id fb15a07ba148 class=select expected=reject source=tests/Autheris.Tests.Unit/WebSqlFilterGuardrailTests.cs
SELECT id, name FROM dbo.employees WHERE ssn = '123-45-6789'

-- @id 68c5689bbf95 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlLimitAndTruncatedTests.cs
SELECT id, amount FROM sales.dbo.orders LIMIT 25000

-- @id 9d00a53f2fe4 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlLimitAndTruncatedTests.cs
SELECT id, amount FROM sales.dbo.orders

-- @id a225e7f3f170 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlLimitAndTruncatedTests.cs
SELECT id, amount FROM sales.dbo.orders LIMIT 5000

-- @id 25fab446a779 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlMaskingExpressionsTests.cs
SELECT '***' AS email FROM sales.dbo.customers

-- @id 2e08a7969db7 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlMaskingExpressionsTests.cs
SELECT iban FROM sales.dbo.customers

-- @id d16a0178b954 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlMaskingExpressionsTests.cs
SELECT email FROM sales.dbo.customers

-- @id 15b1d5dec2cd class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT * FROM default.dbo.infinite_loop

-- @id 17861f642c47 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT * FROM default.dbo.big_table

-- @id 1d6a12252ca5 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT 3

-- @id 499c1eba51a1 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT id, name FROM default.dbo.users

-- @id 70f2b415a585 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT [id], [name] FROM [dbo].[users]

-- @id 7f152a06eb2b class=select expected=reject source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT secret_iban FROM default.dbo.users

-- @id ce7b2d6c9eef class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT * FROM [dbo].[big_table]

-- @id ebbb5b332060 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlStatementManagerTests.cs
SELECT 2

-- @id 26cb04ddc291 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlTrinoProtocolTests.cs
SELECT 1 AS num

-- @id 487ceae01192 class=select expected=accept source=tests/Autheris.Tests.Unit/WebSqlTrinoProtocolTests.cs
SELECT * FROM large_table

-- @id 10df2228bb72 class=select expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
SELECT * FROM customers LIMIT 10000

-- @id 5780106eb644 class=select expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
SELECT * FROM products ORDER BY price DESC LIMIT 50

-- @id 5a9fc8be185c class=select expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
SELECT * FROM customers LIMIT 25

-- @id 9441cb8f8949 class=select expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
SELECT u.id, u.name, o.total FROM users u JOIN orders o ON u.id = o.user_id WHERE u.active = 1

-- @id b5a3e7832932 class=select expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
SELECT id, email, salary FROM users

-- @id bb69d0e3ba40 class=select expected=accept source=tests/TrinoSqlEngine.Tests/AnalysisAndMaskingTests.cs
WITH cte_summary AS (
                SELECT department_id, COUNT(*) as cnt FROM employees GROUP BY department_id
            ),
            cte_filtered AS (
                SELECT * FROM cte_summary WHERE cnt > 10
            )
            SELECT d.name, f.cnt 
            FROM departments d 
            JOIN cte_filtered f ON d.id = f.department_id

-- @id 0502ab69a4d7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT COUNT(id) FILTER (WHERE amount > 1) FROM t

-- @id 0849f7ba8acd class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT DISTINCT dept FROM t ORDER BY dept DESC NULLS LAST LIMIT 5

-- @id 08703c497092 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT dept, COUNT(id) FROM t GROUP BY dept HAVING COUNT(id) > 1

-- @id 133113b59043 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
WITH x AS (SELECT a FROM t) SELECT a FROM x WHERE EXISTS (SELECT 1 FROM u WHERE u.a = x.a)

-- @id 15dc1cb14250 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT lag(amount) IGNORE NULLS OVER (ORDER BY id) FROM t

-- @id 1d5670c95c2f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT * FROM t PIVOT (SUM(amount) FOR dept IN ('Sales', 'Dev'))

-- @id 270b571afb29 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT array_agg(id ORDER BY amount) FROM t

-- @id 317d384f9b5a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT id FROM t WHERE created_at > DATE '2024-01-01'

-- @id 3dd38ffac88a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT trim(name) FROM t

-- @id 3edefa4856d1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT dept, COUNT(id) FROM t GROUP BY DISTINCT dept

-- @id 44db904b6fb3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT overlay(name PLACING 'x' FROM 2) FROM t

-- @id 4540c65de178 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT id FROM t TABLESAMPLE BERNOULLI (10)

-- @id 50cb4f84e11d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT substring(name FROM 2 FOR 3) FROM t

-- @id 5ad536e2b945 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT dept, COUNT(id) FROM t GROUP BY ROLLUP (dept)

-- @id 75636028421e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT row_number() OVER (PARTITION BY dept) FROM t

-- @id 83001f374a3c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT id FROM t WHERE created_at > TIMESTAMP '2024-01-01 00:00:00'

-- @id 8505d51039e9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT a FROM t UNION ALL SELECT a FROM u

-- @id 8aaa80321408 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT id FROM t TABLESAMPLE SYSTEM (5)

-- @id 8d7d083c1e1a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM t

-- @id 8db5f311a902 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT dept, COUNT(id) FROM t GROUP BY CUBE (dept)

-- @id 8e19f9e69c4f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT id FROM t WHERE created_at > current_date - INTERVAL '1' DAY

-- @id 90c2199643c8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT CASE WHEN a > 1 THEN 'x' ELSE 'y' END FROM t WHERE b IN (SELECT b FROM u) AND c LIKE 'a%' ESCAPE '!'

-- @id 98cc40e3407b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT current_timestamp FROM t

-- @id 9e2ef1f5f25f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT json_value(payload, 'lax $.a') FROM t

-- @id a4d7314e7f92 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT * FROM stock MATCH_RECOGNIZE (MEASURES A.price AS price ONE ROW PER MATCH AFTER MATCH SKIP PAST LAST ROW PATTERN (A+ B+) DEFINE A AS A.price > 10)

-- @id b8cce603f723 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT SUM(amount) OVER w FROM t WINDOW w AS (ORDER BY id)

-- @id c7e854ab8ece class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT position('a' IN name) FROM t

-- @id cc27a2284def class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT dept, COUNT(id) FROM t GROUP BY GROUPING SETS ((dept), ())

-- @id d7f2e24fa572 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT CAST(created_at AS date), EXTRACT(YEAR FROM created_at) FROM t

-- @id e4dffbfb3160 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT listagg(name, ',') WITHIN GROUP (ORDER BY name) FROM t

-- @id f1741e46f999 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT dept, COUNT(id) FROM t GROUP BY AUTO

-- @id f980e451e0fe class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT current_user FROM t

-- @id ff9b53f5334a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstBuilderFailLoudTests.cs
SELECT transform(tags, x -> upper(x)) FROM t

-- @id 1d4b9bbee7e4 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorCteTests.cs
WITH orders AS (SELECT * FROM orders) SELECT * FROM orders

-- @id 2151c8b198aa class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorCteTests.cs
WITH mycte AS (SELECT 1 AS col) SELECT * FROM "MYCTE"

-- @id 2f4779ccdc0f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorCteTests.cs
WITH virtual_table AS (SELECT 1 AS col) SELECT * FROM virtual_table

-- @id 5747e7d132da class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorCteTests.cs
WITH "MyCte" AS (SELECT 1 AS col) SELECT * FROM mycte

-- @id 619f5edd2f44 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorCteTests.cs
WITH orders AS (SELECT 1 AS x) SELECT * FROM public.orders

-- @id d9a276e0e424 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorCteTests.cs
WITH orders AS (SELECT * FROM public.orders) SELECT * FROM orders

-- @id 1c74942ab1ce class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorRlsTests.cs
SELECT o.id, o.total, c.name 
            FROM orders AS o 
            JOIN customers AS c ON o.customer_id = c.id 
            WHERE EXISTS (
                SELECT 1 
                FROM shipments AS s 
                WHERE s.order_id = o.id AND s.status = 'DELIVERED'
            )

-- @id 4c94eee46d3a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorRlsTests.cs
SELECT * FROM "orders"

-- @id af16283dd2c0 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorRlsTests.cs
SELECT * FROM "public"."orders"

-- @id f1214e069d26 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/AstSecurityVisitorRlsTests.cs
SELECT * FROM orders JOIN public_info ON orders.id = public_info.id

-- @id 09e3cd89c4e8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/ComplexTrinoBenchmarkQueriesTests.cs
WITH "dept_summary" AS (

-- @id 2258a1bd880d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/ComplexTrinoBenchmarkQueriesTests.cs
SELECT o.id AS order_id, 
                   c.name AS customer_name, 
                   s.status AS shipment_status, 
                   f.product_name, 
                   f.price
            FROM orders AS o
            JOIN customers AS c ON o.customer_id = c.id
            LEFT JOIN shipments AS s ON s.order_id = o.id
            LEFT JOIN finance_items AS f ON f.parent_id = o.id
            WHERE o.total_amount > 100
              AND (s.status IS NULL OR s.status <> 'CANCELLED')
            ORDER BY o.id ASC, f.price DESC

-- @id 359bbea366e1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/ComplexTrinoBenchmarkQueriesTests.cs
WITH [dept_summary] AS (

-- @id 5b5e88580631 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/ComplexTrinoBenchmarkQueriesTests.cs
SELECT c.id, c.name, c.company,
                   (
                       SELECT SUM(o.total_amount) 
                       FROM orders AS o 
                       WHERE o.customer_id = c.id
                   ) AS total_revenue
            FROM customers AS c
            WHERE c.id IN (
                SELECT o.customer_id
                FROM orders AS o
                GROUP BY o.customer_id
                HAVING COUNT(o.id) >= 3
            )
            ORDER BY total_revenue DESC

-- @id 9d5448d1c248 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/ComplexTrinoBenchmarkQueriesTests.cs
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

-- @id c36feedc0f24 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/ComplexTrinoBenchmarkQueriesTests.cs
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

-- @id d50847660dc9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/ComplexTrinoBenchmarkQueriesTests.cs
WITH dept_summary AS (
                SELECT department, COUNT(*) AS invoice_count, AVG(amount) AS avg_amount
                FROM invoices
                GROUP BY department
                HAVING COUNT(*) > 5
            ),
            high_value_invoices AS (
                SELECT i.id, i.department, i.vendor, i.amount
                FROM invoices AS i
                JOIN dept_summary AS d ON i.department = d.department
                WHERE i.amount > d.avg_amount
            )
            SELECT h.department, h.vendor, h.amount
            FROM high_value_invoices AS h
            ORDER BY h.amount DESC
            OFFSET 0 LIMIT 10

-- @id 595d449b9e9f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/AnalyticalDialectTests.cs
SELECT user, account, date FROM orders WHERE role = 'admin'

-- @id 95ad8c0a8fed class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/OracleDialectTests.cs
SELECT id, name FROM orders ORDER BY id OFFSET 5 LIMIT 10

-- @id 4066cb78c3f1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/PostgreSqlDialectTests.cs
SELECT "dbo"."invoices"."id"

-- @id 00f1781ce46f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/SqlServerDialectTests.cs
SELECT [dbo].[invoices].[id]

-- @id 48d61cc64125 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/SqlServerDialectTests.cs
SELECT 1 FROM

-- @id dc42f0927dbd class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/SqlServerDialectTests.cs
WITH [cte] AS (

-- @id 31dbb2fc27d5 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/DialectGenerators/SqliteDialectTests.cs
SELECT "main"."invoices"."id"

-- @id 46576fe0ead5 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/FunctionCallEmissionTests.cs
SELECT COUNT(DISTINCT dept) FROM orders

-- @id 7c8935f76dfd class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/FunctionCallEmissionTests.cs
SELECT CAST(created_at AS date) AS d, COUNT(*) AS n FROM orders GROUP BY CAST(created_at AS date)

-- @id 96beb04b5ff7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/FunctionCallEmissionTests.cs
SELECT COALESCE(amount, 0), nullif(dept, 'x'), upper(dept) FROM orders

-- @id a41b6e2c9dc4 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/FunctionCallEmissionTests.cs
SELECT "lower"(dept) FROM orders

-- @id ae64792c23f5 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/FunctionCallEmissionTests.cs
SELECT SUM(amount), COALESCE(dept, 'none') FROM orders

-- @id d06ea783f792 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/FunctionCallEmissionTests.cs
SELECT dept, COUNT(*) AS n FROM orders GROUP BY dept HAVING COUNT(*) > 5

-- @id 004cac4e13cd class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT dept, SUM(amount) FROM orders GROUP BY ROLLUP (dept)

-- @id 08f4a63680c3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT dept, region, SUM(amount) FROM orders GROUP BY CUBE (dept, region)

-- @id 1ca71875e581 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT dept, SUM(amount) FROM orders GROUP BY CUBE (dept)

-- @id 46e67a026224 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT SUM(amount) FROM orders GROUP BY ROLLUP ((dept, region), id)

-- @id 772056d96ac3 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT grouping(dept, region), SUM(amount) FROM orders GROUP BY CUBE (dept, region)

-- @id 7a9a11c01609 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT dept, grouping(dept), SUM(amount) FROM orders GROUP BY ROLLUP (dept)

-- @id 920ffa8027b3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT region, dept, SUM(amount) FROM orders GROUP BY region, ROLLUP (dept)

-- @id c90f82789f6f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT dept, SUM(amount) FROM orders GROUP BY GROUPING SETS ((dept), ())

-- @id cde10d299b53 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/GroupingSetsEmissionTests.cs
SELECT dept, SUM(amount) FROM orders GROUP BY DISTINCT ROLLUP (dept)

-- @id be9fd056c203 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/PaginationWithTiesTests.cs
SELECT id FROM scores FETCH FIRST 5 ROWS WITH TIES

-- @id ec14ff16ecb2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/PaginationWithTiesTests.cs
SELECT id, score FROM scores ORDER BY score DESC FETCH FIRST 5 ROWS WITH TIES

-- @id f730e8df4055 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/PaginationWithTiesTests.cs
WITH TIES

-- @id 094917d9ac39 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT EXTRACT(year FROM ts) FROM orders

-- @id 498020b30830 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT EXTRACT("year) FROM secrets --" FROM ts) FROM orders

-- @id 5f10b16c40b8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT CAST(ts AS timestamp(3) with time zone) FROM orders

-- @id 9c0126dbc6d7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT CAST(id AS "varchar") FROM orders

-- @id a70987e8aa21 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT CAST(id AS "int) FROM secrets --") FROM orders

-- @id c1d2a200b39b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT CAST(amount AS decimal(10, 2)) FROM orders

-- @id f817116fccc3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT CAST(x AS double precision) FROM orders

-- @id fca37f3fc9f9 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SafeTokenEmissionSql3Tests.cs
SELECT EXTRACT(foo FROM ts) FROM orders

-- @id 0c80a57c9e67 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT position('a' IN name) FROM orders

-- @id 3c2c2e2e60d4 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT trim(name) FROM orders

-- @id 588720bd45e3 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT id FROM orders WHERE created_at > current_timestamp(3)

-- @id 80b6ff42a34f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT substring(name FROM 2 FOR 3) FROM orders

-- @id b94a945ed7c6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT id FROM orders WHERE created_at > current_date - INTERVAL '7' DAY

-- @id bfe4e6117908 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT id FROM orders WHERE created_at > current_time

-- @id cfe58e7cef66 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT substring(name FROM (SELECT MIN(id) FROM customers)) FROM orders

-- @id e7b45f43c96c class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT trim(LEADING 'x' FROM name) FROM orders

-- @id fa6dd9bb0fe3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT substring(name FROM 2) FROM orders

-- @id fcd5a192cc16 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SpecialFormEmissionTests.cs
SELECT current_user FROM orders

-- @id 09d20ad2b2b0 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT Type::staticMethod() FROM t

-- @id 351e27d5df95 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT "col""name" FROM tbl

-- @id 5543e45cabcb class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT (obj).method() FROM t

-- @id 7b5aeffba271 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT *, u.* FROM users u

-- @id 7da36bf3f35a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT id, name AS full_name FROM users WHERE active = true

-- @id 7e87447ed1c2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT id FROM t1 UNION ALL SELECT id FROM t2 INTERSECT SELECT id FROM t3

-- @id a493cbc1c158 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT * FROM orders o LEFT JOIN customers c ON o.customer_id = c.id CROSS JOIN logs

-- @id c706a3e347c3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT * FROM orders JOIN customers USING (customer_id, tenant_id)

-- @id d0d7bc9dd1eb class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
WITH regional_sales AS (SELECT region, SUM(amount) AS total FROM orders GROUP BY region) SELECT * FROM regional_sales

-- @id ddf743edd66f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlAstBuilderTests.cs
SELECT id FROM tbl WHERE id = __param_user_id

-- @id 9f8bc61a2c19 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlServerProjectionAndNullSafeTests.cs
SELECT (SELECT MAX(id) FROM customers) AS m, amount > 2 AS f FROM orders

-- @id c1c042e926e7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlServerProjectionAndNullSafeTests.cs
SELECT CASE WHEN amount > 1 THEN 'x' END AS c, amount > 2 AS f FROM orders

-- @id c9a58fffe3c4 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlServerProjectionAndNullSafeTests.cs
SELECT o.id FROM orders o JOIN customers c USING (id)

-- @id f50ca26f5a09 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/SqlServerProjectionAndNullSafeTests.cs
SELECT id FROM orders WHERE a IS NOT DISTINCT FROM b

-- @id f87a58bc209a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/SqlServerProjectionAndNullSafeTests.cs
SELECT id FROM orders WHERE a IS DISTINCT FROM b

-- @id 5797b46b263a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/TargetDialectRowFilterTests.cs
SELECT id FROM orders WHERE id = 1 OR id = 2

-- @id d2ad85eba988 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/TargetDialectRowFilterTests.cs
SELECT [dept] FROM orders

-- @id 18325c8244bb class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE NULL = NULL

-- @id 22c8bb3bdf96 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE 'tenant_a' = 'tenant_a' AND id = 5

-- @id 24ffa4c33a47 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE NOT (id = 1)

-- @id 2d833cc857a6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE NOT (NOT (id = 1))

-- @id 2e14f44dd1db class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE id = 1 AND 1 = 1

-- @id 460e43d9945c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE id = 1 AND tenant_id = 't1'

-- @id 4f84759966b0 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE tenant_id = 't1' AND tenant_id = 't1'

-- @id 6a7c019ae429 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE 'tenant_a' = 'tenant_b' AND id = 5

-- @id 6c20efefa54e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE 1 = 1 AND tenant_id = 't1' AND tenant_id = 't1'

-- @id 7522fa760e48 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE id = 1 OR 1 = 2

-- @id 90a1399f8fbc class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE id = 1 OR 1 = 1

-- @id b059bfcaca78 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE 'val' IS NOT NULL AND id = 10

-- @id bcb113e8156d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE id = 1 AND 1 = 2

-- @id d833aa6567c7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/Visitors/AstSimplificationVisitorTests.cs
SELECT id FROM orders WHERE tenant_id = 't1' AND tenant_id = 't2'

-- @id 0b8b5ee53689 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT rank() OVER (ORDER BY amount) FROM orders

-- @id 14bb2cffa3e8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT COUNT(*) FILTER (WHERE amount > 100) FROM orders

-- @id 17e6072672e4 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM orders

-- @id 252ae8d4dc1b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT array_agg(id ORDER BY amount) FROM orders

-- @id 3f7f3a8754c4 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT id FROM orders WHERE created_at > DATE 'not-a-date'

-- @id 56c0397577a3 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT SUM(DISTINCT amount) FILTER (WHERE dept = 'x') FROM orders

-- @id 74d46e9fd7aa class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT SUM(amount) OVER (ORDER BY id GROUPS 1 PRECEDING) FROM orders

-- @id 7f18f5fa8136 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT SUM(amount) OVER (ORDER BY id ROWS BETWEEN (SELECT 1) PRECEDING AND CURRENT ROW) FROM orders

-- @id 9c097a9a9824 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT id FROM orders WHERE created_at < TIMESTAMP '2024-01-01 10:00:00'

-- @id a122d7f62703 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT id FROM orders WHERE created_at > JSON '{}'

-- @id be6d7265372d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT row_number() OVER (PARTITION BY dept ORDER BY amount DESC) FROM orders

-- @id d124b809b89b class=select expected=reject source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT max_by(id, amount) FILTER (WHERE dept = 'x') FROM orders

-- @id e8db9ad07e73 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT id FROM orders WHERE created_at > DATE '2024-01-01'

-- @id f974bd403ff6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT SUM(amount) OVER (ORDER BY id RANGE BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) FROM orders

-- @id fe2457a95fb3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Ast/WindowAggregateLiteralEmissionTests.cs
SELECT created_at + INTERVAL '1' DAY FROM orders

-- @id b67d7615319d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/ColumnBindTypeTests.cs
SELECT o.id FROM orders o JOIN entitlements e ON e.orderid = o.id

-- @id 1af4e62418eb class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT id, status FROM orders WHERE status = 'open'

-- @id 1b2dc96ee688 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT * FROM TABLE(system.query(query => 'SELECT 1'))

-- @id 2181a5d43d21 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT id FROM orders WHERE 1 = 1 OR status = 'x'

-- @id 2767d0c00f87 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT upper(email) FROM orders

-- @id 302266388783 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT a0.id FROM orders a0

-- @id 552df575ca67 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT id FROM orders WHERE status = 'a\b'

-- @id 59837efb17ae class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT * FROM mystery

-- @id 5e36cc980127 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT o.id FROM o JOIN entitlements e ON e.orderid = o.id

-- @id 69ff324be35c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT id FROM orders WHERE status = 'open'

-- @id 71338484f61b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
WITH o AS (SELECT id, region FROM orders)

-- @id 9ad5fbdc018e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT status FROM orders

-- @id e30d256a0dc8 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT o.id FROM orders o CROSS JOIN LATERAL (SELECT 1 AS x FROM entitlements e WHERE e.orderid = o.id) l

-- @id f04c6e22c3ea class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompileApiTests.cs
SELECT id FROM orders -- c

-- @id 128dc8645f5e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompileLimitsTests.cs
SELECT id FROM orders WHERE status = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'

-- @id 33b26ffb12e2 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CompileLimitsTests.cs
SELECT id FROM orders WHERE status = '${spark.sql.shuffle.partitions}'

-- @id 7473ab403f2a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CompiledSqlBinderTests.cs
SELECT COUNT(*) FROM t

-- @id 11b005573703 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH RECURSIVE "Orders" (id, n) AS (SELECT 1, 1 UNION ALL SELECT o.id, o.n + 1 FROM "Orders" o WHERE o.n < 3) SELECT id FROM "Orders"

-- @id 3119cee4770a class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH RECURSIVE orders (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT o.id, o.n + 1 FROM orders o WHERE o.n < 3) SELECT id FROM orders

-- @id 5730f78b10ab class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH RECURSIVE chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT c.id, c.n + 1 FROM chain c JOIN orders o ON o.id = c.id WHERE c.n < 3) SELECT id FROM chain

-- @id 582b869c9140 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH RECURSIVE chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT c.id, c.n + 1 FROM chain c WHERE c.n < 3) SELECT id, n FROM chain

-- @id 5c8156422c01 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH a AS (SELECT id FROM orders), b AS (SELECT id FROM a) SELECT id FROM b

-- @id 648cbdff45d7 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH chain (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT c.id, c.n + 1 FROM chain c WHERE c.n < 3) SELECT id, n FROM chain

-- @id 76ab04e7c444 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH "chain" AS (SELECT id FROM (SELECT id FROM "chain") x) SELECT id FROM "chain"

-- @id 867798bd7805 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH a AS (WITH a AS (SELECT 1 AS id) SELECT id FROM a) SELECT id FROM a

-- @id ad5cd4f566c0 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH "Chain" AS (SELECT 1 AS id), "chain" AS (SELECT 2 AS id) SELECT id FROM "chain"

-- @id b512b8ce3a74 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH orders AS (SELECT 1 AS id) SELECT orders.id FROM orders

-- @id cff25ceef2db class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH "Chain" AS (SELECT 1 AS id) SELECT id FROM (WITH "chain" AS (SELECT 2 AS id) SELECT id FROM "chain") t

-- @id dd4dbd59d106 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/CteShadowingTests.cs
WITH orders (id, n) AS (SELECT id, 1 FROM orders UNION ALL SELECT o.id, o.n + 1 FROM orders o WHERE o.n < 3) SELECT id FROM orders

-- @id 065a5b22d777 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT strpos(a, 'x') FROM t

-- @id 085bb7a90d7a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a, GROUPING(a) AS g FROM t GROUP BY ROLLUP (a)

-- @id 096ba0aa50c6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT id FROM t ORDER BY id FETCH FIRST 3 ROWS WITH TIES

-- @id 0e0f8dc65b8f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT `a` FROM `t` -- x

-- @id 16d7d716635d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT "${x}" FROM t

-- @id 1831c5a70f1c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT 'a' FROM `t`

-- @id 1eaf87477bb1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a IS DISTINCT FROM b AS d FROM t

-- @id 1f65887be60d class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT `a` FROM `t`;

-- @id 23bb7f5d7cd4 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT count(*) FILTER (WHERE a > 1) AS n FROM t

-- @id 26633fdc0096 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT current_date, current_timestamp FROM t

-- @id 2a25ce3004c3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a FROM t ORDER BY a DESC NULLS LAST, b ASC NULLS FIRST

-- @id 315aa641e973 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT TRY_CAST(a AS integer) FROM t

-- @id 377476118679 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT `a` FROM `t` WHERE `a` = ${x}

-- @id 3f26d0807caf class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT `it's -- ; ${x} 1`, `a``b` FROM `t`

-- @id 49957288e0e0 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a FROM t WHERE b = '${x}'

-- @id 49d77ad84c09 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a, SUM(b) OVER (PARTITION BY a ORDER BY c ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s FROM t

-- @id 4bacdc7f115c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT :p1

-- @id 4dfee1851816 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT CAST(a AS json) FROM t

-- @id 531286f8a0e3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT approx_distinct(a) FROM t

-- @id 55d8b8701c88 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT `a` FROM `t` WHERE `a` = :p1 AND `b` = :p2 AND `c` = :p1

-- @id 5abfb8de3642 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a || 'x' AS c, -a AS n, a % 2 AS m FROM t

-- @id 5ce7ea1d6aa0 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT id FROM t ORDER BY id OFFSET 5 LIMIT 10

-- @id 61e7b7751586 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT date_add('day', 3, d) AS a, date_trunc('month', d) AS t, now() AS n FROM t

-- @id 68d06369c2df class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT EXTRACT(year FROM d) AS y FROM t

-- @id 6c721b1a0dae class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a AS "x${y}" FROM t

-- @id 6c740c63eb28 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT `a` FROM `t` WHERE 1 = 1

-- @id 7cf821927dcf class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a FROM t WHERE a IN (1, 2, 3) AND b BETWEEN 1 AND 5 AND c LIKE 'a%'

-- @id 95594e60bda6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT o.id FROM o CROSS JOIN LATERAL (SELECT 1 AS x FROM p WHERE p.id = o.id) l

-- @id 9a27804f2cfe class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT length(a) FROM t

-- @id 9c3d9b03b9a4 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT CASE WHEN a > 1 THEN 'x' ELSE 'y' END AS c FROM t

-- @id a0e88945e39a class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT `a`

-- @id a5f122f738a8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT substring(a FROM 2 FOR 3) AS s, trim(BOTH 'x' FROM a) AS tr, position('a' IN b) AS p FROM t

-- @id ac04c6aeade8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT DATE '2024-01-15' AS d, TIMESTAMP '2024-01-15 10:30:00' AS ts FROM t

-- @id b9d47008406d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT t.a FROM t JOIN u ON t.id = u.id WHERE EXISTS (SELECT 1 FROM v WHERE v.id = t.id)

-- @id befa40ae571f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT "a" FROM `t`

-- @id cfa38c50f94d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT id FROM t WHERE a = 'x' AND b > 5 AND c = 'x'

-- @id d23d790d9265 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT arbitrary(a) FROM t

-- @id dcb332df90b6 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT a FROM t WHERE b = 'x'

-- @id e521e5d424ca class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
SELECT true AS a, false AS b, NULL AS c FROM t

-- @id e79390d34574 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DatabricksGeneratorTests.cs
WITH c AS (SELECT a FROM t) SELECT a FROM c UNION ALL SELECT a FROM c

-- @id b68bb2af533b class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DmlCoverageTests.cs
SELECT [a] FROM [t];

-- @id 0d414c658111 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "it's -- /* ; $$ [ ] 1", "a""b; 2" FROM "t""x"

-- @id 10f2bc59f023 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT 'a' FROM "t"

-- @id 23f32d15197d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "a" FROM "t" -- x

-- @id 26792e1abafb class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "a" /* x */ FROM "t"

-- @id 30ba07de6134 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "a" FROM "t" WHERE 1 = 1

-- @id 3559cbbeb088 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "a" FROM "t" WHERE "a" = $1 AND "b" = $2 AND "c" = $1

-- @id 563759a8686d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "a"[$1] FROM "t"

-- @id 6096b3b47b56 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT $1, $2

-- @id 7e7a1f0324ad class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT ARRAY[1, 2, 3][2] AS x FROM t

-- @id 9fdc2f666c35 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT CAST(a AS decimal(10, 2)) AS d, TRY_CAST(b AS integer) AS i, CAST(c AS varchar) AS v FROM t

-- @id a0a2ef5300f0 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT DATE '2024-01-15' AS d, TIMESTAMP '2024-01-15 10:30:00' AS ts, TIME '10:30:00' AS tm FROM t

-- @id a5750a98472b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT EXTRACT(year FROM d) AS y, EXTRACT(dow FROM d) AS w FROM t

-- @id ade856e08482 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "a" FROM "t";

-- @id b6b861cec0ad class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "abc FROM t

-- @id bb534fd54616 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT $$a$$

-- @id bdc7500ab189 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT $tag$a$tag$

-- @id cf7f72f0bf16 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT E'a'

-- @id d5e4d94d6991 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT "a"

-- @id e549e52f955b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/DuckDbGeneratorTests.cs
SELECT date_add('day', 3, d) AS a, date_trunc('month', d) AS t, d + INTERVAL '2' DAY AS i FROM t

-- @id 1ecf1803a13a class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [a] FROM [t] WHERE [a] IN (

-- @id 30b8bc052aec class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [a] /* x */ FROM [t]

-- @id 58a7e556b530 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [a] FROM [t] 

-- @id 708d6aa9ac8d class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [a] FROM [t] WHERE [a] = @p0

-- @id 7644449c8044 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [a] FROM [t] -- x

-- @id 967f87d8cbec class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [a] FROM [t] WHERE [a] = 1

-- @id 9fcbb59346af class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT N'a' FROM [t]

-- @id b4f749964e64 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [c1], x2 FROM [t3] WHERE [a] = @p12 AND @@DATEFIRST = @p12

-- @id b6dac037551e class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [a] FROM [t]

-- @id bef99ddc0271 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT 'a' FROM [t]

-- @id c5037e4436d8 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT "a" FROM [t]

-- @id cd4b66a19890 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [it's -- /* ; $$ " 1] FROM [t]]x; 2]

-- @id d19d538e1e69 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT [abc FROM t

-- @id ecb4719f6b93 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/EmittedSqlInvariantCheckerTests.cs
SELECT HASHBYTES('SHA2_256', [a]) FROM [t]

-- @id 54db7343fdc3 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/FunctionAllowlistTests.cs
SELECT some_extension_function(email) FROM orders

-- @id 5eb80c74cadd class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/FunctionAllowlistTests.cs
SELECT upper(email), coalesce(status, 'x'), count(*) FROM orders GROUP BY email, status

-- @id c857fcef7f86 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/FunctionAllowlistTests.cs
SELECT lower(email) FROM orders

-- @id c93cffc85a99 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/FunctionAllowlistTests.cs
SELECT count(*) FILTER (WHERE status = 'open') FROM orders

-- @id a6e9346a6364 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralDedupTests.cs
SELECT id FROM orders WHERE amount = TIMESTAMP '2026-01-01 00:00:00.100' OR amount = TIMESTAMP '2026-01-01 00:00:00.200'

-- @id 00d44fff8e98 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT substring(a FROM 2 FOR 3) AS s, substring(a FROM 2) AS s2 FROM t

-- @id 098d86c5c9b7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT count(*) AS n, count(DISTINCT a) AS d FROM t

-- @id 1070185408ee class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT trim(a) AS x, trim(BOTH 'x' FROM a) AS y, position('a' IN b) AS z FROM t

-- @id 146efa071f77 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT TRY_CAST(a AS integer) AS i, CAST(a AS timestamp) AS ts, CAST(a AS boolean) AS b, CAST(a AS double) AS d FROM t

-- @id 1e1aeee4554d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT TRUE AS x, FALSE AS y, NULL AS z FROM t

-- @id 208562cf8a98 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT a, b, GROUPING(a, b) AS g FROM t GROUP BY CUBE (a, b)

-- @id 420162e872eb class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT a FROM t ORDER BY a DESC NULLS FIRST, b ASC NULLS LAST

-- @id 4621a54d8e68 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT current_date, current_timestamp, localtimestamp FROM t

-- @id 56d4dbc440a5 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT id FROM t WHERE a IS DISTINCT FROM b

-- @id 574b36514b40 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT -a AS n, a % 2 AS m, a || 'x' AS c FROM t

-- @id 6d594d5bc3e7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT a FROM t WHERE true AND NOT false

-- @id 7752fdbe67b2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT id FROM t WHERE a = 'x' OR b = 'x' OR c = 1 OR d = '1'

-- @id 8c43d0c0ea1a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT CASE WHEN a > 1 THEN 'x' ELSE 'y' END AS c, a > 2 AS f FROM t

-- @id 9b0567bc72c2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT EXTRACT(DOW FROM d) AS dw, EXTRACT(week FROM d) AS wk FROM t

-- @id a58c103745d2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT a FROM t ORDER BY a FETCH FIRST 3 ROWS WITH TIES

-- @id ac755b5b58bd class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT a, ROW_NUMBER() OVER (PARTITION BY b ORDER BY c) AS rn FROM t

-- @id bb9c77c69b0e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT id FROM t WHERE 1 = 0

-- @id bde7a1beb95a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT id FROM t WHERE name = 'O''Brien'

-- @id da1bcf0b95a6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT a, SUM(b) OVER (PARTITION BY a ORDER BY c ROWS BETWEEN 2 PRECEDING AND CURRENT ROW) AS s,

-- @id e7b2924f6bad class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT id FROM t WHERE a > 5 AND b < 9999999999 AND c = 1.50

-- @id f61bb60a8d17 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/LiteralParameterizationTests.cs
SELECT id FROM t WHERE d = DATE '2024-01-15' AND ts > TIMESTAMP '2024-01-15 10:30:00'

-- @id a456ab1f0bf5 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/MaskTests.cs
SELECT email FROM orders

-- @id 08428491fb5f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT a > 1 AS f, CASE WHEN a > 1 THEN 'x' ELSE 'y' END AS c FROM t WHERE true

-- @id 0b566bf3704c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT EXTRACT(year FROM d) AS y, EXTRACT(week FROM d) AS w FROM t

-- @id 4b13cfcb2068 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT :p2, :p1 FROM DUAL

-- @id 5395ceeefdd2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT date_add('day', 3, d) AS a, date_add('month', 2, d) AS m, date_trunc('month', d) AS t FROM t

-- @id 54ce78a915f0 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT "A" FROM "T" -- x

-- @id 5a203d4e24fb class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT :p1 FROM "DUAL"

-- @id 60134c1a1f44 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT q'[a]' FROM "T"

-- @id 71f1d3a515ad class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT :p1 FROM DUAL

-- @id 7c5e601d05a3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT CAST(a AS decimal(10, 2)) AS d, CAST(b AS double) AS i, CAST(c AS varchar) AS v FROM t

-- @id 812cd6dbad4e class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT "A" FROM "T" WHERE "A" = :p1 AND "B" = :p2 AND "C" = :p1

-- @id b4399ea3496e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT 'a' FROM "T"

-- @id d3abf98a54d8 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT "A" FROM "T" WHERE 1 = 1

-- @id f5ea9c3de7ae class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT "A" FROM "T";

-- @id fd358bd875f4 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/OracleGeneratorTests.cs
SELECT nq'[a]' FROM "T"

-- @id a6064d7f32f7 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/ParameterAccountingTests.cs
SELECT id FROM t WHERE a = ?

-- @id d754cfdc1736 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/ParameterAccountingTests.cs
SELECT id FROM t WHERE a IN (

-- @id d8c0c9c9b059 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/ParameterAccountingTests.cs
SELECT id FROM t WHERE a = __param_foo

-- @id fd1c1e2e54f8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/ParameterAccountingTests.cs
SELECT id FROM t WHERE a = __param_foo OR b = __param_foo

-- @id ba75be87ba5c class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/PipelineSeamTests.cs
SELECT 'smuggled',

-- @id 8739985d6f7e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/PostgreSqlGeneratorTests.cs
SELECT count(*) FILTER (WHERE a > 1) AS n, array_agg(a ORDER BY b) AS xs FROM t

-- @id f8e2d2a1f51e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/PostgreSqlGeneratorTests.cs
SELECT CAST(a AS decimal(10, 2)) AS d, CAST(b AS double) AS i, CAST(c AS varchar) AS v, CAST(d AS varbinary) AS x FROM t

-- @id 294d129e7a08 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/SqlServerFunctionMappingTests.cs
SELECT ceil(a) FROM t

-- @id 64c28f363c0f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/SqlServerFunctionMappingTests.cs
SELECT char_length(a) FROM t

-- @id f4d4edde8c77 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/SqlServerFunctionMappingTests.cs
SELECT upper(a) FROM t

-- @id 6a7e4e1b3adf class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/TableCatalogResolutionTests.cs
SELECT id FROM s.t

-- @id dc45dd6726be class=select expected=reject source=tests/TrinoSqlEngine.Tests/Compiler/TableCatalogResolutionTests.cs
SELECT id FROM main.s.t

-- @id eebe4d7b3259 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/TableCatalogResolutionTests.cs
SELECT id FROM dev.s.t

-- @id 3c8fcee0b3d9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/TypedInjectionTests.cs
SELECT o.id FROM orders o JOIN lookup l ON l.code = o.status

-- @id 3ed64ce27150 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/TypedInjectionTests.cs
SELECT code FROM lookup

-- @id a9f53b6b3d4e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/TypedInjectionTests.cs
WITH c AS (SELECT id FROM orders) SELECT id FROM c

-- @id e24e0e22b93f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Compiler/TypedInjectionTests.cs
SELECT id FROM orders WHERE status = 'x'

-- @id 64380c02437e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Differential/LegacyVsAstDifferentialTests.cs
SELECT id, name FROM users WHERE tenant_id = @tenant_id AND status = @status

-- @id 7d9edaba3dab class=select expected=accept source=tests/TrinoSqlEngine.Tests/Differential/LegacyVsAstDifferentialTests.cs
SELECT * FROM products

-- @id e926fed7c3df class=select expected=accept source=tests/TrinoSqlEngine.Tests/Differential/LegacyVsAstDifferentialTests.cs
SELECT count(*) FROM items GROUP BY category

-- @id f0ddcd038e2b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Differential/LegacyVsAstDifferentialTests.cs
SELECT id, amount FROM orders WHERE amount > 50

-- @id 7e77db59288b class=select expected=accept source=tests/TrinoSqlEngine.Tests/DmlLimitAndAliasSql78Tests.cs
SELECT "Orders".id FROM "Orders"

-- @id b687eca138a2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/DmlLimitAndAliasSql78Tests.cs
SELECT orders.id FROM orders

-- @id 01d29b988483 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 FETCH NEXT ROW WITH TIES

-- @id 03857ff662bc class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 UNION DISTINCT CORRESPONDING BY (x) SELECT 123 UNION ALL CORRESPONDING SELECT 123

-- @id 07c0c5e9a7fe class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT INCLUDING, EXCLUDING, PROPERTIES FROM t

-- @id 098ab5aaf340 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT col1.f1, col2, col3.f1.f2.f3 FROM table1

-- @id 09dfc8a836cf class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT ROW (1, 'a', true).* AS (f1, f2, f3)

-- @id 0cbba67fd740 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT NOT EXISTS(SELECT 1) = EXISTS(SELECT 2)

-- @id 0e31fce9b0ed class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT pivot FROM t

-- @id 12353ac509b7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t FULL JOIN LATERAL (VALUES 1) ON true

-- @id 128386519676 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY a

-- @id 15c620ba5fc9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT *                 FROM TABLE(some_ptf(                    input => TABLE(orders) AS copartition(a, b, c)))

-- @id 16eb9fc1d7da class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT EXISTS(SELECT 1)

-- @id 1a8fffd5b3f1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY DISTINCT GROUPING SETS ((a, b), (a), ()), CUBE (c), ROLLUP (d)

-- @id 1aedfa03ea91 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY a, b

-- @id 1b466d89ed22 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT SUM(x) FILTER (WHERE x > 4)

-- @id 1ce71fe21414 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 LIMIT 2

-- @id 1cfefd1544c3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT EXISTS(SELECT 1) = EXISTS(SELECT 2)

-- @id 1d67f9aab644 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT r.* FROM t

-- @id 2131d5abe695 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT ALL, SOME, ANY FROM t

-- @id 23208521524f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT OVERLAY('abcdef' PLACING 'XY' FROM 3 FOR 2)

-- @id 257023caf3e2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY ALL AUTO

-- @id 26228bdfdc0d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM a, b

-- @id 2a7a4e319236 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t CROSS JOIN UNNEST(a)

-- @id 2d66527960eb class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t FOR VERSION AS OF 'version1'

-- @id 2eb9b5750602 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT id FROM public.orders

-- @id 3814dda74166 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT *                 FROM trades                 LEFT JOIN NEAREST (                     FROM quotes                     WHERE quotes.symbol = trades.symbol                     MATCH quotes.ts <= trades.ts                 ) ON TRUE

-- @id 3a09bfa89e7e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 OFFSET 2

-- @id 3b20f72d2443 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 FETCH FIRST 2 ROWS WITH TIES

-- @id 3d006953e920 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT a, b, GROUPING(a, b) FROM table1 GROUP BY GROUPING SETS ((a), (b))

-- @id 3edd47923087 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT substring('t', 2)

-- @id 40d1d3d4b9c1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM TABLE(some_ptf(                     arg1 => TABLE(orders) AS ord(a, b, c)                         PARTITION BY a                         PRUNE WHEN EMPTY                         ORDER BY b ASC NULLS LAST,                     arg2 => CAST(NULL AS DESCRIPTOR),                     arg3 => DESCRIPTOR(x integer, y varchar),                     arg4 => 5,                     'not-named argument'                     COPARTITION (ord, nation)))

-- @id 40d6dc606e73 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123.456E7 FROM DUAL

-- @id 55e6ea4bd86b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM JSON_TABLE(col, 'lax $' AS customer COLUMNS(                     NESTED PATH 'lax $.cust_status[*]' AS status COLUMNS(                        status varchar PATH 'lax $.type',                        fresh boolean PATH 'lax &.new'),                     NESTED PATH 'lax &.cust_comm[*]' AS comment COLUMNS(                        comment varchar PATH 'lax $.text'))                     PLAN (customer OUTER (status CROSS comment))                     ERROR ON ERROR)

-- @id 56e4144f7f84 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM (VALUES (1, '1'), (2, '2')) OFFSET 2

-- @id 57696b02337d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 UNION DISTINCT SELECT 123 UNION ALL SELECT 123

-- @id 5bbdd6b29b7e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t FOR TIMESTAMP AS OF TIMESTAMP '2021-03-01 00:00:01'

-- @id 5ca5aa319d0d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 INTERSECT DISTINCT CORRESPONDING BY (x) SELECT 123 INTERSECT ALL CORRESPONDING SELECT 123

-- @id 5eab93603dcf class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t PIVOT (sum(amount) FOR month IN (1, 2))

-- @id 603b96b414b8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 ORDER BY a

-- @id 653aefba23d8 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 EXCEPT DISTINCT CORRESPONDING SELECT 123 EXCEPT ALL CORRESPONDING SELECT 123

-- @id 69d50ef1a8ed class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT substring('t' FROM 2)

-- @id 6a336ed4a148 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT id FROM "public"."order"

-- @id 6c20a68ebebc class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM TABLE(some_ptf(input => 1))

-- @id 6d2fbaff694c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
WITH SESSION                    key = 'value'                 WITH                    FUNCTION foo()                    RETURNS bigint                    RETURN 42                 SELECT 1

-- @id 6ee614a57405 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t PIVOT (sum(amount) AS total, avg(amount) AS mean FOR month IN (1 AS jan))

-- @id 71ec932edc1e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 EXCEPT DISTINCT SELECT 123 EXCEPT ALL SELECT 123

-- @id 78150a19efa7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT substring('t', 2, 3)

-- @id 7eccae1a2e0b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY AUTO

-- @id 7f875a0ab104 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t CROSS JOIN LATERAL (VALUES 1)

-- @id 80b8d2607617 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
WITH a (t, u) AS (SELECT * FROM x), b AS (SELECT * FROM y) TABLE z

-- @id 80d5942dea95 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT TIMESTAMP '2012-10-31 01:00 UTC' AT TIME ZONE 'America/Los_Angeles'

-- @id 815c14b842fb class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY GROUPING SETS (a)

-- @id 81b00b40112e class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT TIMESTAMP '2012-10-31 01:00 UTC' AT LOCAL

-- @id 84590fe73f02 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT *                 FROM TABLE(some_ptf(                     input1 => TABLE(customers) PARTITION BY nationkey,                     input3 => TABLE(lineitem),                     input2 => TABLE(nation) PARTITION BY nationkey                     COPARTITION(customers, nation)))

-- @id 84a56608d848 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT substring('t' FROM 2 FOR 3)

-- @id 85daa5dd3f91 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT (NOT EXISTS(SELECT 1)) = EXISTS(SELECT 2)

-- @id 86e7da4b9b55 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 FETCH NEXT ROW ONLY

-- @id 8ac3bcb409ef class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM (VALUES ('a', 1, 2.2e0), ('b', 2, 3.3e0))

-- @id 8c199941fc0a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 INTERSECT DISTINCT SELECT 123 INTERSECT ALL SELECT 123

-- @id 8c9cc3cb2652 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 EXCEPT DISTINCT CORRESPONDING BY (x) SELECT 123 EXCEPT ALL CORRESPONDING SELECT 123

-- @id 8f03100743d4 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
WITH RECURSIVE a AS (SELECT * FROM x) TABLE y

-- @id 8fd271e40faa class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM TABLE(some_ptf(input => TABLE(orders) AS ord))

-- @id 9096b4cbdaec class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM (VALUES (1, '1'), (2, '2')) OFFSET 2 ROWS

-- @id 93fc4eb30d09 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 FETCH FIRST 2 ROWS ONLY

-- @id 980f96d56430 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT *                 FROM trades                 CROSS JOIN NEAREST (                     FROM quotes                     WHERE quotes.symbol = trades.symbol                     MATCH quotes.ts <= trades.ts                 )

-- @id 9901f21d71cf class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM (VALUES (1, '1'), (2, '2')) FETCH FIRST ROW WITH TIES

-- @id 991aad856339 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY ALL GROUPING SETS ((a, b), (a), ()), CUBE (c), ROLLUP (d)

-- @id 9f8a65f411d6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 UNION DISTINCT CORRESPONDING SELECT 123 UNION ALL CORRESPONDING SELECT 123

-- @id a19830a41cf6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t CROSS JOIN UNNEST(a, b) WITH ORDINALITY

-- @id a673a6ec807a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT id FROM "public"."order""2"

-- @id a9030eb474bc class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT *                 FROM TABLE(some_ptf(                     input => TABLE(orders) "COPARTITION"(a, b, c)))

-- @id ada72d3f0ce2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT col1.f1[0], col2, col3[2].f2.f3, col4[4] FROM table1

-- @id b1d15e40aee7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM JSON_TABLE(col, 'lax $' COLUMNS(ordinal_number FOR ORDINALITY, customer_name varchar PATH 'lax $.cust_no' DEFAULT 'anonymous' ON EMPTY null ON ERROR, customer_countries varchar FORMAT JSON PATH 'lax.cust_ctr[*]' WITH WRAPPER KEEP QUOTES null ON EMPTY ERROR ON ERROR,customer_regions varchar FORMAT JSON ENCODING UTF16 PATH 'lax.cust_reg[*]' EMPTY ARRAY ON EMPTY EMPTY OBJECT ON ERROR) EMPTY ON ERROR)

-- @id b49daa55ca46 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM (VALUES (1, '1'), (2, '2')) FETCH FIRST ROW ONLY

-- @id b533d7c166bc class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t PIVOT (sum(amount) FOR (region, month) IN (('NA', 1) AS na_jan) GROUP BY region)

-- @id b8db52a6cfc9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY ()

-- @id bb856faa99f9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t, LATERAL (VALUES 1) a(x)

-- @id bda46420a7dd class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT 123 INTERSECT DISTINCT CORRESPONDING SELECT 123 INTERSECT ALL CORRESPONDING SELECT 123

-- @id bdd379b02821 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t

-- @id c515d7164d31 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t PIVOT (sum(amount) FOR month IN (1)) AS p (r, jan)

-- @id c897f63244a9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT ROW (1, 'a', true).*

-- @id c9f794503638 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM t FULL JOIN UNNEST(a) AS tmp (c) ON true

-- @id ceb78524498d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT zone FROM t

-- @id d34792cc6099 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM (VALUES (1, '1'), (2, '2')) LIMIT ALL

-- @id d3a13698794d class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT CAST(ROW(11, 12) AS ROW(COL0 INTEGER, COL1 INTEGER)).col0

-- @id d59d2a2395f3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM a CROSS JOIN b NATURAL JOIN c CROSS JOIN d NATURAL JOIN e

-- @id d7989d8b33af class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT array_agg(x ORDER BY t.y) FROM t

-- @id d978e4d0fb01 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM T WINDOW someWindow AS (PARTITION BY a), otherWindow AS (someWindow ORDER BY b)

-- @id dd1dbffc9533 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT *                 FROM TABLE(some_ptf(                    input => TABLE(SELECT * FROM orders copartition(a, b, c))))

-- @id e38a9a2edeb0 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT *                 FROM trades,                      NEAREST (                          FROM quotes                          WHERE quotes.symbol = trades.symbol                          MATCH quotes.ts <= trades.ts                      )

-- @id e96556229d18 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 OFFSET 2 ROWS

-- @id eb84aaf5cd37 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 GROUP BY DISTINCT AUTO

-- @id f41a2db71b5b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT OVERLAY('abcdef' PLACING 'XY' FROM 3)

-- @id f95b35161448 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT A.*

-- @id f9813e558cb2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM table1 LIMIT ALL

-- @id fa49d9087c5f class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM a CROSS JOIN b LEFT JOIN c ON true

-- @id fa87d97f578b class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
WITH SESSION                    key = 'value',                    catalog.key2 = DECIMAL '10.0'                 SELECT 1

-- @id fe26e993efd3 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements.json
SELECT * FROM TABLE(some_ptf(input => TABLE(orders) AS ord(a, b, c)))

-- @id 0953876a497f class=select expected=reject source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements_invalid.json
WITH FUNCTION abc() RETURNS int RETURN 42, SESSION query_max_memory = '1GB' SELECT 1

-- @id 0fcfa78a0d2f class=select expected=reject source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements_invalid.json
WITH WITH SESSION query_max_memory = '1GB' SELECT 1

-- @id 151d31dd13c8 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements_invalid.json
WITH SESSION query_max_memory = '1GB', FUNCTION abc() RETURNS int RETURN 42, SESSION query_max_total_memory = '1GB' SELECT 1

-- @id 8a07e7311dd9 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Fixtures/trino_statements_invalid.json
WITH SESSION query_max_memory = '1GB', WITH SESSION query_max_total_memory = '1GB' SELECT 1

-- @id 03ec6cf59b44 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT sys_context('USERENV', 'ISATT') FROM orders

-- @id 06cf7f28f4fc class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT id FROM orders WHERE amount > 100

-- @id 0b9fed3a4d4b class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT E'abc' FROM orders

-- @id 0fe74f649138 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT mytype::f(1) FROM orders

-- @id 14f0b6d62e50 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT 1 WHERE

-- @id 1581d72a7d22 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM orders FOR TIMESTAMP AS OF TIMESTAMP '2026-01-01 00:00:00'

-- @id 19496724f2f9 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT ts_stat('SELECT 1'), id FROM orders

-- @id 1a6a7b22be72 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT dbms_lock.sleep(5)

-- @id 26de1bbfaeff class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT ora_hash('test')

-- @id 26f6856cb85f class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
WITH "sales.orders" AS (SELECT 1 x) SELECT * FROM sales.orders

-- @id 2858958ce952 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM customers ORDER BY 1 FETCH FIRST 5 ROWS WITH TIES

-- @id 2b14d0fd8287 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
WITH x AS (SELECT * FROM customers LIMIT 5) SELECT * FROM x

-- @id 2fa3cebdc4f9 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM orders WHERE id IN (

-- @id 3078ffae2594 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT (name).f(1) FROM orders

-- @id 31acba776736 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT doc.value('/a', 'int') FROM orders

-- @id 378252d42ca5 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM orders FOR VERSION AS OF 1

-- @id 45290f23e847 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT "QUERY_TO_XML"('SELECT 1', true, false, '')

-- @id 458c9be73f2f class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
WITH "sales.orders" AS (SELECT 1 x) SELECT * FROM "sales.orders"

-- @id 4694f0f91bd6 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT upper(name) FROM orders

-- @id 46e905430faf class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT table_to_xml('hr.salaries', true, false, '')

-- @id 471337d19017 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT id FROM orders /* hidden */

-- @id 4850127c2c09 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM (

-- @id 4e93148a0f85 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT id FROM orders WHERE id IN (SELECT database_to_xml(true, false, ''))

-- @id 515030828d52 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
WITH SESSION key = 'value' SELECT 1

-- @id 5b537acaf6d1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
WITH "Orders" AS (SELECT 1 x) SELECT * FROM orders

-- @id 5b9aab7a9238 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT dbms_pipe.receive_message('pipe', 5)

-- @id 60d386a02a42 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM TABLE(some_ptf(input => TABLE(orders)))

-- @id 6816e353797d class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT dblink('host=evil', 'SELECT * FROM hr.salaries')

-- @id 6a5926cecd10 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT md5(note) FROM orders

-- @id 7773821e6cdc class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT userenv('SCHEMA')

-- @id 7a013cc4414b class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM (SELECT "id, ssn AS id2", "na""me", substr(ssn, 1, 2) AS "ssn" FROM customers WHERE tenant_id = 't1')

-- @id 7a5a50a6c3bc class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT query_to_xml_and_xmlschema('SELECT * FROM hr.salaries', true, false, '')

-- @id 7c8df2e6f692 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM "sales.orders"

-- @id 7f4d06501887 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT set_config('app.tenant', 'victim', false)

-- @id 8d9057453979 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
WITH FUNCTION foo() RETURNS bigint RETURN 42 SELECT 1

-- @id 9046d4bf7596 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT dbms_random.value(0, 100)

-- @id 972a6be5ecee class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT id FROM orders -- comment

-- @id a5e914bc09d9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT pg_catalog.pg_read_file('/etc/passwd')

-- @id a6c2ce876155 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT lo_import('/etc/passwd')

-- @id aa7470e68849 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM WHERE

-- @id af32e05eaf19 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT current_setting('app.tenant')

-- @id b0ff676f67fb class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM customers WHERE EXISTS (SELECT 1 FROM orders LIMIT 1)

-- @id b15c3e6a5444 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT 'a\b' FROM orders

-- @id b3a6404c27a0 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT $$x$$ FROM orders

-- @id b489ebd8956f class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT load_extension('x'), id FROM orders

-- @id b8ae68984796 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT region, count(*), sum(amount), round(avg(amount), 2), date_trunc('month', created_at),

-- @id ba8395b4279b class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT id FROM orders -- note

-- @id cfe43e7fb837 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM "sales.orders" -- comment

-- @id d121842a012d class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT lower(name) FROM orders

-- @id d5e1d887ef48 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT 1 FROM t WHERE x = '

-- @id dca673d64b6e class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT x[1].f() FROM orders

-- @id de27983d619c class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM customers FETCH FIRST 5000 ROWS ONLY

-- @id e2ce80214638 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM customers WHERE id IN (SELECT id FROM orders LIMIT 10)

-- @id e87586c77824 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT util."normalize"(name), upper(name) FROM orders

-- @id e9e4ee1ed09a class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT upper(name), count(*) FROM orders GROUP BY upper(name)

-- @id ecff31f08d54 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM (SELECT * FROM customers LIMIT 3) c

-- @id edef0b3e8e4b class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT pg_sleep(100)

-- @id ef20d0e7a767 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT nvl(name, 'default'), trunc(sysdate) FROM orders

-- @id f5c3c2520bf2 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT * FROM TABLE(pg.system.query(query => 'SELECT * FROM orders'))

-- @id f9f5fce33d48 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityRemediationTests.cs
SELECT sys_context('USERENV', 'SESSION_USER')

-- @id 062b3070a088 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT 'hello\nworld' FROM orders

-- @id 09100acccbf1 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT e'test' FROM orders

-- @id 0ae96afb5e64 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT E'foo bar' FROM orders

-- @id 191f3ea488ae class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT * FROM srv.db.dbo.orders

-- @id 1b05e1ef07ed class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT 1 FROM orders -- end of line comment

-- @id 1ce5c1684e02 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT /* standard comment */ 1 FROM orders

-- @id 22f4dc7117ce class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT * FROM orders FOR TIMESTAMP AS OF '2026-01-01'

-- @id 267425fa8356 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT E'hello' FROM orders

-- @id 344ecec6a35d class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT txid_current()

-- @id 3bb69f1bd76a class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT id FROM orders LIMIT 20

-- @id 402fa0c00fd4 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT /* /* double */ */ 1 FROM orders

-- @id 4773bbae5a0a class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT has_dbaccess('master')

-- @id 6ef0b5929a22 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT pg_get_expr('1', 1)

-- @id 70379360d6ec class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT /* outer /* nested */ comment */ 1 FROM orders

-- @id 7cb077c0bb0f class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT pg_stat_reset()

-- @id 8e9f139a801b class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT * FROM "my.table"

-- @id 912c7f8fa0c1 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT '\' FROM orders

-- @id 9426f97264b3 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT * FROM orders FOR VERSION AS OF 100

-- @id a08bb3c0ad09 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT 'O''Reilly' FROM orders

-- @id a4fd4e225570 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT 'abc\'def' FROM orders

-- @id aafc01bdb169 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT is_srvrolemember('sysadmin')

-- @id ac7cbbd6fc71 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT suser_sname()

-- @id b320eec417e6 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
select * from test_ß_ä_ı

-- @id b34919c0786f class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT fn_dblog(NULL, NULL)

-- @id c3604080a819 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT pg_notify('chan', 'payload')

-- @id c80c1be44c56 class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT * FROM server.database.schema.table

-- @id dba682075e47 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT id, name FROM orders ORDER BY id

-- @id de1d3d05a986 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT inet_server_addr()

-- @id df1f1d15d442 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT version()

-- @id f69a796a99df class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT $$line1
line2$$ FROM orders

-- @id f8e21c279bae class=select expected=reject source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT $$dollar string$$ FROM orders

-- @id feeece7907cf class=select expected=accept source=tests/TrinoSqlEngine.Tests/SecurityReview20261002SqTests.cs
SELECT * FROM mycatalog.myschema.mytable

-- @id 2971a560a09a class=select expected=accept source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT tags[1], ARRAY[1,2] FROM orders

-- @id 2a9352388063 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT 'abc' FROM orders

-- @id 3f2218a33abe class=select expected=accept source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT 'a--b' FROM orders

-- @id 6f29852717f2 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT '[' , x FROM orders WHERE a = ']--'

-- @id 73032a5599d8 class=select expected=reject source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT [id] FROM orders

-- @id 7791bbab27ec class=select expected=reject source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT "a[b" FROM orders

-- @id 783a62786376 class=select expected=accept source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT 'a/*b' FROM orders

-- @id 8ab14a62b01c class=select expected=accept source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT '[' || x || ']' FROM orders

-- @id c46a8f4573aa class=select expected=accept source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT ARRAY[1,2][1] FROM orders

-- @id fc740341d70e class=select expected=reject source=tests/TrinoSqlEngine.Tests/Sql1BracketLexerDifferentialTests.cs
SELECT U&'d!0061t' UESCAPE '!' FROM orders

-- @id 97b3e186e61d class=select expected=accept source=tests/TrinoSqlEngine.Tests/SqlParameterExtractorTests.cs
SELECT c.id, c.company_name, SUM(o.amount) AS total
            FROM customers c
            JOIN orders o ON c.id = o.customer_id
            WHERE c.country = @country
              AND o.amount >= @minAmount
            GROUP BY c.id, c.company_name;

-- @id 9f894603b9ef class=select expected=accept source=tests/TrinoSqlEngine.Tests/SqlParameterExtractorTests.cs
SELECT id, name, revenue
            FROM customers
            WHERE country = @country 
              AND created_at >= @fromDate 
              AND status = {{status}};

-- @id f90a486a8e96 class=select expected=accept source=tests/TrinoSqlEngine.Tests/SqlParameterExtractorTests.cs
SELECT * FROM orders WHERE id = @orderId AND total >= {{minTotal}}

-- @id 1449415f14fb class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT id, amount FROM (SELECT * FROM orders WHERE tenant_id = 42) WHERE amount > 100

-- @id 35f84a182607 class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT * FROM "my_schema"."orders"

-- @id 4b1483d660cf class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
WITH a AS (SELECT * FROM (SELECT * FROM orders WHERE tenant_id = 42)), b AS (SELECT * FROM a) SELECT * FROM b

-- @id 678ae7c98442 class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT * FROM TABLE(my_ptf(TABLE((SELECT * FROM orders WHERE tenant_id = 42))))

-- @id 71fa085f15f9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT * FROM orders JOIN audit_log ON orders.id = audit_log.order_id

-- @id 8801ef59eb8c class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT * FROM TABLE(my_ptf(TABLE(orders)))

-- @id 8db6286406f5 class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT orders.id FROM (SELECT * FROM orders WHERE tenant_id = 42) AS orders

-- @id ca57508f4b6d class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT * FROM (SELECT * FROM "orders" WHERE tenant_id = 42)

-- @id ec560b0922d1 class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
WITH a AS (SELECT * FROM orders), b AS (SELECT * FROM a) SELECT * FROM b

-- @id f258be6212c9 class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT id, amount FROM orders WHERE amount > 100

-- @id fabb97be5db7 class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
SELECT * FROM (SELECT * FROM "my_schema"."orders" WHERE tenant_id = 42)

-- @id fd10ed3fc3ee class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
WITH orders AS (SELECT 1 AS id) SELECT * FROM orders

-- @id fd1326fc1c9b class=select expected=accept source=tests/TrinoSqlEngine.Tests/TrinoParserComplianceTests.cs
WITH orders AS (SELECT * FROM (SELECT * FROM orders WHERE tenant_id = 42)) SELECT * FROM orders

