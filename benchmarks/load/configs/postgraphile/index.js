const express = require('express');
const { postgraphile } = require('postgraphile');

const app = express();
const port = parseInt(process.env.PORT || '5001', 10);
const dbUrl = process.env.DATABASE_URL || 'postgres://postgres:REDACTED_HISTORICAL_BENCHMARK_SECRET@localhost:5432/postgres';

app.use(
  postgraphile(dbUrl, 'public', {
    watchPg: false,
    graphiql: false,
    enhanceGraphiql: false,
    disableQueryLog: true,
    dynamicJson: true,
    setofFunctionsContainNulls: false,
    legacyRelations: 'omit',
    retryOnInitFail: true,
    graphqlRoute: '/graphql',
    bodySizeLimit: '2mb',
    maxPoolSize: parseInt(process.env.PG_MAX_POOL || '50', 10),
  })
);

app.listen(port, '0.0.0.0', () => {
  console.log(`🚀 PostGraphile benchmark target ready at: http://0.0.0.0:${port}/graphql`);
});
