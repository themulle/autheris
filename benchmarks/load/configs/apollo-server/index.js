import { ApolloServer } from '@apollo/server';
import { startStandaloneServer } from '@apollo/server/standalone';
import pg from 'pg';

const { Pool } = pg;

const pool = new Pool({
  connectionString: process.env.DATABASE_URL,
  max: parseInt(process.env.PG_MAX_POOL || '50', 10),
  idleTimeoutMillis: 30000,
});

const typeDefs = `#graphql
  type Artist {
    id: Int!
    name: String
  }

  type Genre {
    id: Int!
    name: String
  }

  type Track {
    id: Int!
    name: String!
    genre: Genre
  }

  type Album {
    id: Int!
    title: String!
    artist_id: Int!
    artist: Artist
    tracks: [Track!]!
  }

  input TitleFilter {
    _like: String
    _eq: String
  }

  input AlbumWhereInput {
    title: TitleFilter
  }

  type Query {
    albums(where: AlbumWhereInput): [Album!]!
    albums_by_pk(id: Int!): Album
  }
`;

const resolvers = {
  Query: {
    albums: async (_, { where }) => {
      let sql = 'SELECT id, title, artist_id FROM "albums"';
      const params = [];
      if (where?.title?._like) {
        params.push(where.title._like.replace(/%/g, '%'));
        sql += ` WHERE title ILIKE $1`;
      } else if (where?.title?._eq) {
        params.push(where.title._eq);
        sql += ` WHERE title = $1`;
      }
      sql += ' LIMIT 100';
      const res = await pool.query(sql, params);
      return res.rows;
    },
    albums_by_pk: async (_, { id }) => {
      const res = await pool.query('SELECT id, title, artist_id FROM "albums" WHERE id = $1 LIMIT 1', [id]);
      return res.rows[0] || null;
    },
  },
  Album: {
    artist: async (album) => {
      if (!album.artist_id) return null;
      const res = await pool.query('SELECT id, name FROM "artists" WHERE id = $1 LIMIT 1', [album.artist_id]);
      return res.rows[0] || null;
    },
    tracks: async (album) => {
      const res = await pool.query('SELECT id, name, genre_id FROM "tracks" WHERE album_id = $1', [album.id]);
      return res.rows;
    }
  },
  Track: {
    genre: async (track) => {
      if (!track.genre_id) return null;
      const res = await pool.query('SELECT id, name FROM "genres" WHERE id = $1 LIMIT 1', [track.genre_id]);
      return res.rows[0] || null;
    }
  }
};

const server = new ApolloServer({
  typeDefs,
  resolvers,
});

const port = parseInt(process.env.PORT || '4000', 10);
const { url } = await startStandaloneServer(server, {
  listen: { port, host: '0.0.0.0' },
});

console.log(`🚀 Apollo Server benchmark target ready at: ${url}`);
