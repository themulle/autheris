# Test Fixtures

Test cases and SQL statements in this directory are extracted from the Trino project ([https://github.com/trinodb/trino](https://github.com/trinodb/trino)), licensed under the Apache License 2.0.

### Attribution
- **Original Source:** `core/trino-parser/src/test/java/io/trino/sql/parser/TestSqlParser.java`
- **Copyright:** Copyright (C) Trino contributors
- **License:** Apache License, Version 2.0 (see `../LICENSE` and `../THIRD_PARTY_NOTICES.md`)

### Contents
- `trino_statements.json`: Positive statement test cases for `singleStatement`.
- `trino_expressions.json`: Positive expression test cases for `standaloneExpression`.
- `trino_statements_invalid.json`: Negative syntax test cases for `singleStatement`.
- `trino_expressions_invalid.json`: Negative syntax test cases for `standaloneExpression`.
